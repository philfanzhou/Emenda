using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class StartReanalyzeJobResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public ReanalyzeJobModel? Job { get; set; }
}

public interface IReanalyzeJobService
{
    /// <summary>
    /// Synchronous path: validate item state, snapshot previous root_cause, persist
    /// a new Job with status=Pending. Does NOT call VL.
    /// </summary>
    Task<StartReanalyzeJobResult> StartJobAsync(
        Guid itemId,
        string reviewerId,
        string studentId,
        string? teacherDescription,
        CancellationToken ct = default);

    /// <summary>
    /// Batch status query filtered by reviewer ownership. Unknown jobIds are silently
    /// dropped; only jobs owned by the caller are returned.
    /// </summary>
    Task<List<ReanalyzeJobModel>> GetJobsAsync(
        string reviewerId,
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct = default);

    /// <summary>
    /// All Pending/Running jobs owned by the caller. Used to recover badges after
    /// a page reload.
    /// </summary>
    Task<List<ReanalyzeJobModel>> GetActiveJobsAsync(
        string reviewerId,
        CancellationToken ct = default);

    /// <summary>
    /// Worker entry: pull up to <paramref name="maxCount"/> oldest Pending jobs from DB.
    /// </summary>
    Task<List<ReanalyzeJobModel>> ClaimPendingJobsAsync(
        int maxCount,
        CancellationToken ct = default);

    /// <summary>
    /// Pending -> Running. Sets started_at. Returns true if the job existed and was Pending.
    /// </summary>
    Task<bool> MarkRunningAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Running -> Completed. Updates mistake_item.root_cause + writes ProcessLog
    /// (Action=Reanalyzed, details contains job_id).
    /// </summary>
    Task<bool> MarkCompletedAsync(
        Guid jobId,
        string newRootCause,
        long vlDurationMs,
        CancellationToken ct = default);

    /// <summary>
    /// Running -> Failed. Writes ProcessLog (Action=Reanalyzed, details contains
    /// job_id + "FAILED: {reason}"). Does NOT change mistake_item.root_cause.
    /// </summary>
    Task<bool> MarkFailedAsync(
        Guid jobId,
        string errorMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Crash recovery: any Running job is marked Failed with
    /// "服务重启导致任务中断". Returns count of recovered jobs.
    /// </summary>
    Task<int> RecoverStaleRunningJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Delete Completed/Failed jobs whose completed_at is older than <paramref name="cutoff"/>.
    /// Returns count of deleted jobs.
    /// </summary>
    Task<int> DeleteExpiredJobsAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
