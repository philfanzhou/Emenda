using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    public async Task<ExtractionDeletionView> DeleteAsync(ExtractionAccess access, Guid id, CancellationToken ct = default)
    {
        RequireDeletionAccess(access);
        await using var storage = await SerializeJobAsync(id, ct);
        await using var db = Open();
        var job = await db.ExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return await RequeueDeletionAsync(db, id, ct);

        var objectPaths = await ListJobObjectPathsAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Same order as submit. Admission is never held across a storage call.
        await LockAdmissionAsync(db, job.StudentId, ct);
        await LockAsync(db, $"extraction-source:{job.Caller}:{job.SourceKey}", ct);
        await LockAsync(db, id.ToString(), ct);
        await RegisterJobObjectsAsync(db, id, objectPaths, ct);
        job = await db.ExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            await tx.CommitAsync(ct);
            return await RequeueDeletionAsync(db, id, ct);
        }

        var sourceId = $"extract:{id:N}";
        // Review decisions and associated practice/FK writes must settle before choosing what stays.
        var items = await db.MistakeItems.FromSqlInterpolated($"""
            SELECT m.* FROM public.mistake_item m JOIN public.extraction_question q ON q.item_id=m.id
            WHERE q.job_id={id} AND m.source_upload_id={sourceId} AND m.student_id={job.StudentId}
                AND m.question_id=q.id::text ORDER BY m.id FOR UPDATE OF m
            """).ToListAsync(ct);
        var retained = items.Where(i => i.ReviewStatus == (int)ReviewStatus.Confirmed).ToList();
        var deleted = items.Where(i => i.ReviewStatus != (int)ReviewStatus.Confirmed).Select(i => i.Id).ToArray();
        var snapshot = await CurrentSnapshotAsync(db, job, ct);
        foreach (var item in retained.Where(i => i.AnalysisSnapshot is null))
        {
            var view = snapshot.Questions.SingleOrDefault(q => q.ItemId == item.Id && q.Question.Id.ToString("D") == item.QuestionId);
            if (view?.Question is not { Outcome: "incorrect" } question || item.StudentId != job.StudentId ||
                string.IsNullOrWhiteSpace(question.Statement) || string.IsNullOrWhiteSpace(question.StudentAnswer) ||
                string.IsNullOrWhiteSpace(question.ReferenceAnswer) || string.IsNullOrWhiteSpace(question.Evidence))
                throw new ExtractionException("retained_analysis_unavailable", 409);
            item.AnalysisSnapshot = SerializeAnalysis(question, job.Version);
        }
        await RetainFilesAsync(db, job, retained, ct);
        await db.SaveChangesAsync(ct);
        await db.MistakeItems.Where(i => deleted.Contains(i.Id)).ExecuteDeleteAsync(ct);

        // Cover older manifests and partially staged supplements whose PUT never started.
        var supplements = await db.ExtractionSupplements.Where(s => s.JobId == id).Select(s => s.SourcesJson).ToListAsync(ct);
        var paths = Sources(job).Select(s => s.Path)
            .Concat(supplements.SelectMany(s => ExtractionRules.Read<List<ExtractionSource>>(s).Select(p => p.Path)))
            .Concat(snapshot.Questions.SelectMany(q => q.Images).Select(c => c.Path))
            .Distinct(StringComparer.Ordinal).ToArray();
        var now = await NowAsync(db, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.extraction_object(path,job_id,pending,legacy,attempts,next_attempt)
            SELECT p,{id},false,false,0,{now} FROM unnest({paths}::text[]) p ON CONFLICT(path) DO NOTHING
            """, ct);
        // An explicit purge also collects the job's legacy retired versions, regardless of the periodic legacy flag.
        await db.ExtractionObjects.Where(o => o.JobId == id)
            .ExecuteUpdateAsync(update => update.SetProperty(o => o.Legacy, false).SetProperty(o => o.NextAttempt, now), ct);

        // Issue the insert outside SaveChanges' savepoint: the row guards compare xmin to the top-level transaction.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.extraction_deletion(job_id,admission_hash,source_binding_hash,owner_hash,deleted_at,deleted_mistakes,retained_confirmed)
            VALUES ({id},{job.ContentHash ?? job.PayloadHash},{SourceBindingHash(job.Caller, job.SourceKey)},
                {ExtractionRules.Hash(job.Caller)},{now},{deleted.Length},{retained.Count})
            """, ct);
        await db.ExtractionProposals.Where(r => r.JobId == id).ExecuteDeleteAsync(ct);
        await db.ExtractionSupplements.Where(r => r.JobId == id).ExecuteDeleteAsync(ct);
        await db.ExtractionRevisions.Where(r => r.JobId == id).ExecuteDeleteAsync(ct);
        await db.ExtractionQuestions.Where(q => q.JobId == id).ExecuteDeleteAsync(ct);
        await db.ExtractionJobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return await GetDeletionCoreAsync(id, ct);
    }

    private async Task<ExtractionDeletionView> RequeueDeletionAsync(MistakeDbContext db,
        Guid id, CancellationToken ct)
    {
        if (!await db.ExtractionDeletions.AnyAsync(d => d.JobId == id, ct))
            throw new ExtractionException("not_found", 404);
        await InventoryObjectsAsync(db, id, ct);
        // Only failed entries for a committed deletion receipt are expedited. The collector
        // still owns physical deletion and will recheck references before touching storage.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.extraction_object o SET next_attempt=clock_timestamp()
            WHERE o.job_id={id} AND o.pending AND o.attempts>0 AND o.next_attempt>clock_timestamp()
                AND EXISTS (SELECT 1 FROM public.extraction_deletion d WHERE d.job_id=o.job_id)
            """, ct);
        return await GetDeletionCoreAsync(id, ct);
    }

    public async Task<ExtractionDeletionView> GetDeletionAsync(ExtractionAccess access, Guid id, CancellationToken ct = default)
    {
        RequireDeletionAccess(access);
        await using var storage = await SerializeJobAsync(id, ct);
        await using var db = Open();
        if (!await db.ExtractionDeletions.AnyAsync(d => d.JobId == id, ct))
            throw new ExtractionException("not_found", 404);
        await InventoryObjectsAsync(db, id, ct);
        return await GetDeletionCoreAsync(id, ct);
    }

    private async Task<ExtractionDeletionView> GetDeletionCoreAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var receipt = await db.ExtractionDeletions.AsNoTracking().SingleOrDefaultAsync(d => d.JobId == id, ct)
            ?? throw new ExtractionException("not_found", 404);
        var objects = await db.ExtractionObjects.AsNoTracking().Where(o => o.JobId == id)
            .Select(o => new { o.Path, o.Attempts, o.NextAttempt }).ToListAsync(ct);
        var count = 0;
        var failed = false;
        if (objects.Count > 0)
        {
            var now = await NowAsync(db, ct);
            var paths = objects.SelectMany(o => StorageFamily(o.Path)).Distinct(StringComparer.Ordinal).ToArray();
            var referenced = (await RetainedReferencesAsync(db, paths, ct)).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in objects)
            {
                if (StorageFamily(entry.Path).Any(referenced.Contains)) continue;
                count++;
                failed |= entry.Attempts > 0 && entry.NextAttempt > now;
            }
        }
        return new(id, receipt.DeletedAt, receipt.DeletedMistakes, receipt.RetainedConfirmed, count,
            count == 0 ? "Completed" : failed ? "Failed" : "Pending");
    }

    private static void RequireDeletionAccess(ExtractionAccess access)
    {
        if (!access.Administrator) throw new ExtractionException("not_found", 404);
    }
}
