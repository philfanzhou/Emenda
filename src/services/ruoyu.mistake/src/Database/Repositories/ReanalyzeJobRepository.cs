using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;

namespace Ruoyu.Study.Mistake.Database.Repositories;

public class ReanalyzeJobRepository : IReanalyzeJobRepository
{
    private const long ClaimLockKey = 0x52554F5955524541;
    private const int MaxConcurrency = 2;
    private const int MaxAttempts = 3;
    private const int LeaseSeconds = 90;
    private readonly MistakeDbContext _db;

    public ReanalyzeJobRepository(MistakeDbContext db) => _db = db;

    public async Task AddAsync(ReanalyzeJobModel model, CancellationToken ct = default)
    {
        await LegacyReturnWriteGuard.EnsureAsync(_db, model.ItemId, ct);
        await _db.ReanalyzeJobs.AddAsync(ToEntity(model), ct);
    }

    public async Task<ReanalyzeJobModel?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await _db.ReanalyzeJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct)) is { } job
            ? ToModel(job) : null;

    public async Task<List<ReanalyzeJobModel>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var keys = ids.Distinct().ToArray();
        return (await _db.ReanalyzeJobs.AsNoTracking().Where(j => keys.Contains(j.Id)).ToListAsync(ct))
            .Select(ToModel).ToList();
    }

    public async Task<List<ReanalyzeJobModel>> GetActiveByReviewerAsync(string reviewerId, CancellationToken ct = default) =>
        (await _db.ReanalyzeJobs.AsNoTracking().Where(j => j.ReviewerId == reviewerId &&
            (j.Status == 1 || j.Status == 2)).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToListAsync(ct))
            .Select(ToModel).ToList();

    public async Task<List<ReanalyzeJobModel>> ClaimAsync(int maxCount, CancellationToken ct = default)
    {
        var claimed = new List<ReanalyzeJobModel>();
        if (maxCount <= 0) return claimed;
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);
        while (claimed.Count < Math.Min(maxCount, MaxConcurrency))
        {
            var active = await _db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::integer AS \"Value\" FROM reanalyze_job WHERE status=2 AND lease_until>clock_timestamp()")
                .SingleAsync(ct);
            if (active >= MaxConcurrency) break;
            var jobs = await _db.ReanalyzeJobs.FromSqlRaw("""
                SELECT j.* FROM reanalyze_job j
                WHERE (j.status=1 OR (j.status=2 AND (j.lease_until IS NULL OR j.lease_until<=clock_timestamp())))
                  AND NOT EXISTS (SELECT 1 FROM legacy_return lr WHERE lr.item_id=j.item_id)
                  AND NOT EXISTS (
                    SELECT 1 FROM reanalyze_job earlier
                    WHERE earlier.item_id=j.item_id AND earlier.status IN (1,2)
                      AND (earlier.created_at,earlier.id)<(j.created_at,j.id))
                ORDER BY j.created_at,j.id LIMIT 1 FOR UPDATE OF j SKIP LOCKED
                """).ToListAsync(ct);
            var job = jobs.FirstOrDefault();
            if (job is null) break;
            var item = (await _db.MistakeItems.FromSqlInterpolated(
                $"SELECT * FROM mistake_item WHERE id={job.ItemId} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
            if (job.Attempt >= MaxAttempts || item is null || !Allowed(item))
            {
                await FailWithoutOwnerAsync(job, item, job.Attempt >= MaxAttempts ? "attempt_limit" : "input_unavailable", ct);
                continue;
            }
            if (job.Status == (int)ReanalyzeJobState.Running && job.ExecutionInputDigest != Digest(item))
            {
                await FailWithoutOwnerAsync(job, item, "input_changed", ct);
                continue;
            }
            var now = await ClockAsync(ct);
            job.Status = (int)ReanalyzeJobState.Running;
            job.OwnerId = Guid.NewGuid();
            job.FencingVersion++;
            job.Attempt++;
            job.LeaseUntil = now.AddSeconds(LeaseSeconds);
            job.StartedAt = now;
            job.ExecutionRootCause = item.RootCause;
            job.ExecutionInputDigest = Digest(item);
            await _db.SaveChangesAsync(ct);
            var model = ToModel(job);
            model.ExecutionStudentId = item.StudentId;
            model.ExecutionImagePaths = item.SourceRegions.Select(r => r.SourceImagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            claimed.Add(model);
        }
        await transaction.CommitAsync(ct);
        return claimed;
    }

    public async Task<bool> RenewAsync(Guid jobId, Guid ownerId, long version, CancellationToken ct = default)
    {
        return await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reanalyze_job SET lease_until=clock_timestamp()+interval '90 seconds'
            WHERE id={jobId} AND owner_id={ownerId} AND fencing_version={version}
              AND status=2 AND lease_until>clock_timestamp()
            """, ct) == 1;
    }

    public async Task<bool> CompleteAsync(Guid jobId, Guid ownerId, long version,
        string rootCause, long durationMs, CancellationToken ct = default)
    {
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var job = await LockedJobAsync(jobId, ct);
        if (!ValidOwner(job, ownerId, version, await ClockAsync(ct))) return false;
        var item = await LockedItemAsync(job!.ItemId, ct);
        if (item is null || !Allowed(item) || job.ExecutionInputDigest != Digest(item))
        {
            var reason = item is null ? "item_deleted" : !Allowed(item) ? "item_rejected" : "input_changed";
            await CommitFailureAsync(job, ownerId, version, item, reason, ct);
            await transaction.CommitAsync(ct);
            return false;
        }
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reanalyze_job SET status=3, new_root_cause={rootCause}, vl_duration_ms={durationMs},
                completed_at=clock_timestamp(), owner_id=NULL, lease_until=NULL
            WHERE id={jobId} AND owner_id={ownerId} AND fencing_version={version}
              AND status=2 AND lease_until>clock_timestamp()
            """, ct);
        if (rows != 1) return false;
        item.RootCause = rootCause;
        item.UpdatedAt = await ClockAsync(ct);
        await AddLogAsync(job, item, rootCause, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> FailAsync(Guid jobId, Guid ownerId, long version,
        string reasonCode, CancellationToken ct = default)
    {
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var job = await LockedJobAsync(jobId, ct);
        if (!ValidOwner(job, ownerId, version, await ClockAsync(ct))) return false;
        var item = await LockedItemAsync(job!.ItemId, ct);
        var result = await CommitFailureAsync(job, ownerId, version, item, SanitizeReason(reasonCode), ct);
        if (result) await transaction.CommitAsync(ct);
        return result;
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        _db.ReanalyzeJobs.Where(j => (j.Status == 3 || j.Status == 4) &&
            j.CompletedAt != null && j.CompletedAt < cutoff && !_db.LegacyReturns.Any(o => o.ItemId == j.ItemId)).ExecuteDeleteAsync(ct);

    private async Task<ReanalyzeJobEntity?> LockedJobAsync(Guid id, CancellationToken ct) =>
        (await _db.ReanalyzeJobs.FromSqlInterpolated(
            $"SELECT * FROM reanalyze_job WHERE id={id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();

    private async Task<MistakeItemEntity?> LockedItemAsync(Guid id, CancellationToken ct) =>
        (await _db.MistakeItems.FromSqlInterpolated(
            $"SELECT * FROM mistake_item WHERE id={id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();

    private async Task<DateTimeOffset> ClockAsync(CancellationToken ct) =>
        await _db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private static bool ValidOwner(ReanalyzeJobEntity? job, Guid owner, long version, DateTimeOffset now) =>
        job is { Status: 2 } && job.OwnerId == owner && job.FencingVersion == version && job.LeaseUntil > now;

    private static bool Allowed(MistakeItemEntity item) =>
        (item.ReviewStatus == (int)ReviewStatus.PendingReview || item.ReviewStatus == (int)ReviewStatus.Confirmed)
        && item.SourceRegions.Count > 0;

    private static string Digest(MistakeItemEntity item)
    {
        var input = JsonSerializer.Serialize(new { item.StudentId, item.SourceRegions, item.RootCause, item.ReviewStatus });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string SanitizeReason(string reason) => reason switch
    {
        "vl_failed" or "vl_empty" or "item_deleted" or "item_rejected" or "input_changed" or
            "input_unavailable" or "attempt_limit" or "worker_failed" => reason,
        _ => "worker_failed"
    };

    private async Task<bool> CommitFailureAsync(ReanalyzeJobEntity job, Guid ownerId, long version,
        MistakeItemEntity? item, string reason, CancellationToken ct)
    {
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reanalyze_job SET status=4, error_message={reason}, completed_at=clock_timestamp(),
                owner_id=NULL, lease_until=NULL
            WHERE id={job.Id} AND owner_id={ownerId} AND fencing_version={version}
              AND status=2 AND lease_until>clock_timestamp()
            """, ct);
        if (rows != 1) return false;
        if (item is not null) await AddLogAsync(job, item, $"FAILED: {reason}", ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task FailWithoutOwnerAsync(ReanalyzeJobEntity job, MistakeItemEntity? item,
        string reason, CancellationToken ct)
    {
        job.Status = 4;
        job.ErrorMessage = reason;
        job.CompletedAt = await ClockAsync(ct);
        job.OwnerId = null;
        job.LeaseUntil = null;
        if (item is not null) await AddLogAsync(job, item, $"FAILED: {reason}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task AddLogAsync(ReanalyzeJobEntity job, MistakeItemEntity item, string result, CancellationToken ct)
    {
        await _db.ProcessLogs.AddAsync(new ProcessLogEntity
        {
            Id = Guid.NewGuid(), MistakeItemId = item.Id, StudentId = item.StudentId,
            Action = (int)ProcessAction.Reanalyzed,
            Details = JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                teacher_description_provided = !string.IsNullOrWhiteSpace(job.TeacherDescription),
                teacher_description_length = job.TeacherDescription?.Length ?? 0,
                new_root_cause = result
            }),
            CreatedAt = await ClockAsync(ct)
        }, ct);
    }

    private static ReanalyzeJobEntity ToEntity(ReanalyzeJobModel m) => new()
    {
        Id=m.Id, ItemId=m.ItemId, ReviewerId=m.ReviewerId, StudentId=m.StudentId,
        TeacherDescription=m.TeacherDescription, PreviousRootCause=m.PreviousRootCause,
        Status=(int)m.Status, ErrorMessage=m.ErrorMessage, NewRootCause=m.NewRootCause,
        CreatedAt=m.CreatedAt, StartedAt=m.StartedAt, CompletedAt=m.CompletedAt,
        VlDurationMs=m.VlDurationMs, OwnerId=m.OwnerId, LeaseUntil=m.LeaseUntil,
        FencingVersion=m.FencingVersion, Attempt=m.Attempt,
        ExecutionRootCause=m.ExecutionRootCause, ExecutionInputDigest=m.ExecutionInputDigest
    };

    private static ReanalyzeJobModel ToModel(ReanalyzeJobEntity e) => new()
    {
        Id=e.Id, ItemId=e.ItemId, ReviewerId=e.ReviewerId, StudentId=e.StudentId,
        TeacherDescription=e.TeacherDescription, PreviousRootCause=e.PreviousRootCause,
        Status=(ReanalyzeJobState)e.Status, ErrorMessage=e.ErrorMessage, NewRootCause=e.NewRootCause,
        CreatedAt=e.CreatedAt, StartedAt=e.StartedAt, CompletedAt=e.CompletedAt,
        VlDurationMs=e.VlDurationMs, OwnerId=e.OwnerId, LeaseUntil=e.LeaseUntil,
        FencingVersion=e.FencingVersion, Attempt=e.Attempt,
        ExecutionRootCause=e.ExecutionRootCause, ExecutionInputDigest=e.ExecutionInputDigest
    };
}
