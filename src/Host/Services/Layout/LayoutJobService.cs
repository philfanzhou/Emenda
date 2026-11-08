using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Host.Services.Intake;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Layout;

/// <summary>
/// Layout job persistence and durable execution primitives (IKQA2O), following the
/// database-clock lease/heartbeat/fencing conventions of ReanalyzeJobHostedService and
/// SourceIntakeService: claims bump a fencing version under an advisory lock, fenced
/// writes re-check owner + fence + lease inside the writing transaction, and inference
/// itself never runs inside this class. Accepted results and replay records are
/// insert-only; prior result revisions are retained and results never cross source
/// revisions.
/// </summary>
public sealed class LayoutJobService(DbContextOptions<MistakeDbContext> databaseOptions,
    LayoutCapabilityOptions capability, StructaDocLayoutOptions layoutOptions) : ILayoutJobService
{
    private const long ClaimLockKey = 0x52554F59554C4159;
    private const int LeaseSeconds = 90;
    private MistakeDbContext Open() => new(databaseOptions);

    public async Task<LayoutJobView> EnqueueAsync(Guid sourceId, Guid revision, Guid requestKey, string actor,
        CancellationToken ct = default)
    {
        if (requestKey == Guid.Empty || string.IsNullOrWhiteSpace(actor) || sourceId == Guid.Empty || revision == Guid.Empty)
            throw new LayoutJobException("invalid_request", 400);
        var canonical = LayoutJobCanonical.Request(sourceId, revision);

        // Replay first, without the capability: a reused key always returns the original
        // job and its captured configuration, even while new work is refused.
        await using (var db = Open())
        {
            var replay = await ReadReplayAsync(db, actor, requestKey, canonical, ct);
            if (replay != null) return replay;
        }
        if (!capability.Enabled) throw new LayoutJobException("layout_disabled", 503);

        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockRequestAsync(context, actor, requestKey, ct);
        await LockRevisionAsync(context, sourceId, revision, ct);
        var request = await context.LayoutRequests.SingleOrDefaultAsync(
            r => r.Actor == actor && r.Action == "create" && r.RequestKey == requestKey, ct);
        if (request != null)
        {
            if (request.Canonical != canonical) throw new LayoutJobException("request_payload_conflict");
            var replayed = await JobViewAsync(context, request.JobId, ct);
            await transaction.CommitAsync(ct);
            return replayed ?? throw new LayoutJobException("invalid_job_state", 503);
        }

        var source = await context.SourceIntakes.AsNoTracking()
            .SingleOrDefaultAsync(s => s.SourceUploadId == sourceId && s.ContentRevision == revision, ct)
            ?? throw new LayoutJobException("source_not_managed", 404);
        var manifest = JsonSerializer.Deserialize<SourceManifest>(source.Manifest, IntakeCanonical.Json)
            ?? throw new LayoutJobException("invalid_source_manifest", 503);
        IntakeCanonical.ValidateManifest(manifest, sourceId, revision, manifest.AcquiredBy);
        var rotations = LayoutGeometry.PreserveSnapshotRotations(manifest.ImageRotations);
        var images = manifest.Images
            .Select((image, index) => (Image: image, ManifestIndex: index))
            .Where(entry => entry.Image.Type == "mistake")
            .DistinctBy(entry => entry.Image.Path, StringComparer.Ordinal)
            .ToList();
        if (images.Count == 0) throw new LayoutJobException("no_layout_images", 400);

        var now = await DbNowAsync(context, ct);
        var nextRevision = (await context.LayoutJobs.AsNoTracking()
            .Where(job => job.SourceUploadId == sourceId && job.ContentRevision == revision)
            .MaxAsync(job => (int?)job.ResultRevision, ct) ?? 0) + 1;
        var jobId = Guid.NewGuid();
        context.LayoutJobs.Add(new LayoutJobEntity
        {
            JobId = jobId,
            SourceUploadId = sourceId,
            ContentRevision = revision,
            SnapshotId = manifest.SnapshotId,
            StudentId = manifest.StudentId,
            Actor = actor,
            ResultRevision = nextRevision,
            Status = LayoutJobCanonical.Queued,
            Manifest = JsonSerializer.Serialize(manifest, IntakeCanonical.Json),
            Config = LayoutJobCanonical.CaptureConfig(layoutOptions),
            CredentialRef = LayoutJobCanonical.CredentialReference,
            ImageRotations = JsonSerializer.Serialize(rotations, (JsonSerializerOptions?)null),
            OwnerId = Guid.NewGuid(),
            Fence = 0,
            LeaseUntil = null,
            Attempt = 0,
            CreatedAt = now
        });
        context.LayoutJobImages.AddRange(images.Select((entry, position) => new LayoutJobImageEntity
        {
            JobId = jobId,
            ImagePath = entry.Image.Path,
            Position = position,
            ExpectedLength = entry.Image.Length,
            ExpectedSha256 = entry.Image.Sha256,
            Status = LayoutJobCanonical.Pending,
            Attempts = 0,
            UpdatedAt = now
        }));
        context.LayoutRequests.Add(new LayoutRequestEntity
        {
            Actor = actor, Action = "create", RequestKey = requestKey,
            SourceUploadId = sourceId, ContentRevision = revision, Canonical = canonical, JobId = jobId
        });
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await JobViewAsync(context, jobId, ct) ?? throw new LayoutJobException("invalid_job_state", 503);
    }

    public async Task<LayoutJobView?> GetJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = Open();
        return await JobViewAsync(db, jobId, ct);
    }

    public async Task<List<LayoutJobView>> GetJobsAsync(Guid sourceId, Guid revision, CancellationToken ct = default)
    {
        await using var db = Open();
        var jobs = await db.LayoutJobs.AsNoTracking()
            .Where(job => job.SourceUploadId == sourceId && job.ContentRevision == revision)
            .OrderBy(job => job.ResultRevision).Select(job => job.JobId).ToListAsync(ct);
        var views = new List<LayoutJobView>();
        foreach (var jobId in jobs)
        {
            var view = await JobViewAsync(db, jobId, ct);
            if (view != null) views.Add(view);
        }
        return views;
    }

    public async Task<LayoutJobClaim?> ClaimAsync(CancellationToken ct = default)
    {
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);
        var jobs = await db.LayoutJobs.FromSqlInterpolated($"""
            SELECT * FROM layout_job
            WHERE (status={LayoutJobCanonical.Queued} OR (status={LayoutJobCanonical.Running}
              AND (lease_until IS NULL OR lease_until<=clock_timestamp())))
            ORDER BY created_at, job_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var job = jobs.FirstOrDefault();
        if (job == null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }
        job.Status = LayoutJobCanonical.Running;
        job.OwnerId = Guid.NewGuid();
        job.Fence++;
        job.Attempt++;
        job.LeaseUntil = await DbNowAsync(db, ct) + TimeSpan.FromSeconds(LeaseSeconds);
        await db.SaveChangesAsync(ct);
        var claim = new LayoutJobClaim(job.JobId, job.OwnerId, job.Fence,
            await JobViewAsync(db, job.JobId, ct) ?? throw new LayoutJobException("invalid_job_state", 503));
        await transaction.CommitAsync(ct);
        return claim;
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, Guid owner, long fence, CancellationToken ct = default)
    {
        await using var db = Open();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE layout_job SET lease_until=clock_timestamp()+({LeaseSeconds} * interval '1 second')
            WHERE job_id={jobId} AND owner_id={owner} AND fence={fence}
              AND status={LayoutJobCanonical.Running} AND lease_until>clock_timestamp()
            """, ct);
        return changed == 1;
    }

    public async Task<bool> PublishResultAsync(LayoutJobClaim claim, string imagePath, LayoutDetectionResult result,
        CancellationToken ct = default)
    {
        var violations = LayoutGeometry.ValidateLayoutResult(result);
        if (violations.Count > 0) throw new LayoutJobException("layout_result_invalid", 503);
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(db, claim, ct);
        if (job == null) return false;
        var image = await db.LayoutJobImages.SingleOrDefaultAsync(
            i => i.JobId == claim.JobId && i.ImagePath == imagePath, ct);
        if (image == null) return false;
        if (image.Status == LayoutJobCanonical.Succeeded) return false;
        if (!await db.LayoutResults.AsNoTracking().AnyAsync(
                r => r.JobId == claim.JobId && r.ImagePath == imagePath, ct))
        {
            // The accepted exchange itself is one attempt, counted symmetrically with
            // failed exchanges so the bounded budget reflects real inference work.
            image.Attempts++;
            var manifest = JsonSerializer.Deserialize<SourceManifest>(job.Manifest, IntakeCanonical.Json);
            var manifestIndex = manifest?.Images.FindIndex(i => i.Path == imagePath) ?? -1;
            var rotations = JsonSerializer.Deserialize<int[]>(job.ImageRotations, (JsonSerializerOptions?)null) ?? [];
            var now = await DbNowAsync(db, ct);
            var resultId = Guid.NewGuid();
            db.LayoutResults.Add(new LayoutResultEntity
            {
                ResultId = resultId,
                JobId = job.JobId,
                SourceUploadId = job.SourceUploadId,
                ContentRevision = job.ContentRevision,
                SnapshotId = job.SnapshotId,
                ResultRevision = job.ResultRevision,
                ImagePath = image.ImagePath,
                ImagePosition = image.Position,
                BytesLength = image.ExpectedLength,
                BytesSha256 = image.ExpectedSha256,
                SourceRotation = LayoutJobCanonical.SourceRotationOf(rotations, manifestIndex),
                Attempt = job.Attempt,
                UpstreamRequestId = result.RequestId,
                ContractId = LayoutJobCanonical.ContractId,
                CoordinateFrame = LayoutGeometry.CoordinateFrame,
                Width = result.Width,
                Height = result.Height,
                Orientation = result.Orientation,
                Engine = result.Engine,
                Model = result.Model,
                Version = result.Version,
                Diagnostics = JsonSerializer.Serialize(LayoutJobCanonical.BoundDiagnostics(result.Diagnostics),
                    (JsonSerializerOptions?)null),
                Config = job.Config,
                CreatedAt = now
            });
            db.LayoutResultRegions.AddRange(result.Regions.Select(region => new LayoutResultRegionEntity
            {
                ResultId = resultId, RegionId = region.Id, Label = region.Label,
                X0 = region.Box.X0, Y0 = region.Box.Y0, X1 = region.Box.X1, Y1 = region.Box.Y1
            }));
        }
        image.Status = LayoutJobCanonical.Succeeded;
        image.LastError = null;
        image.RetryNotBefore = null;
        image.UpdatedAt = await DbNowAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RecordImageFailureAsync(LayoutJobClaim claim, string imagePath, string errorKind,
        bool retryable, TimeSpan? retryAfter, CancellationToken ct = default)
    {
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(db, claim, ct);
        if (job == null) return false;
        var image = await db.LayoutJobImages.SingleOrDefaultAsync(
            i => i.JobId == claim.JobId && i.ImagePath == imagePath, ct);
        if (image == null) return false;
        if (image.Status == LayoutJobCanonical.Succeeded) return true;
        var now = await DbNowAsync(db, ct);
        image.Attempts = retryable ? image.Attempts + 1 : LayoutJobCanonical.MaxAttemptsPerImage;
        image.Status = LayoutJobCanonical.Failed;
        image.LastError = LayoutJobCanonical.BoundErrorKind(errorKind);
        image.RetryNotBefore = retryable ? now + LayoutJobCanonical.Backoff(image.Attempts, retryAfter) : null;
        image.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> CompleteJobAsync(LayoutJobClaim claim, CancellationToken ct = default)
    {
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(db, claim, ct);
        if (job == null) return false;
        var images = await db.LayoutJobImages.AsNoTracking().Where(i => i.JobId == claim.JobId).ToListAsync(ct);
        var allSucceeded = images.All(i => i.Status == LayoutJobCanonical.Succeeded);
        job.Status = allSucceeded ? LayoutJobCanonical.Succeeded : LayoutJobCanonical.Failed;
        job.ErrorKind = allSucceeded ? null
            : LayoutJobCanonical.BoundErrorKind(images.Where(i => i.Status != LayoutJobCanonical.Succeeded)
                .Select(i => i.LastError).FirstOrDefault(error => error != null) ?? "layout_failed");
        job.CompletedAt = await DbNowAsync(db, ct);
        job.LeaseUntil = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ReleaseAsync(LayoutJobClaim claim, CancellationToken ct = default)
    {
        await using var db = Open();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE layout_job SET status={LayoutJobCanonical.Queued}, lease_until=NULL
            WHERE job_id={claim.JobId} AND owner_id={claim.Owner} AND fence={claim.Fence}
            """, ct);
        return changed == 1;
    }

    private static async Task<LayoutJobEntity?> LockJobAsync(MistakeDbContext db, LayoutJobClaim claim,
        CancellationToken ct)
    {
        var job = (await db.LayoutJobs.FromSqlInterpolated(
                $"SELECT * FROM layout_job WHERE job_id={claim.JobId} FOR UPDATE").ToListAsync(ct))
            .FirstOrDefault();
        // Full ownership and lease re-check inside the writing transaction: a stale
        // attempt cannot publish, fail or complete after lease loss or supersession.
        var now = await DbNowAsync(db, ct);
        if (job == null || job.OwnerId != claim.Owner || job.Fence != claim.Fence ||
            job.LeaseUntil is not { } lease || lease <= now)
            return null;
        return job;
    }

    private static async Task<LayoutJobView?> ReadReplayAsync(MistakeDbContext db, string actor, Guid requestKey,
        string canonical, CancellationToken ct)
    {
        var request = await db.LayoutRequests.AsNoTracking().SingleOrDefaultAsync(
            r => r.Actor == actor && r.Action == "create" && r.RequestKey == requestKey, ct);
        if (request == null) return null;
        if (request.Canonical != canonical) throw new LayoutJobException("request_payload_conflict");
        return await JobViewAsync(db, request.JobId, ct) ?? throw new LayoutJobException("invalid_job_state", 503);
    }

    private static async Task<LayoutJobView?> JobViewAsync(MistakeDbContext db, Guid jobId, CancellationToken ct)
    {
        var job = await db.LayoutJobs.AsNoTracking().SingleOrDefaultAsync(j => j.JobId == jobId, ct);
        if (job == null) return null;
        var images = await db.LayoutJobImages.AsNoTracking().Where(i => i.JobId == jobId)
            .OrderBy(i => i.Position).ToListAsync(ct);
        var results = await db.LayoutResults.AsNoTracking().Where(r => r.JobId == jobId)
            .OrderBy(r => r.ImagePosition).ToListAsync(ct);
        var regions = await db.LayoutResultRegions.AsNoTracking()
            .Where(region => results.Select(r => r.ResultId).Contains(region.ResultId))
            .OrderBy(region => region.RegionId).ToListAsync(ct);
        return new LayoutJobView(job.JobId, job.SourceUploadId, job.ContentRevision, job.SnapshotId,
            job.StudentId, job.Actor, job.ResultRevision, job.Status, job.Config, job.CredentialRef,
            job.ImageRotations, job.CreatedAt, job.CompletedAt, job.ErrorKind,
            images.Select(i => new LayoutJobImageView(i.ImagePath, i.Position, i.ExpectedLength, i.ExpectedSha256,
                i.Status, i.Attempts, i.LastError, i.RetryNotBefore)).ToList(),
            results.Select(r => new LayoutResultView(r.ResultId, r.JobId, r.ImagePath, r.ResultRevision, r.Attempt,
                r.UpstreamRequestId, r.ContractId, r.CoordinateFrame, r.Width, r.Height, r.Orientation,
                r.Engine, r.Model, r.Version, r.SourceRotation, r.BytesLength, r.BytesSha256, r.Config,
                r.CreatedAt, JsonSerializer.Deserialize<string[]>(r.Diagnostics, (JsonSerializerOptions?)null) ?? [],
                regions.Where(region => region.ResultId == r.ResultId)
                    .Select(region => new LayoutResultRegionView(region.RegionId, region.Label,
                        region.X0, region.Y0, region.X1, region.Y1)).ToList())).ToList());
    }

    private static Task LockRequestAsync(MistakeDbContext db, string actor, Guid key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({IntakeCanonical.Encode(actor, "create", key.ToString("D"))},791253))", ct);

    private static Task LockRevisionAsync(MistakeDbContext db, Guid source, Guid revision, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({source.ToString("D") + "|" + revision.ToString("D")},791254))", ct);

    private static async Task<DateTimeOffset> DbNowAsync(MistakeDbContext db, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
}
