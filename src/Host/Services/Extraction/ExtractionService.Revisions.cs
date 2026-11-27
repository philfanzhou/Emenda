using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Dtos;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    public async Task<ExtractionJobView> ReviseAsync(ExtractionAccess access, Guid id,
        ExtractionRevisionRequest request, CancellationToken ct = default)
    {
        var result = await ReviseCoreAsync(access, id, request, null, ct);
        await CollectJobAsync(id, ct);
        return result;
    }

    public async Task<ExtractionQuestionRevisionView> ReviseQuestionAsync(ExtractionAccess access, Guid id,
        Guid questionId, ExtractionQuestionRevisionRequest request, CancellationToken ct = default)
    {
        if (request is null || request.ExpectedVersion < 1 || request.Question is null ||
            questionId == Guid.Empty || request.Question.Id != questionId)
            throw new ExtractionException("invalid_request");
        var result = await ReviseCoreAsync(access, id,
            new(request.RequestKey, request.ExpectedVersion, request.Reason, [request.Question]), questionId, ct);
        await CollectJobAsync(id, ct);
        return new(result.Id, result.StudentId, result.Version,
            result.Questions.SingleOrDefault(q => q.Question.Id == questionId)) { Superseded = result.Superseded };
    }

    private async Task<ExtractionJobView> ReviseCoreAsync(ExtractionAccess access, Guid id,
        ExtractionRevisionRequest request, Guid? questionId, CancellationToken ct, ProposalAcceptance? acceptance = null)
    {
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new ExtractionException("invalid_request");
        // Keep existing whole-set receipts byte-compatible; bind partial receipts to their target.
        var hash = acceptance?.Hash ?? (questionId.HasValue ? ExtractionRules.Hash(new { questionId, request }) : ExtractionRules.Hash(request));
        await using var jobLock = await SerializeJobAsync(id, ct);
        List<ExtractionQuestionDraft> drafts;
        ExtractionJobEntity initial;
        ExtractionSnapshot? prior;
        await using (var db = Open())
        {
            initial = await FindAsync(db, access, id, ct);
            var replay = await db.ExtractionRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.JobId == id && r.RequestKey == request.RequestKey, ct);
            if (replay is not null)
            {
                if (replay.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
                return await ViewAsync(db, initial, replay.Version, ct);
            }
            await EnsureKeyAvailableAsync(db, id, request.RequestKey, ct);
            if (initial.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
            if (initial.Status is not ("Succeeded" or "Failed")) throw new ExtractionException("processing_in_progress", 409);
            prior = initial.Version == 0 ? null : ExtractionRules.Read<ExtractionSnapshot>(
                (await db.ExtractionRevisions.AsNoTracking().SingleAsync(r => r.JobId == id && r.Version == initial.Version, ct)).SnapshotJson);
            if (prior is not null) prior = await WithDeletionStateAsync(db, prior, ct);
            if (questionId.HasValue && prior?.Questions.Any(q => q.Question.Id == questionId) != true)
                throw new ExtractionException("not_found", 404);
            drafts = questionId.HasValue
                ? prior!.Questions.Select(q => q.Question.Id == questionId ? request.Questions.Single() : q.Question).ToList()
                : request.Questions;
            if (acceptance is not null)
                drafts = await ProposalRevisionAsync(db, id, initial.Version, prior ?? new([]), acceptance, ct);
            else if (prior is not null && drafts is not null)
            {
                // Legacy Portal corrections omit grade. Keep inferred grades on existing groups.
                var priorGrades = prior.Questions.ToDictionary(q => q.Question.Id, q => q.Question.Grade);
                drafts = drafts.Select(q => q is not null && q.Grade is null && priorGrades.GetValueOrDefault(q.Id) is { } grade
                    ? q with { Grade = grade } : q!).ToList();
            }
            ExtractionRules.ValidateQuestions(drafts, Sources(initial), automatic: false);
            if (drafts is null) throw new ExtractionException("invalid_questions");
        }
        var snapshot = await BuildSnapshotAsync(id, request.RequestKey, drafts, Sources(initial), prior, ct);
        await using var publish = Open();
        await using var tx = await publish.Database.BeginTransactionAsync(ct);
        await LockAsync(publish, id.ToString(), ct);
        var current = await FindAsync(publish, access, id, ct);
        var accepted = await publish.ExtractionRevisions.SingleOrDefaultAsync(r => r.JobId == id && r.RequestKey == request.RequestKey, ct);
        if (accepted is not null)
        {
            if (accepted.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
            return await ViewAsync(publish, current, accepted.Version, ct);
        }
        if (current.Version != request.ExpectedVersion || current.Status is not ("Succeeded" or "Failed"))
            throw new ExtractionException("version_conflict", 409);
        await PublishExtractionAsync(publish, current, snapshot, prior, request.RequestKey, hash,
            access.Actor, request.Reason, "manual", ct);
        if (acceptance is not null)
            await publish.ExtractionProposals.Where(p => p.Id == acceptance.Id && p.JobId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Accepted"), ct);
        await tx.CommitAsync(ct);
        return await ViewAsync(publish, current, null, ct);
    }

    private static async Task PublishExtractionAsync(MistakeDbContext db, ExtractionJobEntity job,
        ExtractionSnapshot snapshot, ExtractionSnapshot? prior, Guid requestKey, string payloadHash,
        string actor, string reason, string model, CancellationToken ct)
    {
        var now = await NowAsync(db, ct);
        var current = await db.ExtractionQuestions.Where(q => q.JobId == job.Id).ToListAsync(ct);
        var incomingIds = snapshot.Questions.Select(q => q.Question.Id).ToArray();
        if (await db.ExtractionQuestions.AnyAsync(q => incomingIds.Contains(q.Id) && q.JobId != job.Id, ct))
            throw new ExtractionException("question_source_conflict", 409);
        var itemIds = current.Where(q => q.ItemId.HasValue).Select(q => q.ItemId!.Value).ToArray();
        var items = await db.MistakeItems.FromSqlInterpolated(
            $"SELECT * FROM mistake_item WHERE id = ANY({itemIds}) ORDER BY id FOR UPDATE").ToDictionaryAsync(i => i.Id, ct);
        var published = new List<ExtractionQuestionView>();
        foreach (var proposed in snapshot.Questions)
        {
            var row = current.SingleOrDefault(q => q.Id == proposed.Question.Id);
            if (row is null)
            {
                row = new() { Id = proposed.Question.Id, JobId = job.Id, Active = true };
                db.ExtractionQuestions.Add(row);
            }
            var old = prior?.Questions.SingleOrDefault(q => q.Question.Id == proposed.Question.Id);
            var changed = old is null ||
                ExtractionRules.Hash(old.Question with { QuestionNumber = null }) !=
                ExtractionRules.Hash(proposed.Question with { QuestionNumber = null }) || !row.Active;
            row.Active = true;
            MistakeItemEntity? item = row.ItemId is { } itemId ? items.GetValueOrDefault(itemId) : null;
            if (proposed.Question.Outcome == "incorrect" && row.ItemId is null)
            {
                item = new() { Id = Guid.NewGuid(), StudentId = job.StudentId, Subject = proposed.Question.Subject,
                    Grade = proposed.Question.Grade ?? 0, SourceUploadId = $"extract:{job.Id:N}", QuestionId = row.Id.ToString("D"),
                    SourceRegions = proposed.Images.Select(i => new SourceRegionEntry { SourceImagePath = i.Path }).ToList(),
                    ReviewStatus = (int)ReviewStatus.PendingReview, CreatedAt = now };
                MistakeSource.SetAnalysis(item, SerializeAnalysis(proposed.Question, job.Version + 1));
                row.ItemId = item.Id;
                db.MistakeItems.Add(item);
                db.ProcessLogs.Add(new() { Id = Guid.NewGuid(), MistakeItemId = item.Id, StudentId = job.StudentId,
                    Action = (int)ProcessAction.Created, CreatedAt = now,
                    Details = ExtractionRules.Serialize(new { operation = "extraction_created", actor, reason, jobId = job.Id,
                        version = job.Version + 1, questionId = row.Id }) });
            }
            else if (item is not null && changed)
            {
                RetainBeforeCorrection(db, item, actor, reason, job.Version + 1, now);
                item.Subject = proposed.Question.Subject;
                item.Grade = proposed.Question.Grade ?? 0;
                item.SourceRegions = proposed.Images.Select(i => new SourceRegionEntry { SourceImagePath = i.Path }).ToList();
                item.ReviewStatus = proposed.Question.Outcome == "incorrect" ? (int)ReviewStatus.PendingReview : (int)ReviewStatus.Rejected;
                MistakeSource.SetAnalysis(item, proposed.Question.Outcome == "incorrect"
                    ? SerializeAnalysis(proposed.Question, job.Version + 1) : null);
                item.ReviewerId = null;
                item.ReviewedAt = null;
                item.RootCause = null;
                item.UpdatedAt = now;
            }
            published.Add(proposed with { ItemId = row.ItemId, ItemDeleted = row.ItemId.HasValue && item is null });
        }
        foreach (var removed in current.Where(q => q.Active && !incomingIds.Contains(q.Id)))
        {
            removed.Active = false;
            if (removed.ItemId is not { } id) continue;
            var item = items.GetValueOrDefault(id);
            if (item is null) continue;
            RetainBeforeCorrection(db, item, actor, reason, job.Version + 1, now);
            item.SourceRegions = [];
            item.ReviewStatus = (int)ReviewStatus.Rejected;
            MistakeSource.SetAnalysis(item, null);
            item.ReviewerId = null;
            item.ReviewedAt = null;
            item.RootCause = null;
            item.UpdatedAt = now;
        }
        db.ExtractionRevisions.Add(new() { JobId = job.Id, Version = job.Version + 1, RequestKey = requestKey,
            PayloadHash = payloadHash, SnapshotJson = ExtractionRules.Serialize(new ExtractionSnapshot(published)),
            Actor = actor, Reason = reason, Model = model, CreatedAt = now });
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_revision SET snapshot_json='{{\"questions\":[]}}'::jsonb WHERE job_id={job.Id}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_proposal SET status='Expired',request_json=NULL,result_json=NULL,owner=NULL,lease_until=NULL WHERE job_id={job.Id} AND status IN ('Generating','Pending')", ct);
        var previousPaths = prior?.Questions.SelectMany(q => q.Images).Select(i => i.Path).ToArray() ?? [];
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_object SET legacy=false WHERE job_id={job.Id} AND path=ANY({previousPaths})", ct);
        var currentPaths = published.SelectMany(q => q.Images).Select(i => i.Path).Concat(Sources(job).Select(s => s.Path)).ToArray();
        await ImageVariantSchema.EnqueueAsync(db, published.SelectMany(q => q.Images).Select(i => i.Path).Except(previousPaths), ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_object SET pending=NOT(path=ANY({currentPaths})),next_attempt=clock_timestamp() WHERE job_id={job.Id} AND NOT legacy", ct);
        job.CandidateJson = null;
        job.LayoutJson = "[]";
        job.Version++;
        job.Status = "Succeeded";
        job.ErrorCode = null;
        job.OwnerId = null;
        job.LeaseUntil = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static string SerializeAnalysis(ExtractionQuestionDraft question, int version) =>
        ExtractionRules.Serialize(new MistakeAnalysisSnapshotDto(question.Statement, question.StudentAnswer!,
            question.ReferenceAnswer, question.Explanation, question.Evidence, version));

    private static void RetainBeforeCorrection(MistakeDbContext db, MistakeItemEntity item, string actor,
        string reason, int version, DateTimeOffset now) => db.ProcessLogs.Add(new()
        {
            Id = Guid.NewGuid(), MistakeItemId = item.Id, StudentId = item.StudentId,
            Action = (int)ProcessAction.Analyzed, CreatedAt = now,
            Details = ExtractionRules.Serialize(new { operation = "extraction_corrected", actor, reason, version,
                previous = new { item.Subject, item.ReviewStatus, item.ReviewerId, item.ReviewedAt, item.RootCause } })
        });
}
