using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Errors;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class ReanalyzeJobService : IReanalyzeJobService
{
    private readonly IReanalyzeJobRepository _jobRepository;
    private readonly IMistakeItemRepository _mistakeItemRepository;
    private readonly IProcessLogRepository _processLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ReanalyzeJobService> _logger;
    private readonly IMistakeSourceGuard? _sourceGuard;

    public ReanalyzeJobService(
        IReanalyzeJobRepository jobRepository,
        IMistakeItemRepository mistakeItemRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<ReanalyzeJobService> logger,
        IMistakeSourceGuard? sourceGuard = null)
    {
        _jobRepository = jobRepository;
        _mistakeItemRepository = mistakeItemRepository;
        _processLogRepository = processLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _sourceGuard = sourceGuard;
    }

    public async Task<StartReanalyzeJobResult> StartJobAsync(
        Guid itemId,
        string reviewerId,
        string studentId,
        string? teacherDescription,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            return new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.InvalidArgument,
                ErrorMessage = "错题 ID 不能为空"
            };
        }
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.InvalidArgument,
                ErrorMessage = "未识别教师身份"
            };
        }
        if (string.IsNullOrWhiteSpace(studentId))
        {
            return new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.InvalidArgument,
                ErrorMessage = "缺少学生ID"
            };
        }

        var item = await _mistakeItemRepository.GetByIdAsync(itemId, ct);
        if (item == null)
        {
            return new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.NotFound,
                ErrorMessage = "错题不存在"
            };
        }

        if (_sourceGuard != null) await _sourceGuard.EnsureOwnedWriteAsync(itemId, studentId, ct);

        if (item.ReviewStatus != (int)ReviewStatus.PendingReview &&
            item.ReviewStatus != (int)ReviewStatus.Confirmed)
        {
            return new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.FailedPrecondition,
                ErrorMessage = "只能对 PendingReview 或 Confirmed 状态的错题重新分析"
            };
        }

        if (item.SourceRegions == null || item.SourceRegions.Count == 0)
        {
            return new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.FailedPrecondition,
                ErrorMessage = "错题无图片，无法重新分析"
            };
        }

        var now = DateTimeOffset.UtcNow;
        var job = new ReanalyzeJobModel
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            ReviewerId = reviewerId,
            StudentId = studentId,
            TeacherDescription = string.IsNullOrWhiteSpace(teacherDescription) ? null : teacherDescription,
            PreviousRootCause = item.RootCause,
            Status = ReanalyzeJobState.Pending,
            CreatedAt = now,
        };

        await _jobRepository.AddAsync(job, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "ReanalyzeJob enqueued: JobId={JobId}, ItemId={ItemId}, ReviewerId={ReviewerId}, HasTeacherNote={HasNote}",
            job.Id, job.ItemId, job.ReviewerId, !string.IsNullOrWhiteSpace(job.TeacherDescription));

        return new StartReanalyzeJobResult { Success = true, Job = job };
    }

    public async Task<List<ReanalyzeJobModel>> GetJobsAsync(
        string reviewerId,
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return new List<ReanalyzeJobModel>();

        var jobs = await _jobRepository.GetByIdsAsync(jobIds, ct);
        // CRITICAL: filter by reviewer_id BEFORE returning, even if jobIds include
        // jobs owned by other teachers. Cross-teacher isolation.
        return jobs.Where(j => j.ReviewerId == reviewerId).ToList();
    }

    public async Task<List<ReanalyzeJobModel>> GetActiveJobsAsync(
        string reviewerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reviewerId)) return new List<ReanalyzeJobModel>();
        return await _jobRepository.GetActiveByReviewerAsync(reviewerId, ct);
    }

    public Task<List<ReanalyzeJobModel>> ClaimJobsAsync(int maxCount, CancellationToken ct = default) =>
        _jobRepository.ClaimAsync(Math.Clamp(maxCount, 0, 2), ct);

    public Task<bool> RenewLeaseAsync(Guid jobId, Guid ownerId, long version, CancellationToken ct = default) =>
        _jobRepository.RenewAsync(jobId, ownerId, version, ct);

    public Task<bool> MarkCompletedAsync(Guid jobId, Guid ownerId, long version,
        string newRootCause, long vlDurationMs, CancellationToken ct = default) =>
        _jobRepository.CompleteAsync(jobId, ownerId, version, newRootCause, vlDurationMs, ct);

    public Task<bool> MarkFailedAsync(Guid jobId, Guid ownerId, long version,
        string reasonCode, CancellationToken ct = default) =>
        _jobRepository.FailAsync(jobId, ownerId, version, reasonCode, ct);

    public async Task<int> DeleteExpiredJobsAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var deleted = await _jobRepository.DeleteExpiredAsync(cutoff, ct);
        if (deleted > 0)
        {
            _logger.LogInformation("ReanalyzeJob: deleted {Count} expired jobs (cutoff={Cutoff:o})", deleted, cutoff);
        }
        return deleted;
    }

}
