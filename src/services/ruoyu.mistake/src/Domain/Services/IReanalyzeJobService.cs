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
    /// Atomically claim up to <paramref name="maxCount"/> jobs under the database-wide limit.
    /// </summary>
    Task<List<ReanalyzeJobModel>> ClaimJobsAsync(
        int maxCount,
        CancellationToken ct = default);
    Task<bool> RenewLeaseAsync(Guid jobId, Guid ownerId, long version, CancellationToken ct = default);

    /// <summary>
    /// Complete only the matching live lease; Job, Item and ProcessLog commit together.
    /// </summary>
    Task<bool> MarkCompletedAsync(
        Guid jobId,
        Guid ownerId,
        long version,
        string newRootCause,
        long vlDurationMs,
        CancellationToken ct = default);

    /// <summary>
    /// Fail only the matching live lease with a bounded reason code.
    /// </summary>
    Task<bool> MarkFailedAsync(
        Guid jobId,
        Guid ownerId,
        long version,
        string reasonCode,
        CancellationToken ct = default);

    /// <summary>
    /// Delete Completed/Failed jobs whose completed_at is older than <paramref name="cutoff"/>.
    /// Returns count of deleted jobs.
    /// </summary>
    Task<int> DeleteExpiredJobsAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
