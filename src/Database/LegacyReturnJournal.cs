using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;

namespace Ruoyu.Study.Mistake.Database;

/// <summary>Persists short transactions only. Callers authenticate review requests and verify external bytes and receipts.</summary>
public sealed class LegacyReturnJournal(DbContextOptions<MistakeDbContext> options)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private MistakeDbContext Open() => new(options);

    public async Task<LegacyReturnEntity?> GetAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var db = Open();
        return await db.LegacyReturns.AsNoTracking().SingleOrDefaultAsync(o => o.ItemId == itemId, ct);
    }

    public async Task<LegacyReturnEntity> CreateAsync(Guid itemId, string actor, string? reason, string? cause,
        int grade, int subject, CancellationToken ct = default, Guid? ownerId = null,
        Func<MistakeItemEntity, bool>? validateItem = null)
    {
        if (itemId == Guid.Empty || string.IsNullOrWhiteSpace(actor)) throw new InvalidOperationException("invalid_request");
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await LockItemAsync(db, itemId, ct);
        var effectiveGrade = grade is >= 1 and <= 12 ? grade : item.Grade;
        var effectiveSubject = subject is >= 1 and <= 9 ? subject : item.Subject;
        var canonical = JsonSerializer.Serialize(new object?[] { actor, reason ?? "", cause, effectiveGrade, effectiveSubject }, Json);
        var prior = await db.LegacyReturns.AsNoTracking().SingleOrDefaultAsync(o => o.ItemId == itemId, ct);
        if (prior is not null)
        {
            if (prior.Canonical != canonical) throw new InvalidOperationException("return_payload_conflict");
            await tx.CommitAsync(ct);
            return prior;
        }
        if (item.ReviewStatus != 1 || await db.SourceIntakeGroups.AnyAsync(g => g.ItemId == itemId, ct))
            throw new InvalidOperationException("return_not_pending");
        if (validateItem is not null && !validateItem(item)) throw new InvalidOperationException("invalid_return_source");
        if (await db.ReanalyzeJobs.AnyAsync(j => j.ItemId == itemId && (j.Status == 1 || j.Status == 2), ct))
            throw new InvalidOperationException("reanalysis_active");
        var paths = item.SourceRegions.Select(r => r.SourceImagePath).Distinct(StringComparer.Ordinal).ToArray();
        var now = await ClockAsync(db, ct);
        var op = new LegacyReturnEntity
        {
            ItemId = itemId, OperationId = $"mistake-return:{itemId:D}", StudentId = item.StudentId,
            Actor = actor, Canonical = canonical, OriginalItem = await OriginalAsync(db, itemId, ct),
            ReturnReason = reason ?? "", RootCause = cause, Grade = effectiveGrade, Subject = effectiveSubject,
            OwnerId = ownerId ?? Guid.NewGuid(), Fence = 1, CreatedAt = now, LeaseUntil = now.AddSeconds(60)
        };
        db.LegacyReturns.Add(op);
        await db.SaveChangesAsync(ct);
        for (var position = 0; position < paths.Length; position++)
            db.LegacyReturnObjects.Add(new() { ItemId = itemId, Position = position, SourcePath = paths[position] });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return op;
    }

    public async Task<LegacyReturnEntity?> AcquireAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockItemAsync(db, itemId, ct);
        var op = await db.LegacyReturns.SingleOrDefaultAsync(o => o.ItemId == itemId, ct);
        var now = await ClockAsync(db, ct);
        if (op is null || op.Phase is "Completed" or "Conflict" || op.LeaseUntil > now) return null;
        op.OwnerId = Guid.NewGuid(); op.Fence++; op.LeaseUntil = now.AddSeconds(60);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return op;
    }

    public Task<bool> RenewAsync(Guid itemId, Guid owner, long fence, CancellationToken ct = default) =>
        LeaseAsync(itemId, owner, fence, release: false, ct);
    public Task<bool> ReleaseAsync(Guid itemId, Guid owner, long fence, CancellationToken ct = default) =>
        LeaseAsync(itemId, owner, fence, release: true, ct);

    private async Task<bool> LeaseAsync(Guid itemId, Guid owner, long fence, bool release, CancellationToken ct)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockItemAsync(db, itemId, ct);
        var rows = release
            ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE legacy_return SET lease_until=NULL WHERE item_id={itemId} AND owner_id={owner} AND fence={fence} AND lease_until>clock_timestamp() AND phase NOT IN ('Completed','Conflict')", ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE legacy_return SET lease_until=clock_timestamp()+interval '60 seconds' WHERE item_id={itemId} AND owner_id={owner} AND fence={fence} AND lease_until>clock_timestamp() AND phase NOT IN ('Completed','Conflict')", ct);
        await tx.CommitAsync(ct);
        return rows == 1;
    }

    public Task CaptureAsync(Guid itemId, Guid owner, long fence, int position, long length, string sha256, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, async (db, op) =>
        {
            if (op.Phase != "Capturing") throw new InvalidOperationException("return_binding");
            var obj = await db.LegacyReturnObjects.SingleAsync(o => o.ItemId == itemId && o.Position == position, ct);
            if (obj.ExpectedLength is not null)
            {
                if (obj.ExpectedLength != length || obj.ExpectedSha256 != sha256) throw new InvalidOperationException("return_source_changed");
                return;
            }
            obj.ExpectedLength = length; obj.ExpectedSha256 = sha256;
        }, ct);

    public Task PrepareAsync(Guid itemId, Guid owner, long fence, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, (_, op) => { op.Phase = "Prepared"; return Task.CompletedTask; }, ct);

    public async Task<LegacyReturnAttemptEntity> ReserveAsync(Guid itemId, Guid owner, long fence, int position, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var attempt = new LegacyReturnAttemptEntity
        {
            AttemptId = id, ItemId = itemId, Position = position, OwnerId = owner, Fence = fence,
            ObjectKey = $"uploads/mistake-returns/{itemId:D}/{id:D}/original.jpg"
        };
        await WriteAsync(itemId, owner, fence, (db, _) => { db.LegacyReturnAttempts.Add(attempt); return Task.CompletedTask; }, ct);
        return attempt;
    }

    public Task SettleAsync(Guid itemId, Guid owner, long fence, Guid attemptId, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, async (db, _) =>
        {
            var a = await db.LegacyReturnAttempts.SingleAsync(a => a.ItemId == itemId && a.AttemptId == attemptId, ct);
            if (a.OwnerId != owner || a.Fence != fence) throw new InvalidOperationException("lease_lost");
            a.Phase = "CopySettled"; a.CopySettledAt = await ClockAsync(db, ct);
        }, ct);

    public Task UnknownAsync(Guid itemId, Guid owner, long fence, Guid attemptId, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, async (db, _) =>
        {
            var a = await db.LegacyReturnAttempts.SingleAsync(a => a.ItemId == itemId && a.AttemptId == attemptId, ct);
            a.Phase = "Unknown";
        }, ct);

    public Task SelectAsync(Guid itemId, Guid owner, long fence, Guid attemptId, long length, string sha256, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, async (db, _) =>
        {
            var a = await db.LegacyReturnAttempts.SingleAsync(a => a.ItemId == itemId && a.AttemptId == attemptId, ct);
            a.Phase = "Verified"; a.VerifiedLength = length; a.VerifiedSha256 = sha256;
            await db.SaveChangesAsync(ct);
            var obj = await db.LegacyReturnObjects.SingleAsync(o => o.ItemId == itemId && o.Position == a.Position, ct);
            obj.SelectedAttemptId = attemptId;
        }, ct);

    public Task SendAsync(Guid itemId, Guid owner, long fence, string payload, string digest, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, (_, op) => { op.Phase = "Sending"; op.Payload = payload; op.PayloadDigest = digest; return Task.CompletedTask; }, ct);

    /// <summary>The caller must obtain this proof through the authenticated, normal-TLS historical receipt client.</summary>
    public Task CompleteAsync(Guid itemId, Guid owner, long fence, string operationId, string studentId,
        Guid recordId, DateTimeOffset receiptCreatedAt, string digestVersion, string digest, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, async (db, op) =>
        {
            if (op.Phase != "Sending" || operationId != op.OperationId || studentId != op.StudentId
                || recordId == Guid.Empty || receiptCreatedAt.Offset != TimeSpan.Zero || receiptCreatedAt == default
                || digestVersion != "returned-payload-v1" || digest != op.PayloadDigest)
                throw new InvalidOperationException("return_receipt_binding");
            var now = await ClockAsync(db, ct);
            op.Phase = "Completed"; op.ConfirmedRecordId = recordId; op.ReceiptCreatedAt = receiptCreatedAt;
            op.CompletedAt = now; op.FinalLogId = Guid.NewGuid(); op.ErrorKind = null;
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET review_status=3,reviewer_id={op.Actor},reviewed_at={now},updated_at={now},root_cause={op.RootCause},grade={op.Grade!.Value},subject={op.Subject!.Value} WHERE id={itemId}", ct);
            db.ProcessLogs.Add(new() { Id = op.FinalLogId.Value, MistakeItemId = itemId, StudentId = op.StudentId,
                Action = 4, CreatedAt = now, Details = "Legacy return completed; OriginalRetained" });
        }, ct);

    public Task ErrorAsync(Guid itemId, Guid owner, long fence, string errorKind, bool conflict = false, CancellationToken ct = default) =>
        WriteAsync(itemId, owner, fence, (_, op) =>
        {
            if (errorKind.Length > 64 || errorKind.Length == 0 || errorKind.Any(c => !(c is >= 'a' and <= 'z' or '_')))
                throw new InvalidOperationException("invalid_error_kind");
            op.ErrorKind = errorKind; if (conflict) op.Phase = "Conflict";
            return Task.CompletedTask;
        }, ct);

    private async Task WriteAsync(Guid itemId, Guid owner, long fence,
        Func<MistakeDbContext, LegacyReturnEntity, Task> write, CancellationToken ct)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockItemAsync(db, itemId, ct);
        var op = await db.LegacyReturns.SingleAsync(o => o.ItemId == itemId, ct);
        if (op.OwnerId != owner || op.Fence != fence || op.LeaseUntil is null || op.LeaseUntil <= await ClockAsync(db, ct)
            || op.Phase is "Completed" or "Conflict") throw new InvalidOperationException("lease_lost");
        await write(db, op);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<MistakeItemEntity> LockItemAsync(MistakeDbContext db, Guid itemId, CancellationToken ct) =>
        (await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE id={itemId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
        ?? throw new InvalidOperationException("item_not_found");
    private static Task<string> OriginalAsync(MistakeDbContext db, Guid itemId, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"SELECT jsonb_build_object('id',id,'studentId',student_id,'subject',subject,'grade',grade,'sourceUploadId',source_upload_id,'sourceRegions',source_regions,'reviewStatus',review_status,'reviewerId',reviewer_id,'reviewedAt',reviewed_at,'rootCause',root_cause,'questionId',question_id,'createdAt',created_at,'updatedAt',updated_at)::text AS \"Value\" FROM mistake_item WHERE id={itemId}").SingleAsync(ct);
    private static Task<DateTimeOffset> ClockAsync(MistakeDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
}
