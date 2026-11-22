using Emenda.Database;
using Emenda.Database.Entities;
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
            .OrderBy(o => o.NextAttempt).Select(o => o.JobId).Take(20).ToListAsync(ct);
        foreach (var id in jobs.Distinct()) await CollectJobAsync(id, ct);
    }

    private async Task CollectJobAsync(Guid id, CancellationToken ct)
    {
        await using var jobLock = await SerializeJobAsync(id, ct);
        await using var db = Open();
        var job = await db.ExtractionJobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return;
        var snapshot = job.Version == 0 ? new ExtractionSnapshot([]) : ExtractionRules.Read<ExtractionSnapshot>(
            (await db.ExtractionRevisions.AsNoTracking().SingleAsync(r => r.JobId == id && r.Version == job.Version, ct)).SnapshotJson);
        var itemIds = snapshot.Questions.Where(q => q.ItemId.HasValue).Select(q => q.ItemId!.Value).ToArray();
        var existing = await db.MistakeItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        if (snapshot.Questions.Any(q => q.Images.Count > 0 && q.ItemId.HasValue && !existing.Contains(q.ItemId.Value)))
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
            .Concat(Sources(job).Select(s => s.Path)).ToHashSet(StringComparer.Ordinal);
        // Independent practice images and any shared current mistake references must also survive.
        await foreach (var item in db.MistakeItems.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            referenced.UnionWith(item.SourceRegions.Select(r => r.SourceImagePath));
        await foreach (var practice in db.MistakePractices.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            referenced.UnionWith(practice.ImagePaths);
        var now = await NowAsync(db, ct);
        var objects = await db.ExtractionObjects.Where(o => o.JobId == id && o.NextAttempt <= now
            && (!o.Legacy || options.Value.CleanupLegacyObjects)).Take(100).ToListAsync(ct);
        foreach (var entry in objects)
        {
            await using var objectTransaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({entry.Path},0))", ct);
            // The database triggers fence new references against this final check and deletion.
            var independentlyReferenced = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM mistake_item m, jsonb_array_elements(m.source_regions) r
                    WHERE r->>'sourceImagePath'={entry.Path})
                    OR EXISTS(SELECT 1 FROM mistake_practice p WHERE p.image_paths @> jsonb_build_array({entry.Path}::text)) AS "Value"
                """).SingleAsync(ct);
            if (referenced.Contains(entry.Path) || independentlyReferenced)
            {
                entry.Pending = false;
                entry.NextAttempt = now.AddMinutes(1);
                await db.SaveChangesAsync(ct);
                await objectTransaction.CommitAsync(ct);
                continue;
            }
            entry.Pending = true;
            // Never allow the queue to target anything outside derived/staged objects of this upload.
            var prefix = $"mistakes/extractions/{id:N}/";
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal) || entry.Path[prefix.Length..].StartsWith("originals/", StringComparison.Ordinal))
                throw new ExtractionException("cleanup_path_conflict", 503);
            // Persist rejection of new references BEFORE an irreversible storage delete.
            // Cancellation or a failed final receipt can then leave only a retryable pending row.
            await db.SaveChangesAsync(ct);
            await objectTransaction.CommitAsync(ct);
            bool deleted;
            try { deleted = await oss.DeleteDerivedAsync(entry.Path); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning("Extraction cleanup failed {JobId} {ErrorType}", id, error.GetType().Name); deleted = false; }
            if (deleted) db.ExtractionObjects.Remove(entry);
            else
            {
                entry.Attempts = Math.Min(entry.Attempts + 1, 20);
                entry.NextAttempt = now.AddSeconds(Math.Min(3600, 5 * (1 << Math.Min(entry.Attempts, 9))));
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
