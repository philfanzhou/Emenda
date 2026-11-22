using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    public async Task<ExtractionJobView> ReviseAsync(ExtractionAccess access, Guid id,
        ExtractionRevisionRequest request, CancellationToken ct = default)
    {
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new ExtractionException("invalid_request");
        var hash = ExtractionRules.Hash(request);
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
            if (initial.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
            if (initial.Status is not ("Succeeded" or "Failed")) throw new ExtractionException("processing_in_progress", 409);
            ExtractionRules.ValidateQuestions(request.Questions, Sources(initial), automatic: false);
            prior = initial.Version == 0 ? null : ExtractionRules.Read<ExtractionSnapshot>(
                (await db.ExtractionRevisions.AsNoTracking().SingleAsync(r => r.JobId == id && r.Version == initial.Version, ct)).SnapshotJson);
        }
        var snapshot = await BuildSnapshotAsync(id, request.RequestKey, request.Questions, Sources(initial), prior, ct);
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
                    Grade = job.Grade, SourceUploadId = $"extract:{job.Id:N}", QuestionId = row.Id.ToString("D"),
                    SourceRegions = proposed.Images.Select(i => new SourceRegionEntry { SourceImagePath = i.Path }).ToList(),
                    ReviewStatus = (int)ReviewStatus.PendingReview, CreatedAt = now };
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
                item.SourceRegions = proposed.Images.Select(i => new SourceRegionEntry { SourceImagePath = i.Path }).ToList();
                item.ReviewStatus = proposed.Question.Outcome == "incorrect" ? (int)ReviewStatus.PendingReview : (int)ReviewStatus.Rejected;
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
            item.ReviewStatus = (int)ReviewStatus.Rejected;
            item.ReviewerId = null;
            item.ReviewedAt = null;
            item.RootCause = null;
            item.UpdatedAt = now;
        }
        db.ExtractionRevisions.Add(new() { JobId = job.Id, Version = job.Version + 1, RequestKey = requestKey,
            PayloadHash = payloadHash, SnapshotJson = ExtractionRules.Serialize(new ExtractionSnapshot(published)),
            Actor = actor, Reason = reason, Model = model, CreatedAt = now });
        job.Version++;
        job.Status = "Succeeded";
        job.ErrorCode = null;
        job.OwnerId = null;
        job.LeaseUntil = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static void RetainBeforeCorrection(MistakeDbContext db, MistakeItemEntity item, string actor,
        string reason, int version, DateTimeOffset now) => db.ProcessLogs.Add(new()
        {
            Id = Guid.NewGuid(), MistakeItemId = item.Id, StudentId = item.StudentId,
            Action = (int)ProcessAction.Analyzed, CreatedAt = now,
            Details = ExtractionRules.Serialize(new { operation = "extraction_corrected", actor, reason, version,
                previous = new { item.Subject, item.ReviewStatus, item.ReviewerId, item.ReviewedAt, item.RootCause, item.SourceRegions } })
        });
}
