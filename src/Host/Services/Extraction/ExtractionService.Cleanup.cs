using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private async Task<IAsyncDisposable> SerializeJobAsync(Guid id, CancellationToken ct)
    {
        var db = Open();
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            var key = ExtractionRules.LockKey("extraction-storage:" + id);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({key})", ct);
            return new JobLock(db, key);
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private sealed class JobLock(MistakeDbContext db, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({key})"); }
            finally { await db.DisposeAsync(); }
        }
    }

    private async Task TrackObjectAsync(Guid jobId, string path, CancellationToken ct)
    {
        // Commit the cleanup intent before a PUT; an interrupted publisher cannot leave an untracked object.
        await using var db = Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO extraction_object(path,job_id,pending,legacy,attempts,next_attempt) VALUES ({path},{jobId},true,false,0,clock_timestamp()) ON CONFLICT(path) DO NOTHING", ct);
    }

    public async Task RunCleanupAsync(CancellationToken ct = default)
    {
        await using var db = Open();
        var now = await NowAsync(db, ct);
        var jobs = await db.ExtractionObjects.Where(o => o.NextAttempt <= now && (!o.Legacy || options.Value.CleanupLegacyObjects))
            .GroupBy(o => o.JobId).OrderBy(g => g.Min(o => o.NextAttempt)).Select(g => g.Key).Take(20).ToListAsync(ct);
        foreach (var id in jobs) await CollectJobAsync(id, ct);
    }

    private static IEnumerable<string> StorageFamily(string path) => ThumbnailHelper.GetAllThumbnailPaths(path).Prepend(path);

    private static Task<List<string>> RetainedReferencesAsync(MistakeDbContext db, string[] paths, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT r->>'sourceImagePath' AS "Value" FROM mistake_item m, jsonb_array_elements(m.source_regions) r
                WHERE r->>'sourceImagePath'=ANY({paths})
            UNION
            SELECT path FROM mistake_practice p, jsonb_array_elements_text(p.image_paths) path
                WHERE path=ANY({paths})
            """).ToListAsync(ct);

    private static string JobStoragePrefix(Guid id) => $"mistakes/extractions/{id:N}/";

    private async Task CollectJobAsync(Guid id, CancellationToken ct)
    {
        await using var jobLock = await SerializeJobAsync(id, ct);
        await using var db = Open();
        var job = await db.ExtractionJobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        var deleted = job is null && await db.ExtractionDeletions.AnyAsync(d => d.JobId == id, ct);
        if (job is null && !deleted) return;
        var snapshot = job is null || job.Version == 0 ? new ExtractionSnapshot([]) : ExtractionRules.Read<ExtractionSnapshot>(
            (await db.ExtractionRevisions.AsNoTracking().SingleAsync(r => r.JobId == id && r.Version == job.Version, ct)).SnapshotJson);
        var itemIds = snapshot.Questions.Where(q => q.ItemId.HasValue).Select(q => q.ItemId!.Value).ToArray();
        var existing = await db.MistakeItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        if (job is not null && snapshot.Questions.Any(q => q.Images.Count > 0 && q.ItemId.HasValue && !existing.Contains(q.ItemId.Value)))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, id.ToString(), ct);
            var pruned = new ExtractionSnapshot(snapshot.Questions.Select(q => q.ItemId.HasValue && !existing.Contains(q.ItemId.Value)
                ? q with { Images = [], ItemDeleted = true } : q).ToList());
            await PublishExtractionAsync(db, job, pruned, snapshot, Guid.NewGuid(), "deleted_item_cleanup",
                "cleanup", "retire_deleted_item_crops", "cleanup", ct);
            await tx.CommitAsync(ct);
            snapshot = pruned;
        }
        var referenced = snapshot.Questions.SelectMany(q => q.Images).Select(i => i.Path)
            .Concat(job is null ? [] : Sources(job).Select(s => s.Path)).ToHashSet(StringComparer.Ordinal);
        var prefix = JobStoragePrefix(id);
        var now = await NowAsync(db, ct);
        var objects = await db.ExtractionObjects.Where(o => o.JobId == id && o.NextAttempt <= now
            && (!o.Legacy || options.Value.CleanupLegacyObjects)).OrderBy(o => o.NextAttempt).ThenBy(o => o.Path).Take(100).ToListAsync(ct);
        foreach (var entry in objects)
        {
            ct.ThrowIfCancellationRequested();
            await using var objectTransaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({entry.Path},0))", ct);
            // The database triggers fence new references against this final check and deletion.
            if (referenced.Contains(entry.Path) ||
                (await RetainedReferencesAsync(db, StorageFamily(entry.Path).ToArray(), ct)).Count != 0)
            {
                if (job?.Status != "Staging") entry.Pending = false;
                entry.Attempts = 0;
                entry.NextAttempt = now.AddMinutes(1);
                await db.SaveChangesAsync(ct);
                await objectTransaction.CommitAsync(ct);
                continue;
            }
            var suffix = entry.Path.StartsWith(prefix, StringComparison.Ordinal) ? entry.Path[prefix.Length..] : "";
            var original = deleted && suffix.StartsWith("originals/", StringComparison.Ordinal);
            // This is a queue of known object keys, never an instruction to purge an arbitrary prefix.
            var safe = suffix.Length > 0 && !suffix.Contains('\\') &&
                !suffix.Split('/').Any(segment => segment is "" or "." or "..");
            var derived = suffix.StartsWith("versions/", StringComparison.Ordinal) || suffix.StartsWith("supplements/", StringComparison.Ordinal);
            entry.Pending = true;
            // Persist rejection of new references BEFORE an irreversible storage delete.
            // Cancellation or a failed final receipt can then leave only a retryable pending row.
            await db.SaveChangesAsync(ct);
            await objectTransaction.CommitAsync(ct);
            var removed = false;
            try
            {
                if (safe && original) removed = await oss.DeleteExtractionOriginalAsync(entry.Path);
                else if (safe && derived) removed = await oss.DeleteDerivedAsync(entry.Path);
                else logger.LogWarning("Extraction cleanup refused an out-of-scope object for {JobId}", id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning("Extraction cleanup failed {JobId} {ErrorType}", id, error.GetType().Name); }
            if (removed) db.ExtractionObjects.Remove(entry);
            else
            {
                entry.Attempts = Math.Min(entry.Attempts + 1, 20);
                entry.NextAttempt = (await NowAsync(db, ct)).AddSeconds(Math.Min(3600, 5 * (1 << Math.Min(entry.Attempts, 9))));
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
