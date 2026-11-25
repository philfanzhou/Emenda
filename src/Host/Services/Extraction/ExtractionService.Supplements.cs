using Emenda.Database;
using Emenda.Common.Oss;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private static ExtractionSupplementView SupplementView(ExtractionSupplementEntity row) =>
        new(row.Id, row.QuestionId, row.ItemId, row.Status, row.Note, row.AcceptedVersion ?? row.ExpectedVersion,
            ExtractionRules.Read<List<ExtractionSource>>(row.SourcesJson));

    private static async Task<ExtractionSnapshot> CurrentSnapshotAsync(MistakeDbContext db, ExtractionJobEntity job, CancellationToken ct) =>
        job.Version == 0 ? new([]) : await WithDeletionStateAsync(db, ExtractionRules.Read<ExtractionSnapshot>(
            (await db.ExtractionRevisions.AsNoTracking().SingleAsync(r => r.JobId == job.Id && r.Version == job.Version, ct)).SnapshotJson), ct);

    private static ExtractionQuestionView Target(ExtractionSnapshot snapshot, Guid questionId, Guid itemId)
    {
        var target = snapshot.Questions.SingleOrDefault(q => q.Question.Id == questionId);
        if (target is null || target.ItemDeleted || target.ItemId != itemId) throw new ExtractionException("not_found", 404);
        if (target.Question.Outcome != "incorrect") throw new ExtractionException("question_not_in_review", 409);
        return target;
    }

    private static void ResetReview(MistakeItemEntity item)
    {
        item.ReviewStatus = (int)ReviewStatus.PendingReview;
        item.ReviewerId = null;
        item.ReviewedAt = null;
        item.RootCause = null;
    }

    private static async Task EnsureKeyAvailableAsync(MistakeDbContext db, Guid id, Guid key, CancellationToken ct)
    {
        if (await db.ExtractionRevisions.AnyAsync(r => r.JobId == id && r.RequestKey == key, ct)
            || await db.ExtractionSupplements.AnyAsync(r => r.JobId == id && r.RequestKey == key, ct)
            || await db.ExtractionProposals.AnyAsync(r => r.JobId == id && r.RequestKey == key, ct))
            throw new ExtractionException("request_payload_conflict", 409);
    }

    public async Task<ExtractionSupplementView> RequestSupplementAsync(ExtractionAccess access, Guid id, Guid questionId,
        ExtractionSupplementRequest request, CancellationToken ct = default)
    {
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 1 || request.ItemId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            throw new ExtractionException("invalid_request");
        var hash = ExtractionRules.Hash(new { questionId, request, operation = "request_supplement" });
        await using var jobLock = await SerializeJobAsync(id, ct);
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id.ToString(), ct);
        var job = await FindAsync(db, access, id, ct);
        var replay = await db.ExtractionSupplements.SingleOrDefaultAsync(r => r.JobId == id && r.RequestKey == request.RequestKey, ct);
        if (replay is not null)
        {
            if (replay.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
            return SupplementView(replay);
        }
        await EnsureKeyAvailableAsync(db, id, request.RequestKey, ct);
        if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
        var snapshot = await CurrentSnapshotAsync(db, job, ct);
        Target(snapshot, questionId, request.ItemId);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_supplement SET status='Superseded' WHERE job_id={id} AND question_id={questionId} AND status='Requested'", ct);
        var row = new ExtractionSupplementEntity { Id = Guid.NewGuid(), JobId = id, QuestionId = questionId,
            ItemId = request.ItemId, RequestKey = request.RequestKey, PayloadHash = hash, ExpectedVersion = job.Version,
            AcceptedVersion = job.Version + 1, Note = request.Note, CreatedAt = await NowAsync(db, ct) };
        db.ExtractionSupplements.Add(row);
        ResetReview(await db.MistakeItems.SingleAsync(i => i.Id == request.ItemId, ct));
        await PublishExtractionAsync(db, job, snapshot, snapshot, request.RequestKey, hash, access.Actor, request.Note, "supplement_request", ct);
        await tx.CommitAsync(ct);
        return SupplementView(row);
    }

    public async Task<ExtractionSupplementView> AppendSupplementAsync(ExtractionAccess access, Guid id, Guid questionId,
        Guid supplementId, ExtractionSupplementUpload request, CancellationToken ct = default)
    {
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 1 || request.ItemId == Guid.Empty)
            throw new ExtractionException("invalid_request");
        ExtractionRules.ValidateUpload(new("supplement", 1, "supplement", request.Images));
        var hash = ExtractionRules.Hash(new { questionId, supplementId, request.ExpectedVersion, request.ItemId,
            images = request.Images.Select(i => new { i.ContentType, sha256 = ExtractionRules.Hash(i.Content) }).ToList() });
        await using var jobLock = await SerializeJobAsync(id, ct);
        ExtractionSupplementEntity staged;
        await using (var db = Open())
        {
            var job = await FindAsync(db, access, id, ct);
            var existing = await db.ExtractionSupplements.SingleOrDefaultAsync(r => r.JobId == id && r.RequestKey == request.RequestKey, ct);
            if (existing is not null && existing.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
            if (existing?.Status == "Completed") return SupplementView(existing);
            if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
            Target(await CurrentSnapshotAsync(db, job, ct), questionId, request.ItemId);
            var parent = await db.ExtractionSupplements.SingleOrDefaultAsync(r => r.Id == supplementId && r.JobId == id
                && r.QuestionId == questionId && r.ItemId == request.ItemId, ct) ?? throw new ExtractionException("not_found", 404);
            if (parent.Status != "Requested") throw new ExtractionException("supplement_not_pending", 409);
            if (existing is null)
            {
                await EnsureKeyAvailableAsync(db, id, request.RequestKey, ct);
                staged = new() { Id = Guid.NewGuid(), JobId = id, QuestionId = questionId, ItemId = request.ItemId,
                    RequestKey = request.RequestKey, PayloadHash = hash, ExpectedVersion = job.Version,
                    ParentId = parent.Id, Status = "Staging", CreatedAt = await NowAsync(db, ct) };
                var sources = new List<ExtractionSource>();
                foreach (var image in request.Images)
                {
                    var sha = ExtractionRules.Hash(image.Content);
                    if (sources.Any(s => s.Sha256 == sha && s.ContentType == image.ContentType)) continue;
                    var original = Sources(job).FirstOrDefault(s => s.Sha256 == sha && s.ContentType == image.ContentType);
                    if (original is not null) { sources.Add(original); continue; }
                    QuestionImage.Metadata metadata;
                    try { metadata = QuestionImage.Inspect(image.Content, image.ContentType); }
                    catch (Exception error) when (error is ArgumentException or ImageMagick.MagickException or InvalidDataException)
                    { throw new ExtractionException("invalid_image"); }
                    var sourceId = Guid.NewGuid();
                    var extension = image.ContentType == "image/png" ? "png" : "jpg";
                    sources.Add(new(sourceId, $"mistakes/extractions/{id:N}/supplements/{staged.Id:N}/originals/{sourceId:N}.{extension}",
                        image.ContentType, image.Content.Length, sha, metadata.DisplayWidth, metadata.DisplayHeight, metadata.Orientation));
                }
                staged.SourcesJson = ExtractionRules.Serialize(sources);
                db.ExtractionSupplements.Add(staged);
                await db.SaveChangesAsync(ct);
            }
            else staged = existing;
            foreach (var source in ExtractionRules.Read<List<ExtractionSource>>(staged.SourcesJson))
            {
                if (!Sources(job).Any(s => s.Id == source.Id)) await TrackObjectAsync(id, source.Path, ct);
                var image = request.Images.First(i => ExtractionRules.Hash(i.Content) == source.Sha256 && i.ContentType == source.ContentType);
                await StoreAsync(source.Path, image.Content, source.ContentType, ct);
            }
        }
        await using var publish = Open();
        await using var tx = await publish.Database.BeginTransactionAsync(ct);
        await LockAsync(publish, id.ToString(), ct);
        var current = await FindAsync(publish, access, id, ct);
        if (current.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
        var pending = await publish.ExtractionSupplements.SingleAsync(r => r.Id == supplementId, ct);
        if (pending.Status != "Requested") throw new ExtractionException("supplement_not_pending", 409);
        var snapshotNow = await CurrentSnapshotAsync(publish, current, ct);
        Target(snapshotNow, questionId, request.ItemId);
        var accepted = await publish.ExtractionSupplements.SingleAsync(r => r.Id == staged.Id, ct);
        accepted.Status = "Completed";
        accepted.AcceptedVersion = current.Version + 1;
        pending.Status = "Fulfilled";
        current.SourcesJson = ExtractionRules.Serialize(Sources(current).Concat(ExtractionRules.Read<List<ExtractionSource>>(accepted.SourcesJson)).DistinctBy(s => s.Id).ToList());
        await ImageVariantSchema.EnqueueAsync(publish, ExtractionRules.Read<List<ExtractionSource>>(accepted.SourcesJson).Select(s => s.Path), ct);
        ResetReview(await publish.MistakeItems.SingleAsync(i => i.Id == request.ItemId, ct));
        await PublishExtractionAsync(publish, current, snapshotNow, snapshotNow, request.RequestKey, hash,
            access.Actor, "append_supplement", "supplement_upload", ct);
        await tx.CommitAsync(ct);
        return SupplementView(accepted);
    }

    public async Task<ExtractionSupplementView> GetSupplementAsync(ExtractionAccess access, Guid id, Guid questionId,
        Guid supplementId, CancellationToken ct = default)
    {
        await using var db = Open();
        await FindAsync(db, access, id, ct);
        var row = await db.ExtractionSupplements.AsNoTracking().SingleOrDefaultAsync(r => r.Id == supplementId
            && r.JobId == id && r.QuestionId == questionId, ct) ?? throw new ExtractionException("not_found", 404);
        return SupplementView(row);
    }
}
