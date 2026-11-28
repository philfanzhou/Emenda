using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    /// <summary>
    /// "不是错题": removes one pending incorrect question and its item from the current upload, then reclaims the
    /// crops and question-specific supplement pages nothing else references. Originals and other questions stay.
    /// The question is not remembered as excluded, so a later reanalysis may recognize it again.
    /// </summary>
    public async Task<ExtractionMistakeRemovalView> RemoveMistakeAsync(ExtractionAccess access, Guid id, Guid questionId,
        ExtractionMistakeRemovalRequest request, CancellationToken ct = default)
    {
        if (request is null || request.ExpectedVersion < 1 || request.ItemId == Guid.Empty || questionId == Guid.Empty
            || request.ExpectedReviewStatus != (int)ReviewStatus.PendingReview)
            throw new ExtractionException("invalid_request");
        // The caller sends no request key; the inspected state is the key, so a retry after a lost
        // response or an interrupted cleanup replays this removal instead of conflicting with it.
        var key = Payload.Identity($"remove-mistake:{id:N}:{questionId:N}:{request.ItemId:N}:{request.ExpectedVersion}");
        var hash = Payload.HashJson(new { operation = "remove_mistake", questionId, request });
        string studentId;
        int version;
        await using (var jobLock = await SerializeJobAsync(id, ct))
        await using (var db = Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The review endpoint confirms under this same lock: whichever commits first wins, and the other sees it.
            await LockAsync(db, id.ToString(), ct);
            var job = await FindAsync(db, access, id, ct);
            studentId = job.StudentId;
            var replay = await db.ExtractionRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.JobId == id && r.RequestKey == key, ct);
            if (replay is not null)
            {
                if (replay.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
                version = replay.Version;
                // Expedite only queued deletions; the collector rechecks every reference before deleting.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE extraction_object SET next_attempt=clock_timestamp() WHERE job_id={id} AND pending AND NOT legacy", ct);
            }
            else
            {
                await EnsureKeyAvailableAsync(db, id, key, ct);
                if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
                if (job.Status != "Succeeded") throw new ExtractionException("processing_in_progress", 409);
                var snapshot = await CurrentSnapshotAsync(db, job, ct);
                // Locks the item row: a confirmation committed before this point is seen, a later one finds no question.
                await RequirePendingAsync(db, job, snapshot, questionId, request.ItemId, ct);
                // A correct question collected on request is uncollected through the question PUT; removing it
                // would drop a correct judgment from the upload.
                if (snapshot.Questions.Single(q => q.Question.Id == questionId).Question.Outcome != "incorrect")
                    throw new ExtractionException("review_conflict", 409);
                var remaining = new ExtractionSnapshot(snapshot.Questions.Where(q => q.Question.Id != questionId).ToList());
                job.SourcesJson = Payload.Serialize(await RemainingSourcesAsync(db, job, questionId, remaining, ct));
                await db.ExtractionSupplements.Where(s => s.JobId == id && s.QuestionId == questionId).ExecuteDeleteAsync(ct);
                await db.MistakeItems.Where(i => i.Id == request.ItemId).ExecuteDeleteAsync(ct);
                // Publication retires the question row and queues every path the new version no longer references.
                await PublishRevisionAsync(db, job, remaining, snapshot, key, hash, access.Actor, "remove_mistake", "mistake_removal", ct);
                version = job.Version;
            }
            await tx.CommitAsync(ct);
        }
        await CollectJobAsync(id, ct);
        await using (var db = Open())
        {
            // Supplement pages still being uploaded are not this removal's cleanup.
            var staging = (await db.ExtractionSupplements.AsNoTracking().Where(s => s.JobId == id && s.Status == "Staging")
                .Select(s => s.SourcesJson).ToListAsync(ct))
                .SelectMany(s => Payload.Read<List<ExtractionSource>>(s).Select(p => p.Path)).ToArray();
            if (await db.ExtractionObjects.AnyAsync(o => o.JobId == id && o.Pending && !o.Legacy && !staging.Contains(o.Path), ct))
                throw new ExtractionException("cleanup_pending", 503);
        }
        return new(id, studentId, questionId, request.ItemId, version, CleanupComplete: true);
    }

    /// <summary>
    /// Supplement pages uploaded for this question leave the job unless a remaining question uses them or another
    /// question's supplement reused them. Upload originals are never removed.
    /// </summary>
    private static async Task<List<ExtractionSource>> RemainingSourcesAsync(MistakeDbContext db,
        ExtractionJobEntity job, Guid questionId, ExtractionSnapshot remaining, CancellationToken ct)
    {
        var rows = await db.ExtractionSupplements.AsNoTracking().Where(s => s.JobId == job.Id)
            .Select(s => new { s.QuestionId, s.SourcesJson }).ToListAsync(ct);
        var prefix = $"{JobStoragePrefix(job.Id)}supplements/";
        var owned = rows.Where(r => r.QuestionId == questionId)
            .SelectMany(r => Payload.Read<List<ExtractionSource>>(r.SourcesJson))
            .Where(s => s.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        owned.ExceptWith(rows.Where(r => r.QuestionId != questionId)
            .SelectMany(r => Payload.Read<List<ExtractionSource>>(r.SourcesJson)).Select(s => s.Path));
        var used = remaining.Questions.SelectMany(q => q.Question.Regions).Select(r => r.SourceId).ToHashSet();
        return Sources(job).Where(s => !owned.Contains(s.Path) || used.Contains(s.Id)).ToList();
    }
}
