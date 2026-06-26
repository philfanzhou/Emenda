using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Contract.Protos;
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

    public ReanalyzeJobService(
        IReanalyzeJobRepository jobRepository,
        IMistakeItemRepository mistakeItemRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<ReanalyzeJobService> logger)
    {
        _jobRepository = jobRepository;
        _mistakeItemRepository = mistakeItemRepository;
        _processLogRepository = processLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
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

    public async Task<List<ReanalyzeJobModel>> ClaimPendingJobsAsync(
        int maxCount,
        CancellationToken ct = default)
    {
        if (maxCount <= 0) return new List<ReanalyzeJobModel>();
        return await _jobRepository.ClaimPendingAsync(maxCount, ct);
    }

    public async Task<bool> MarkRunningAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _jobRepository.GetByIdAsync(jobId, ct);
        if (job == null) return false;
        if (job.Status != ReanalyzeJobState.Pending) return false;

        job.Status = ReanalyzeJobState.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await _jobRepository.UpdateStatusAsync(job, ct);
        // MarkRunningAsync is a single-entity write; commit immediately so that
        // subsequent claim cycles see this row as Running (not Pending) and skip it.
        await _unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> MarkCompletedAsync(
        Guid jobId,
        string newRootCause,
        long vlDurationMs,
        CancellationToken ct = default)
    {
        var job = await _jobRepository.GetByIdAsync(jobId, ct);
        if (job == null) return false;
        if (job.Status != ReanalyzeJobState.Running) return false;

        var item = await _mistakeItemRepository.GetByIdAsync(job.ItemId, ct);
        if (item == null)
        {
            // Item was deleted while job was in flight. Mark failed and bail.
            job.Status = ReanalyzeJobState.Failed;
            job.ErrorMessage = "错题已删除";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobRepository.UpdateStatusAsync(job, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return false;
        }

        var previousRootCause = job.PreviousRootCause ?? item.RootCause ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        // Atomic: item.RootCause + job.Status: Completed + ProcessLog in one SaveChanges.
        // SetRootCauseDeferredAsync does NOT save; caller (us) commits via UoW.
        await _mistakeItemRepository.SetRootCauseDeferredAsync(item.Id, newRootCause, now, ct);

        job.NewRootCause = newRootCause;
        job.Status = ReanalyzeJobState.Completed;
        job.CompletedAt = now;
        job.VlDurationMs = vlDurationMs;
        await _jobRepository.UpdateStatusAsync(job, ct);

        await AddProcessLogAsync(item.Id, job, previousRootCause, newRootCause, studentId: item.StudentId, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "ReanalyzeJob completed: JobId={JobId}, ItemId={ItemId}, VlDurationMs={VlDurationMs}, NewLength={NewLength}",
            job.Id, job.ItemId, vlDurationMs, newRootCause.Length);

        return true;
    }

    public async Task<bool> MarkFailedAsync(
        Guid jobId,
        string errorMessage,
        CancellationToken ct = default)
    {
        var job = await _jobRepository.GetByIdAsync(jobId, ct);
        if (job == null) return false;
        if (job.Status != ReanalyzeJobState.Running && job.Status != ReanalyzeJobState.Pending)
        {
            return false;
        }

        var item = await _mistakeItemRepository.GetByIdAsync(job.ItemId, ct);
        if (item == null)
        {
            job.Status = ReanalyzeJobState.Failed;
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobRepository.UpdateStatusAsync(job, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return false;
        }

        var previousRootCause = job.PreviousRootCause ?? item.RootCause ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        // VL failure: do NOT change item.RootCause. Only commit job status + ProcessLog.
        job.Status = ReanalyzeJobState.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = now;
        await _jobRepository.UpdateStatusAsync(job, ct);

        await AddProcessLogAsync(
            item.Id, job, previousRootCause, $"FAILED: {errorMessage}", studentId: item.StudentId, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogWarning(
            "ReanalyzeJob failed: JobId={JobId}, ItemId={ItemId}, Reason={Reason}",
            job.Id, job.ItemId, errorMessage);

        return true;
    }

    public async Task<int> RecoverStaleRunningJobsAsync(CancellationToken ct = default)
    {
        var staleJobs = await _jobRepository.GetStaleRunningAsync(ct);
        if (staleJobs.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var recovered = 0;
        foreach (var job in staleJobs)
        {
            job.Status = ReanalyzeJobState.Failed;
            job.ErrorMessage = "服务重启导致任务中断";
            job.CompletedAt = now;
            await _jobRepository.UpdateStatusAsync(job, ct);

            var item = await _mistakeItemRepository.GetByIdAsync(job.ItemId, ct);
            if (item != null)
            {
                var previousRootCause = job.PreviousRootCause ?? item.RootCause ?? string.Empty;
                await AddProcessLogAsync(
                    item.Id, job, previousRootCause, "FAILED: 服务重启导致任务中断",
                    studentId: item.StudentId, ct);
            }
            recovered++;
        }

        // Single commit covers all N jobs + N logs.
        await _unitOfWork.SaveChangesAsync(ct);
        if (recovered > 0)
        {
            _logger.LogInformation("ReanalyzeJob: recovered {Count} stale running jobs", recovered);
        }
        return recovered;
    }

    public async Task<int> DeleteExpiredJobsAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var deleted = await _jobRepository.DeleteExpiredAsync(cutoff, ct);
        if (deleted > 0)
        {
            _logger.LogInformation("ReanalyzeJob: deleted {Count} expired jobs (cutoff={Cutoff:o})", deleted, cutoff);
        }
        return deleted;
    }

    private async Task AddProcessLogAsync(
        Guid mistakeItemId,
        ReanalyzeJobModel job,
        string previousRootCause,
        string newRootCauseOrFailure,
        string? studentId,
        CancellationToken ct)
    {
        var noteProvided = !string.IsNullOrWhiteSpace(job.TeacherDescription);
        var noteLength = noteProvided ? job.TeacherDescription!.Length : 0;

        var log = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeItemId = mistakeItemId,
            StudentId = studentId,
            Action = (int)ProcessAction.Reanalyzed,
            Details = JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                previous_root_cause = previousRootCause,
                teacher_description_provided = noteProvided,
                teacher_description_length = noteLength,
                new_root_cause = newRootCauseOrFailure
            }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _processLogRepository.AddAsync(log, ct);
    }
}
