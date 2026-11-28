using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    /// <summary>Asks for changes only; whatever the model leaves out is kept, so it need not restate the worksheet.</summary>
    public const string ReanalysisGuidance = "Apply only what the user's hint below asks for, using the original pages. " +
        "Return only the questions the hint changes or newly finds: keep the supplied id for an existing question, omit the id " +
        "only for a question not in the current list. Omit every other question; omitted questions stay exactly as they are. " +
        "User hint: ";

    /// <summary>
    /// "补充分析提示": one hint applied to the whole upload from its originals and current questions, published directly.
    /// Only what the hint supports changes; see <see cref="Rules.Reanalysis"/>. A result with no change keeps
    /// the version and records the key so that a retry replays the same receipt.
    /// </summary>
    public async Task<ExtractionReanalysisView> ReanalyzeAsync(ExtractionAccess access, Guid id,
        ExtractionReanalysisRequest request, CancellationToken ct = default)
    {
        var instruction = request?.Instruction?.Trim();
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 1
            || string.IsNullOrEmpty(instruction) || instruction.Length > 2000
            || request.AllowedSubjects is not { Count: > 0 } || request.AllowedSubjects.Any(s => s is < 1 or > 9))
            throw new ExtractionException("invalid_request");
        request = request with { Instruction = instruction, AllowedSubjects = request.AllowedSubjects.Distinct().Order().ToList() };
        var hash = Payload.HashJson(new { operation = "reanalyze", request });
        ExtractionJobEntity job;
        ExtractionSnapshot snapshot;
        HashSet<Guid> protectedIds;
        await using (var jobLock = await SerializeJobAsync(id, ct))
        await using (var db = Open())
        {
            job = await FindAsync(db, access, id, ct);
            if (await ReanalysisReplayAsync(db, job, request.RequestKey, hash, ct) is { } replay) return replay;
            await EnsureKeyAvailableAsync(db, id, request.RequestKey, ct);
            if (!options.Value.Enabled) throw new ExtractionException("extraction_disabled", 503);
            if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
            if (job.Status != "Succeeded") throw new ExtractionException("processing_in_progress", 409);
            snapshot = await CurrentSnapshotAsync(db, job, ct);
            var itemIds = snapshot.Questions.Where(q => q.ItemId.HasValue).Select(q => q.ItemId!.Value).ToArray();
            var pendingItems = (await db.MistakeItems.AsNoTracking()
                .Where(i => itemIds.Contains(i.Id) && i.ReviewStatus == (int)ReviewStatus.PendingReview)
                .Select(i => i.Id).ToListAsync(ct)).ToHashSet();
            // Reviewed (confirmed or rejected) and deleted items, and subjects the caller may not edit, never change here.
            protectedIds = snapshot.Questions.Where(q => q.ItemDeleted || q.ItemId is { } item && !pendingItems.Contains(item)
                || !request.AllowedSubjects.Contains(q.Question.Subject)).Select(q => q.Question.Id).ToHashSet();
        }
        // The model runs without the job lock; publication rechecks the version and every pending item it changes.
        var sources = Sources(job);
        var analyzed = await AnalyzeRevisionAsync(job.Grade, sources, snapshot.Questions.Select(q => q.Question).ToList(),
            ReanalysisGuidance + instruction, "all", [], ct);
        var key = request.RequestKey;
        var merged = Rules.Reanalysis(snapshot.Questions, protectedIds, request.AllowedSubjects.ToHashSet(),
            analyzed.Questions, sources, i => Payload.Identity($"reanalyze:{id:N}:{key:N}:{i}"));
        if (merged.Select(Payload.HashJson).SequenceEqual(snapshot.Questions.Select(q => Payload.HashJson(q.Question))))
            return await RecordUnchangedAsync(access, job, request, hash, ct);
        var changedItems = snapshot.Questions.Where(q => q.ItemId.HasValue && !q.ItemDeleted
                && merged.Single(m => m.Id == q.Question.Id) is var next && Payload.HashJson(next) != Payload.HashJson(q.Question))
            .Select(q => (q.Question.Id, q.ItemId!.Value)).ToList();
        ExtractionJobView result;
        try
        {
            result = await ReviseCoreAsync(access, id, new(key, request.ExpectedVersion, instruction, merged), null, ct,
                pending: new(hash, changedItems));
        }
        catch (ExtractionException)
        {
            try { await CollectJobAsync(id, CancellationToken.None); }
            catch (Exception error) { logger.LogWarning("Reanalysis cleanup deferred for {JobId} {ErrorType}", id, error.GetType().Name); }
            throw;
        }
        await CollectJobAsync(id, ct);
        return new(id, job.StudentId, key, result.Version);
    }

    private static async Task<ExtractionReanalysisView?> ReanalysisReplayAsync(MistakeDbContext db, ExtractionJobEntity job,
        Guid key, string hash, CancellationToken ct)
    {
        var revision = await db.ExtractionRevisions.AsNoTracking().Where(r => r.JobId == job.Id && r.RequestKey == key)
            .Select(r => new { r.PayloadHash, r.Version }).SingleOrDefaultAsync(ct);
        var unchanged = revision is null ? await db.ExtractionProposals.AsNoTracking()
            .Where(p => p.JobId == job.Id && p.RequestKey == key && p.Status == UnchangedStatus)
            .Select(p => new { p.PayloadHash, Version = p.ExpectedVersion }).SingleOrDefaultAsync(ct) : null;
        var found = revision ?? unchanged;
        if (found is null) return null;
        if (found.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
        return new(job.Id, job.StudentId, key, found.Version);
    }

    /// <summary>Reanalysis that changed nothing: a terminal, geometry-free row that only remembers the key and receipt.</summary>
    private const string UnchangedStatus = "Unchanged";

    private async Task<ExtractionReanalysisView> RecordUnchangedAsync(ExtractionAccess access, ExtractionJobEntity job,
        ExtractionReanalysisRequest request, string hash, CancellationToken ct)
    {
        await using var jobLock = await SerializeJobAsync(job.Id, ct);
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, job.Id.ToString(), ct);
        var current = await FindAsync(db, access, job.Id, ct);
        if (await ReanalysisReplayAsync(db, current, request.RequestKey, hash, ct) is { } replay) return replay;
        await EnsureKeyAvailableAsync(db, job.Id, request.RequestKey, ct);
        if (current.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
        db.ExtractionProposals.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, RequestKey = request.RequestKey, PayloadHash = hash,
            ExpectedVersion = current.Version, Status = UnchangedStatus, Attempts = 1, CreatedAt = await NowAsync(db, ct) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(job.Id, job.StudentId, request.RequestKey, current.Version);
    }
}
