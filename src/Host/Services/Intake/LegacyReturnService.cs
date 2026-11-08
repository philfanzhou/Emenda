using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Services;
using Emenda.Service.Intake;
using Emenda.StudentClient;

namespace Emenda.Host.Services.Intake;

public sealed class LegacyReturnService(DbContextOptions<MistakeDbContext> databaseOptions,
    LegacyReturnOptions options, IOssService oss, SourceReturnClient client) : ILegacyReturnService
{
    private MistakeDbContext Open() => new(databaseOptions);
    private LegacyReturnJournal Journal() => new(databaseOptions);
    private static SourceReturnView View(LegacyReturnEntity op) => new(op.ItemId, op.OperationId,
        op.Phase, op.ConfirmedRecordId, op.CompletedAt, op.ErrorKind);

    public async Task<SourceReturnView?> GetAsync(Guid itemId, CancellationToken ct = default)
    {
        try { var op = await Journal().GetAsync(itemId, ct); return op is null ? null : View(op); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceIntakeException("database_unavailable", 503); }
    }

    public async Task<List<Guid>> PendingAsync(CancellationToken ct = default)
    {
        if (!options.Enabled) return [];
        await using var db = Open();
        return await db.LegacyReturns.AsNoTracking().Where(o => o.Phase == "Capturing" || o.Phase == "Prepared" || o.Phase == "Sending")
            .OrderBy(o => o.ItemId).Select(o => o.ItemId).ToListAsync(ct);
    }

    public async Task<SourceReturnView> ReturnAsync(Guid itemId, string actor, string? reason, string? cause,
        int grade, int subject, CancellationToken ct = default)
    {
        var journal = Journal();
        var owner = Guid.NewGuid();
        try
        {
            var prior = await journal.GetAsync(itemId, ct);
            if (prior is null)
            {
                if (!options.Enabled) throw new SourceIntakeException("legacy_returns_disabled", 503);
                await using var db = Open();
                var item = await db.MistakeItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, ct)
                    ?? throw new SourceIntakeException("item_not_found", 404);
                if (string.IsNullOrWhiteSpace(item.StudentId) || item.SourceRegions.Count == 0
                    || item.SourceRegions.Any(r => !ValidPath(r.SourceImagePath)))
                    throw new SourceIntakeException("invalid_return_source", 400);
            }
            // The caller's unique owner distinguishes its insertion from a concurrent replay.
            // Only a proven, live insertion lease may be released for acquisition by Resume.
            var saved = await journal.CreateAsync(itemId, actor, reason, cause, grade, subject, ct, owner,
                item => !string.IsNullOrWhiteSpace(item.StudentId) && item.SourceRegions.Count > 0
                    && item.SourceRegions.All(r => ValidPath(r.SourceImagePath)));
            if (saved.Phase == "Completed") return View(saved);
            if (saved.Phase == "Conflict") throw new SourceIntakeException("return_payload_conflict", 409);
            if (!options.Enabled) throw new SourceIntakeException("legacy_returns_disabled", 503);
            if (saved.OwnerId == owner) await journal.ReleaseAsync(itemId, owner, saved.Fence, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceIntakeException) { throw; }
        catch (InvalidOperationException error) when (KnownError(error.Message)) { throw Translate(error.Message); }
        catch (Exception)
        {
            // A missing COMMIT response grants no external write permission. Resume will
            // acquire a fresh owner only after the persisted insertion lease expires.
            throw new SourceIntakeException("return_commit_unknown", 503);
        }
        return await ResumeAsync(itemId, ct);
    }

    private static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path)
        && (path.StartsWith("uploads/", StringComparison.Ordinal) || path.StartsWith("mistakes/", StringComparison.Ordinal))
        && path.Split('/').All(s => s.Length > 0 && s is not "." and not "..")
        && !path.Contains('\\') && !ReservedSourceTargetPath.IsReserved(path)
        && !path.StartsWith("uploads/mistake-returns/", StringComparison.Ordinal);

    public async Task<SourceReturnView> ResumeAsync(Guid itemId, CancellationToken ct = default)
    {
        var journal = Journal();
        LegacyReturnEntity op;
        try
        {
            op = await journal.GetAsync(itemId, ct) ?? throw new SourceIntakeException("return_not_found", 404);
            if (op.Phase == "Completed") return View(op);
            if (op.Phase == "Conflict") throw new SourceIntakeException("return_payload_conflict", 409);
            if (!options.Enabled) throw new SourceIntakeException("legacy_returns_disabled", 503);
            var acquired = await journal.AcquireAsync(itemId, ct);
            if (acquired is null) return View(await journal.GetAsync(itemId, ct) ?? op);
            op = acquired;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceIntakeException) { throw; }
        catch (Exception) { throw new SourceIntakeException("return_commit_unknown", 503); }

        // Stage reads may refresh progress, never the ownership acquired by this call.
        var lease = (op.OwnerId, op.Fence);
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(itemId, lease, processing, stop.Token);
        var token = processing.Token;
        try
        {
            var objects = await ObjectsAsync(itemId, token);
            if (op.Phase == "Capturing")
            {
                foreach (var obj in objects)
                {
                    var bytes = await BytesAsync(obj.SourcePath, token);
                    if (obj.ExpectedLength.HasValue && (obj.ExpectedLength != bytes.Length || obj.ExpectedSha256 != bytes.Hash))
                        throw new SourceIntakeException("return_source_changed", 503);
                    await journal.CaptureAsync(itemId, lease.OwnerId, lease.Fence, obj.Position, bytes.Length, bytes.Hash, token);
                }
                await journal.PrepareAsync(itemId, lease.OwnerId, lease.Fence, token);
                objects = await ObjectsAsync(itemId, token);
            }
            op = await RefreshAsync(itemId, lease, token);
            if (op.Phase == "Completed") return View(op);
            if (op.Phase == "Prepared")
            {
                foreach (var obj in objects) await CopyAsync(op, obj, lease, token);
                objects = await ObjectsAsync(itemId, token);
                var candidates = await AttemptsAsync(itemId, token);
                if (objects.Any(o => !o.SelectedAttemptId.HasValue)) throw new SourceIntakeException("return_objects_pending", 503);
                var payload = new CreateReturnedRecordRequest
                {
                    OperationId = op.OperationId, Comments = "Rejected", ReturnReason = op.ReturnReason,
                    ImageEntries = objects.Select(o => new ImageEntryDto { Path = candidates.Single(a => a.AttemptId == o.SelectedAttemptId).ObjectKey, Type = "mistake" }).ToList()
                };
                await journal.SendAsync(itemId, lease.OwnerId, lease.Fence, JsonSerializer.Serialize(payload, IntakeCanonical.Json),
                    SourceReturnClient.Digest(op.StudentId, payload), token);
                op = await RefreshAsync(itemId, lease, token);
                if (op.Phase == "Completed") return View(op);
            }
            await EnsureLeaseAsync(itemId, lease, token);
            var receipt = await client.ReadAsync(op.OperationId, token);
            if (receipt is null)
            {
                var payload = JsonSerializer.Deserialize<CreateReturnedRecordRequest>(op.Payload!, IntakeCanonical.Json)
                    ?? throw new SourceIntakeException("return_payload_conflict", 409);
                if (payload.OperationId != op.OperationId || SourceReturnClient.Digest(op.StudentId, payload) != op.PayloadDigest)
                    throw new SourceIntakeException("return_payload_conflict", 409);
                foreach (var obj in await ObjectsAsync(itemId, token))
                {
                    var selected = (await AttemptsAsync(itemId, token)).Single(a => a.AttemptId == obj.SelectedAttemptId);
                    if (selected.Phase != "Verified" || selected.CopySettledAt is null || !await MatchesAsync(selected.ObjectKey, obj, token))
                        throw new SourceIntakeException("return_selected_bytes_unavailable", 503);
                }
                await EnsureLeaseAsync(itemId, lease, token);
                await client.PostAsync(op.StudentId, payload, token);
                receipt = await client.ReadAsync(op.OperationId, token);
                if (receipt is null)
                {
                    await journal.ErrorAsync(itemId, lease.OwnerId, lease.Fence, "return_receipt_pending", ct: token);
                    return View(await journal.GetAsync(itemId, token) ?? op);
                }
            }
            if (receipt.OperationId != op.OperationId || receipt.StudentId != op.StudentId || receipt.UploadRecordId == Guid.Empty
                || receipt.DigestVersion != "returned-payload-v1" || receipt.PayloadDigest != op.PayloadDigest
                || !DateTimeOffset.TryParse(receipt.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                || time.Offset != TimeSpan.Zero || time.Year < 1970)
                throw new SourceIntakeException("return_receipt_conflict", 409);
            await journal.CompleteAsync(itemId, lease.OwnerId, lease.Fence, receipt.OperationId, receipt.StudentId,
                receipt.UploadRecordId, time, receipt.DigestVersion, receipt.PayloadDigest, token);
            return View(await journal.GetAsync(itemId, token) ?? throw new SourceIntakeException("return_commit_unknown", 503));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SourceIntakeException("lease_lost", 409); }
        catch (SourceIntakeException error)
        {
            try { await journal.ErrorAsync(itemId, lease.OwnerId, lease.Fence, error.ErrorKind,
                error.ErrorKind is "return_receipt_conflict" or "return_payload_conflict", CancellationToken.None); }
            catch (Exception) { }
            throw;
        }
        catch (InvalidOperationException error) when (KnownError(error.Message)) { throw Translate(error.Message); }
        catch (Exception)
        {
            var saved = await GetAsync(itemId, CancellationToken.None);
            if (saved?.Success == true) return saved;
            throw new SourceIntakeException("return_commit_unknown", 503);
        }
        finally
        {
            stop.Cancel(); await heartbeat;
            try { await journal.ReleaseAsync(itemId, lease.OwnerId, lease.Fence, CancellationToken.None); } catch (Exception) { }
        }
    }

    private async Task<LegacyReturnEntity> RefreshAsync(Guid itemId, (Guid OwnerId, long Fence) lease, CancellationToken ct)
    {
        var saved = await Journal().GetAsync(itemId, ct) ?? throw new SourceIntakeException("return_not_found", 404);
        if (saved.Phase == "Completed") return saved;
        if (saved.OwnerId != lease.OwnerId || saved.Fence != lease.Fence || saved.LeaseUntil is null)
            throw new SourceIntakeException("lease_lost", 409);
        await using var db = Open();
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (saved.LeaseUntil <= now) throw new SourceIntakeException("lease_lost", 409);
        return saved;
    }
    private async Task HeartbeatAsync(Guid itemId, (Guid OwnerId, long Fence) lease, CancellationTokenSource processing, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(stop))
                if (!options.Enabled || !await Journal().RenewAsync(itemId, lease.OwnerId, lease.Fence, stop)) { processing.Cancel(); return; }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { processing.Cancel(); }
    }
    private async Task EnsureLeaseAsync(Guid itemId, (Guid OwnerId, long Fence) lease, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.Enabled || !await Journal().RenewAsync(itemId, lease.OwnerId, lease.Fence, ct))
            throw new SourceIntakeException("lease_lost", 409);
    }
    private async Task<List<LegacyReturnObjectEntity>> ObjectsAsync(Guid item, CancellationToken ct)
    {
        await using var db = Open();
        return await db.LegacyReturnObjects.AsNoTracking().Where(o => o.ItemId == item).OrderBy(o => o.Position).ToListAsync(ct);
    }
    private async Task<List<LegacyReturnAttemptEntity>> AttemptsAsync(Guid item, CancellationToken ct)
    {
        await using var db = Open();
        return await db.LegacyReturnAttempts.AsNoTracking().Where(a => a.ItemId == item).ToListAsync(ct);
    }
    private async Task<(long Length, string Hash)> BytesAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var download = oss.DownloadAsync(path);
            Stream stream;
            try { stream = await download.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _ = download.ContinueWith(completed =>
                {
                    if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                    else _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            using var retainedStream = stream;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; long length = 0; int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0) { hash.AppendData(buffer, 0, count); length += count; }
            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceIntakeException("return_bytes_unavailable", 503); }
    }
    private async Task<bool> MatchesAsync(string path, LegacyReturnObjectEntity obj, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, ct);
        return bytes.Length == obj.ExpectedLength && bytes.Hash == obj.ExpectedSha256;
    }
    private async Task CopyAsync(LegacyReturnEntity op, LegacyReturnObjectEntity obj, (Guid OwnerId, long Fence) lease, CancellationToken ct)
    {
        if (obj.SelectedAttemptId.HasValue) return;
        var journal = Journal();
        foreach (var candidate in (await AttemptsAsync(op.ItemId, ct)).Where(a => a.Position == obj.Position && a.Phase == "CopySettled" && a.CopySettledAt.HasValue))
        {
            bool matches;
            try { matches = await MatchesAsync(candidate.ObjectKey, obj, ct); }
            catch (SourceIntakeException error) when (error.ErrorKind == "return_bytes_unavailable") { matches = false; }
            if (matches)
            { await journal.SelectAsync(op.ItemId, lease.OwnerId, lease.Fence, candidate.AttemptId, obj.ExpectedLength!.Value, obj.ExpectedSha256!, ct); return; }
        }
        if (!await MatchesAsync(obj.SourcePath, obj, ct)) throw new SourceIntakeException("return_source_changed", 503);
        await EnsureLeaseAsync(op.ItemId, lease, ct);
        var attempt = await journal.ReserveAsync(op.ItemId, lease.OwnerId, lease.Fence, obj.Position, ct);
        await EnsureLeaseAsync(op.ItemId, lease, ct);
        try { await oss.CopyObjectAsync(obj.SourcePath, attempt.ObjectKey).WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            try { await journal.UnknownAsync(op.ItemId, lease.OwnerId, lease.Fence, attempt.AttemptId, CancellationToken.None); } catch (Exception) { }
            throw new SourceIntakeException("return_copy_unknown", 503);
        }
        ct.ThrowIfCancellationRequested();
        try { await journal.SettleAsync(op.ItemId, lease.OwnerId, lease.Fence, attempt.AttemptId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidOperationException error) when (KnownError(error.Message)) { throw Translate(error.Message); }
        catch (Exception)
        {
            var marker = (await AttemptsAsync(op.ItemId, ct)).Single(a => a.AttemptId == attempt.AttemptId);
            if (marker.Phase != "CopySettled" || marker.CopySettledAt is null) throw new SourceIntakeException("return_copy_settle_unknown", 503);
        }
        if (!await MatchesAsync(attempt.ObjectKey, obj, ct)) throw new SourceIntakeException("return_copy_bytes_unavailable", 503);
        await journal.SelectAsync(op.ItemId, lease.OwnerId, lease.Fence, attempt.AttemptId, obj.ExpectedLength!.Value, obj.ExpectedSha256!, ct);
    }
    private static bool KnownError(string value) => value is "item_not_found" or "return_not_pending" or "reanalysis_active" or "invalid_return_source"
        or "return_payload_conflict" or "lease_lost" or "invalid_request" or "return_source_changed";
    private static SourceIntakeException Translate(string value) => new(value, value == "item_not_found" ? 404 : value is "invalid_request" or "invalid_return_source" ? 400 : value == "return_source_changed" ? 503 : 409);
}
