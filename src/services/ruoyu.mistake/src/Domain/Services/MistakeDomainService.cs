using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class MistakeValidationException : Exception
{
    public MistakeValidationException(string message) : base(message) { }
}

public class CompleteUploadReviewResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SourceUploadId { get; set; }
    public string? StudentId { get; set; }
    public List<MistakeItemModel> Items { get; set; } = new();
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

    public async Task<List<MistakeItemModel>> SubmitMistakeUploadAsync(
        string studentId, int subject, int grade, List<string> imagePaths, string? rootCause, string sourceUploadId)
    {
        ValidateSubmitData(studentId, subject, grade, imagePaths, rootCause, sourceUploadId);

        var item = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            Subject = subject,
            Grade = grade,
            SourceUploadId = sourceUploadId,
            SourceRegions = imagePaths.ConvertAll(p => new SourceRegionModel
            {
                SourceImagePath = p,
                BoundingBox = null
            }),
            ReviewStatus = (int)ReviewStatus.PendingReview,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _logger.LogDebug("SubmitMistakeUploadAsync: Creating item with Id={ItemId}, studentId={StudentId}, subject={Subject}, grade={Grade}, sourceUploadId={SourceUploadId}, imagePaths={ImagePaths}",
            item.Id, studentId, subject, grade, sourceUploadId, string.Join(" | ", imagePaths));

        await _mistakeItemRepository.AddAsync(item);
        await AddProcessLogAsync(item.Id, ProcessAction.Created, "Mistake item created from upload", studentId);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogDebug("SubmitMistakeUploadAsync: Saved item with Id={ItemId}", item.Id);

        return new List<MistakeItemModel> { item };
    }

    public async Task<MistakeItemModel?> GetMistakeItemAsync(Guid id)
    {
        return await _mistakeItemRepository.GetByIdAsync(id);
    }

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

    public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null)
    {
        if (page <= 0) page = 1;
        if (size <= 0) size = 10;
        if (size > 100) size = 100;

        return await _mistakeItemRepository.GetPendingReviewUploadsAsync(page, size, subjects);
    }

    public async Task<MistakeItemModel?> ReviewMistakeItemAsync(Guid id, int reviewStatus, string? reviewerId, string? rootCause, int? grade = null, int? subject = null)
    {
        var item = await _mistakeItemRepository.GetByIdAsync(id);
        if (item == null) return null;

        if (item.ReviewStatus != (int)ReviewStatus.PendingReview)
        {
            throw new MistakeValidationException("只能审核待审核状态的错题");
        }

        if (reviewStatus != (int)ReviewStatus.Confirmed && reviewStatus != (int)ReviewStatus.Rejected)
        {
            throw new MistakeValidationException("审核结果只能是确认或驳回");
        }

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
            : "Mistake item rejected";

        await AddProcessLogAsync(item.Id, action, detail, item.StudentId);
        await _unitOfWork.SaveChangesAsync();

        return item;
    }

    public async Task<MistakeItemModel> AddMistakeItemAsync(MistakeItemModel item)
    {
        ValidateMistakeItemData(item);

        item.Id = Guid.NewGuid();
        item.ReviewStatus = (int)ReviewStatus.PendingReview;
        item.CreatedAt = DateTimeOffset.UtcNow;

        await _mistakeItemRepository.AddAsync(item);
        await AddProcessLogAsync(item.Id, ProcessAction.Created, "Mistake item manually added by teacher", item.StudentId);
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
        if (result)
            await _unitOfWork.SaveChangesAsync();

        return result ? item : null;
    }

    public async Task<bool> UpdateMistakeItemAsync(MistakeItemModel model)
    {
        var result = await _mistakeItemRepository.UpdateAsync(model);
        if (result)
            await _unitOfWork.SaveChangesAsync();
        return result;
    }

    [Obsolete("目标流程中不存在独立的 CompleteUploadReview 步骤。仅保留供 LegacyClean 运维兜底使用。")]
    public async Task<CompleteUploadReviewResult> CompleteUploadReviewAsync(
        string sourceUploadId, string? reviewerId)
    {
        var pendingCount = await _mistakeItemRepository.CountPendingBySourceUploadIdAsync(sourceUploadId);
        if (pendingCount > 0)
        {
            return new CompleteUploadReviewResult
            {
                Success = false,
                ErrorMessage = $"还有 {pendingCount} 条错题待审核，请先完成所有审核"
            };
        }

        var items = await _mistakeItemRepository.GetBySourceUploadIdAsync(sourceUploadId);
        if (items.Count == 0)
        {
            return new CompleteUploadReviewResult
            {
                Success = false,
                ErrorMessage = "未找到该上传记录的错题"
            };
        }

        return new CompleteUploadReviewResult
        {
            Success = true,
            SourceUploadId = sourceUploadId,
            StudentId = items[0].StudentId,
            Items = items
        };
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

    private static void ValidateSubmitData(
        string studentId, int subject, int grade, List<string> imagePaths, string? rootCause, string sourceUploadId)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(studentId))
            errors.Add("缺少学生ID");

        if (subject <= 0 || subject > 9)
            errors.Add("学科值无效（有效范围：1-9）");

        if (grade <= 0 || grade > 12)
            errors.Add("年级值无效（有效范围：1-12）");

        if (string.IsNullOrWhiteSpace(sourceUploadId))
            errors.Add("缺少上传记录ID");

        if (imagePaths == null || imagePaths.Count == 0)
            errors.Add("至少需要提供一张图片");

        if (errors.Count > 0)
            throw new MistakeValidationException(string.Join("; ", errors));
    }

    private static void ValidateMistakeItemData(MistakeItemModel item)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(item.StudentId))
            errors.Add("缺少学生ID");

        if (item.Subject <= 0 || item.Subject > 9)
            errors.Add("学科值无效（有效范围：1-9）");

        if (item.Grade <= 0 || item.Grade > 12)
            errors.Add("年级值无效（有效范围：1-12）");

        if (string.IsNullOrWhiteSpace(item.SourceUploadId))
            errors.Add("缺少上传记录ID");

        if (item.SourceRegions == null || item.SourceRegions.Count == 0)
            errors.Add("至少需要一个图片区域");

        if (errors.Count > 0)
            throw new MistakeValidationException(string.Join("; ", errors));
    }
}
