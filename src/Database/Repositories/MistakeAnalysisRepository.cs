using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Database.Repositories;

public sealed class MistakeAnalysisRepository(MistakeDbContext db) : IMistakeAnalysisRepository
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid> StartAsync(Guid itemId, string reviewerId, string caller, Guid requestId,
        long expectedRevision, string? description, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty || expectedRevision < 0 || string.IsNullOrWhiteSpace(reviewerId) ||
            reviewerId.Length > 36 || description?.Length > 8000)
            throw new ArgumentException("invalid_analysis_request");
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        // Endpoint ownership checks may have tracked an older item before this lock.
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await LockItemAsync(itemId, ct);
        await LegacyReturnWriteGuard.EnsureAsync(db, itemId, ct);
        var analysis = await db.MistakeAnalyses.FindAsync([itemId], ct);
        var feedback = ReadFeedback(analysis);
        var replay = feedback.SingleOrDefault(f => f.Caller == caller && f.RequestId == requestId);
        if (replay is not null)
        {
            if (replay.ReviewerId != reviewerId || replay.ExpectedRevision != expectedRevision || replay.Description != description)
                throw new AnalysisConflictException("request_conflict");
            return replay.JobId;
        }
        EnsureAvailable(item);
        if (item.Subject != 2) throw new ArgumentException("subject_not_supported");
        if ((analysis?.Revision ?? 0) != expectedRevision)
            throw new AnalysisConflictException("analysis_changed");
        if (await db.ReanalyzeJobs.AnyAsync(j => j.ItemId == itemId && (j.Status == 1 || j.Status == 2), ct))
            throw new AnalysisConflictException("analysis_in_progress");

        analysis ??= new MistakeAnalysisEntity { MistakeItemId = itemId };
        if (db.Entry(analysis).State == EntityState.Detached) db.MistakeAnalyses.Add(analysis);
        var jobId = Guid.NewGuid();
        feedback.Add(new AnalysisFeedback
        {
            JobId = jobId, RequestId = requestId, Caller = caller, ReviewerId = reviewerId,
            ExpectedRevision = expectedRevision, Description = description, CreatedAt = DateTimeOffset.UtcNow
        });
        analysis.Revision++;
        analysis.TeacherFeedback = JsonSerializer.Serialize(feedback, Json);
        var input = new StructuredAnalysisInput
        {
            Revision = analysis.Revision, Subject = item.Subject, Grade = item.Grade,
            SourceRegions = item.SourceRegions.Select(r => new SourceRegionModel
            {
                SourceImagePath = r.SourceImagePath,
                BoundingBox = r.BoundingBox is null ? null : new BoundingBoxModel
                { X1 = r.BoundingBox.X1, Y1 = r.BoundingBox.Y1, X2 = r.BoundingBox.X2, Y2 = r.BoundingBox.Y2 }
            }).ToList(),
            PreviousReport = DeserializeReport(analysis.DraftReport ?? analysis.ConfirmedReport),
            TeacherFeedback = feedback.Where(f => f.Description is not null).Select(f => f.Description!).ToList()
        };
        db.ReanalyzeJobs.Add(new ReanalyzeJobEntity
        {
            Id = jobId, ItemId = itemId, StudentId = item.StudentId, ReviewerId = reviewerId,
            TeacherDescription = description, CreatedAt = DateTimeOffset.UtcNow,
            AnalysisInput = JsonSerializer.Serialize(input, Json), ExecutionInputDigest = InputDigest(item)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return jobId;
    }

    public async Task<MistakeAnalysisModel?> GetAsync(Guid itemId, CancellationToken ct = default)
    {
        var analysis = await db.MistakeAnalyses.AsNoTracking().SingleOrDefaultAsync(a => a.MistakeItemId == itemId, ct);
        if (analysis is null) return null;
        var item = await db.MistakeItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("mistake_not_found");
        return Map(analysis, item);
    }

    public async Task<MistakeAnalysisModel> ConfirmAsync(Guid itemId, long expectedRevision, string reviewerId,
        CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await LockItemAsync(itemId, ct);
        await LegacyReturnWriteGuard.EnsureAsync(db, itemId, ct);
        EnsureAvailable(item);
        var analysis = await db.MistakeAnalyses.FindAsync([itemId], ct)
            ?? throw new AnalysisConflictException("analysis_missing");
        if (analysis.Revision != expectedRevision) throw new AnalysisConflictException("analysis_changed");
        if (analysis.DraftReport is null) throw new AnalysisConflictException("draft_missing");
        if (analysis.InputDigest != InputDigest(item)) throw new AnalysisConflictException("input_changed");
        if (await db.ReanalyzeJobs.AnyAsync(j => j.ItemId == itemId && (j.Status == 1 || j.Status == 2), ct))
            throw new AnalysisConflictException("analysis_in_progress");
        if (analysis.DraftJobId == analysis.ConfirmedJobId) return Map(analysis, item);
        var report = DeserializeReport(analysis.DraftReport)!;
        report.Validate();
        analysis.ConfirmedReport = analysis.DraftReport;
        analysis.ConfirmedJobId = analysis.DraftJobId;
        analysis.ConfirmedAt = DateTimeOffset.UtcNow;
        analysis.ConfirmedBy = reviewerId;
        item.RootCause = report.Summary;
        item.ReviewStatus = (int)ReviewStatus.Confirmed;
        item.ReviewerId = reviewerId;
        item.ReviewedAt = analysis.ConfirmedAt;
        item.UpdatedAt = analysis.ConfirmedAt;
        db.ProcessLogs.Add(new ProcessLogEntity
        {
            Id = Guid.NewGuid(), MistakeItemId = itemId, StudentId = item.StudentId,
            Action = (int)ProcessAction.ReviewConfirmed,
            Details = JsonSerializer.Serialize(new { analysis_revision = analysis.Revision, reviewer_id = reviewerId }),
            CreatedAt = analysis.ConfirmedAt.Value
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Map(analysis, item);
    }

    private async Task<MistakeItemEntity> LockItemAsync(Guid itemId, CancellationToken ct) =>
        await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE id={itemId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("mistake_not_found");

    private static void EnsureAvailable(MistakeItemEntity item)
    {
        if (item.ReviewStatus is not (1 or 2) || item.SourceRegions.Count == 0)
            throw new AnalysisConflictException("input_unavailable");
    }

    internal static string InputDigest(MistakeItemEntity item) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { item.StudentId, item.Subject, item.Grade, item.QuestionId, item.SourceRegions }, Json))));

    internal static List<AnalysisFeedback> ReadFeedback(MistakeAnalysisEntity? analysis) =>
        analysis is null ? [] : JsonSerializer.Deserialize<List<AnalysisFeedback>>(analysis.TeacherFeedback, Json)!;

    internal static MistakeAnalysisReport? DeserializeReport(string? report) =>
        report is null ? null : JsonSerializer.Deserialize<MistakeAnalysisReport>(report, Json);

    private static MistakeAnalysisModel Map(MistakeAnalysisEntity analysis, MistakeItemEntity item) => new()
    {
        MistakeItemId = item.Id, Revision = analysis.Revision,
        DraftReport = DeserializeReport(analysis.DraftReport), ConfirmedReport = DeserializeReport(analysis.ConfirmedReport),
        DraftJobId = analysis.DraftJobId, ConfirmedJobId = analysis.ConfirmedJobId,
        TeacherFeedback = ReadFeedback(analysis), GeneratedAt = analysis.GeneratedAt,
        Model = analysis.Model, PromptVersion = analysis.PromptVersion,
        ConfirmedAt = analysis.ConfirmedAt, ConfirmedBy = analysis.ConfirmedBy,
        InputChanged = analysis.DraftReport is not null && analysis.InputDigest != InputDigest(item)
    };
}
