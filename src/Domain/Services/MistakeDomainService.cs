using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;

namespace Emenda.Domain.Services;

public class MistakeValidationException : Exception
{
    public MistakeValidationException(string message) : base(message) { }
}

/// <summary>A write that would change the identity of a source-backed item (HTTP 409 with <see cref="Code"/>).</summary>
public class MistakeConflictException : Exception
{
    public MistakeConflictException(string code) : base(code) => Code = code;
    public string Code { get; }
}

public class MistakeDomainService : IMistakeDomainService
{
    private readonly IMistakeItemRepository _mistakeItemRepository;
    private readonly IMistakePracticeRepository _mistakePracticeRepository;
    private readonly IProcessLogRepository _processLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MistakeDomainService> _logger;

    public MistakeDomainService(
        IMistakeItemRepository mistakeItemRepository,
        IMistakePracticeRepository mistakePracticeRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<MistakeDomainService> logger)
    {
        _mistakeItemRepository = mistakeItemRepository;
        _mistakePracticeRepository = mistakePracticeRepository;
        _processLogRepository = processLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<MistakeItemModel?> GetMistakeItemAsync(Guid id)
    {
        return await _mistakeItemRepository.GetByIdAsync(id);
    }

    public Task<bool> IsConversationObjectReferencedAsync(string objectPath, string caller, CancellationToken ct = default) =>
        _mistakeItemRepository.IsConversationObjectReferencedAsync(objectPath, caller, ct);

    public async Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId)
    {
        return await _mistakeItemRepository.GetBySourceUploadIdAsync(sourceUploadId);
    }

    public async Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(
        string studentId, int subject, int grade, int reviewStatus, int page, int size)
    {
        if (page <= 0) page = 1;
        if (size <= 0) size = 10;
        if (size > 100) size = 100;

        return await _mistakeItemRepository.GetListAsync(studentId, subject, grade, reviewStatus, page, size);
    }

    public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null, CancellationToken ct = default)
    {
        if (page <= 0) page = 1;
        if (size <= 0) size = 10;
        if (size > 100) size = 100;

        return await _mistakeItemRepository.GetPendingReviewUploadsAsync(page, size, subjects, ct);
    }

    public async Task<MistakeItemModel?> ReviewMistakeItemAsync(Guid id, int reviewStatus, string? reviewerId, string? rootCause, int? grade = null, int? subject = null, string? reviewReason = null)
    {
        var item = await _mistakeItemRepository.GetByIdAsync(id);
        if (item == null) return null;

        if (item.ReviewStatus != (int)ReviewStatus.PendingReview)
        {
            throw new MistakeValidationException("只能审核待审核状态的错题");
        }

        if (reviewStatus != (int)ReviewStatus.Confirmed && reviewStatus != (int)ReviewStatus.Rejected)
        {
            throw new MistakeValidationException("审核结果只能是确认或退回");
        }

        if (reviewReason?.Length > 2000) throw new MistakeValidationException("审核说明最多2000个字符");

        item.ReviewStatus = reviewStatus;
        item.ReviewerId = reviewerId;
        item.ReviewedAt = DateTimeOffset.UtcNow;
        item.RootCause = rootCause;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        if (grade.HasValue && grade.Value > 0 && grade.Value <= 12)
        {
            item.Grade = grade.Value;
        }

        if (subject.HasValue && subject.Value > 0 && subject.Value <= 9)
        {
            item.Subject = subject.Value;
        }

        await _mistakeItemRepository.UpdateAsync(item);

        var action = reviewStatus == (int)ReviewStatus.Confirmed
            ? ProcessAction.ReviewConfirmed
            : ProcessAction.ReviewRejected;
        var detail = reviewStatus == (int)ReviewStatus.Confirmed
            ? "Mistake item confirmed"
            : string.IsNullOrWhiteSpace(reviewReason) ? "Mistake item rejected" : "Mistake item rejected: " + reviewReason;

        await AddProcessLogAsync(item.Id, action, detail, item.StudentId);
        await _unitOfWork.SaveChangesAsync();

        return item;
    }

    public async Task<bool> DeleteMistakeItemAsync(Guid id)
    {
        var item = await _mistakeItemRepository.GetByIdAsync(id);
        if (item == null) return false;

        await _mistakePracticeRepository.RemoveByMistakeItemIdAsync(id);
        await _processLogRepository.RemoveByMistakeItemIdAsync(id);
        await _mistakeItemRepository.DeleteAsync(id);
        await _unitOfWork.SaveChangesAsync();

        return true;
    }

    public async Task<MistakeItemModel?> UpdateMistakeItemPartialAsync(Guid id, string? studentId = null, int? subject = null, int? grade = null, List<SourceRegionModel>? sourceRegions = null)
    {
        var item = await _mistakeItemRepository.GetByIdAsync(id);
        if (item == null) return null;

        if (!string.IsNullOrWhiteSpace(studentId))
            item.StudentId = studentId;
        if (subject.HasValue && subject.Value > 0)
            item.Subject = subject.Value;
        if (grade.HasValue && grade.Value > 0)
            item.Grade = grade.Value;
        if (sourceRegions != null && sourceRegions.Count > 0)
            item.SourceRegions = sourceRegions;

        item.UpdatedAt = DateTimeOffset.UtcNow;

        var result = await _mistakeItemRepository.UpdateAsync(item);
        if (!result) return null;
        await _unitOfWork.SaveChangesAsync();
        // Re-read so the response carries the source shape the store derived from the saved regions.
        return await _mistakeItemRepository.GetByIdAsync(id) ?? item;
    }

    public async Task<MistakePracticeModel> AddPracticeAsync(MistakePracticeModel practice, List<string>? imagePaths = null)
    {
        var item = await _mistakeItemRepository.GetByIdAsync(practice.MistakeItemId);
        if (item == null)
        {
            throw new MistakeValidationException("错题条目不存在");
        }

        if (item.ReviewStatus != (int)ReviewStatus.Confirmed)
        {
            throw new MistakeValidationException("只能对已确认的错题进行练习");
        }

        practice.Id = Guid.NewGuid();
        practice.PracticeDate = DateTimeOffset.UtcNow;

        if (imagePaths != null && imagePaths.Count > 0)
        {
            practice.ImagePaths = imagePaths;
        }

        await _mistakePracticeRepository.AddAsync(practice);
        await AddProcessLogAsync(item.Id, ProcessAction.Practiced, "Student practiced mistake", item.StudentId);
        await _unitOfWork.SaveChangesAsync();

        return practice;
    }

    public async Task<List<MistakePracticeModel>> GetPracticesAsync(Guid mistakeItemId)
    {
        return await _mistakePracticeRepository.GetByMistakeItemIdAsync(mistakeItemId);
    }

    public async Task<bool> LogMistakeProcessAsync(Guid mistakeItemId, string studentId, ProcessAction action, string details)
    {
        var item = await _mistakeItemRepository.GetByIdAsync(mistakeItemId);
        if (item == null) return false;

        await AddProcessLogAsync(mistakeItemId, action, details, studentId);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeItemId)
    {
        return await _processLogRepository.GetByMistakeItemIdAsync(mistakeItemId);
    }

    private async Task AddProcessLogAsync(Guid mistakeItemId, ProcessAction action, string details, string? studentId = null)
    {
        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeItemId = mistakeItemId,
            StudentId = studentId,
            Action = (int)action,
            Details = details,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _processLogRepository.AddAsync(processLog);
    }


}
