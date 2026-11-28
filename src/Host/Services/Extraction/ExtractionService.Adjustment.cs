using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    /// <summary>Pending items a publication changes; each must still be pending and bound when it commits.</summary>
    private sealed record PendingTarget(string Hash, List<(Guid QuestionId, Guid ItemId)> Items);

    /// <summary>
    /// Applies one natural-language instruction to a single pending question, directly and in place.
    /// Every original page is sent to the model; only the selected question may change.
    /// </summary>
    public async Task<ExtractionQuestionRevisionView> AdjustQuestionAsync(ExtractionAccess access, Guid id, Guid questionId,
        ExtractionAdjustmentRequest request, CancellationToken ct = default)
    {
        var instruction = request?.Instruction?.Trim();
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 1 || request.ItemId == Guid.Empty
            || questionId == Guid.Empty || string.IsNullOrEmpty(instruction) || instruction.Length > 2000)
            throw new ExtractionException("invalid_request");
        request = request with { Instruction = instruction };
        var hash = Payload.HashJson(new { operation = "adjust", questionId, request });
        ExtractionJobEntity job;
        ExtractionSnapshot snapshot;
        await using (var jobLock = await SerializeJobAsync(id, ct))
        await using (var db = Open())
        {
            job = await FindAsync(db, access, id, ct);
            // A lost response replays its receipt before any inference, so a later confirmation is never reset.
            var replay = await db.ExtractionRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.JobId == id && r.RequestKey == request.RequestKey, ct);
            if (replay is not null)
            {
                if (replay.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
                return QuestionResult(await ViewAsync(db, job, replay.Version, ct), questionId);
            }
            await EnsureKeyAvailableAsync(db, id, request.RequestKey, ct);
            if (!options.Value.Enabled) throw new ExtractionException("extraction_disabled", 503);
            if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
            if (job.Status != "Succeeded") throw new ExtractionException("processing_in_progress", 409);
            snapshot = await CurrentSnapshotAsync(db, job, ct);
            await RequirePendingAsync(db, job, snapshot, questionId, request.ItemId, ct);
        }
        // The model runs without the job lock; publication rechecks version and the live item.
        var target = snapshot.Questions.Single(q => q.Question.Id == questionId);
        var sources = Sources(job);
        var analyzed = await AnalyzeRevisionAsync(job.Grade, AdjustmentPages(sources, target.Question), [target.Question],
            instruction, "questions", [], ct);
        var adjusted = Rules.Adjustment(snapshot.Questions.Select(q => q.Question).ToList(), target.Question,
            analyzed.Questions, sources);
        ExtractionJobView result;
        try
        {
            result = await ReviseCoreAsync(access, id, new(request.RequestKey, request.ExpectedVersion, instruction, [adjusted]),
                questionId, ct, pending: new(hash, [(questionId, request.ItemId)]));
        }
        catch (ExtractionException)
        {
            // Crops stored for a rejected publication are unreferenced; reclaim them now, not on the next sweep.
            try { await CollectJobAsync(id, CancellationToken.None); }
            catch (Exception error) { logger.LogWarning("Adjustment cleanup deferred for {JobId} {ErrorType}", id, error.GetType().Name); }
            throw;
        }
        await CollectJobAsync(id, ct);
        return QuestionResult(result, questionId);
    }

    /// <summary>
    /// Every page while the job is within the model's page limit. Accepted supplements can grow a job past it;
    /// then the target's own pages come first and the rest follow in reading order up to the limit.
    /// </summary>
    private static List<ExtractionSource> AdjustmentPages(List<ExtractionSource> sources, ExtractionQuestionDraft target)
    {
        if (sources.Count <= Rules.MaxSources) return sources;
        var own = target.Regions.Select(r => r.SourceId).ToHashSet();
        var chosen = sources.Where(s => own.Contains(s.Id)).Concat(sources.Where(s => !own.Contains(s.Id)))
            .Take(Rules.MaxSources).Select(s => s.Id).ToHashSet();
        return sources.Where(s => chosen.Contains(s.Id)).ToList();
    }

    private static ExtractionQuestionRevisionView QuestionResult(ExtractionJobView job, Guid questionId) =>
        new(job.Id, job.StudentId, job.Version, job.Questions.SingleOrDefault(q => q.Question.Id == questionId))
        { Superseded = job.Superseded };

    /// <summary>The target must still be the same incorrect question bound to the same pending item.</summary>
    private static async Task RequirePendingAsync(MistakeDbContext db, ExtractionJobEntity job, ExtractionSnapshot snapshot,
        Guid questionId, Guid itemId, CancellationToken ct)
    {
        var target = snapshot.Questions.SingleOrDefault(q => q.Question.Id == questionId)
            ?? throw new ExtractionException("not_found", 404);
        if (target.ItemDeleted || target.ItemId != itemId) throw new ExtractionException("item_binding_conflict", 409);
        if (target.Question.Outcome != "incorrect") throw new ExtractionException("review_conflict", 409);
        var item = (await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE id = {itemId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (item is null || item.StudentId != job.StudentId || item.SourceUploadId != $"extract:{job.Id:N}"
            || item.QuestionId != questionId.ToString("D") || item.Subject != target.Question.Subject)
            throw new ExtractionException("item_binding_conflict", 409);
        if (item.ReviewStatus != (int)ReviewStatus.PendingReview) throw new ExtractionException("review_conflict", 409);
    }
}
