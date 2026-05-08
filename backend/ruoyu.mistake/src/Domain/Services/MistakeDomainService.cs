using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class MistakeValidationException : Exception
{
    public MistakeValidationException(string message) : base(message) { }
}

public class MistakeDomainService
{
    private readonly IMistakeRepository _mistakeRepository;
    private readonly IMistakeDetailRepository _mistakeDetailRepository;
    private readonly IMistakePracticeRepository _mistakePracticeRepository;
    private readonly IProcessLogRepository _processLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOssService _ossService;
    private readonly ILogger<MistakeDomainService> _logger;

    public MistakeDomainService(
        IMistakeRepository mistakeRepository,
        IMistakeDetailRepository mistakeDetailRepository,
        IMistakePracticeRepository mistakePracticeRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        IOssService ossService,
        ILogger<MistakeDomainService> logger)
    {
        _mistakeRepository = mistakeRepository;
        _mistakeDetailRepository = mistakeDetailRepository;
        _mistakePracticeRepository = mistakePracticeRepository;
        _processLogRepository = processLogRepository;
        _unitOfWork = unitOfWork;
        _ossService = ossService;
        _logger = logger;
    }

    public async Task<bool> DeleteMistakeImageAsync(Guid mistakeId, string imagePath)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(mistakeId);
        if (mistake == null) return false;

        if (mistake.Status != (int)MistakeStatus.Pending) return false;

        var removed = await _mistakeDetailRepository.RemoveImageAsync(mistakeId, imagePath);
        if (!removed) return false;

        await _unitOfWork.SaveChangesAsync();

        await SafeDeleteOssFileAsync(imagePath, "DeleteMistakeImage");

        return true;
    }

    public async Task<bool> DeleteMistakeAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        if (mistake.Status != (int)MistakeStatus.Pending) return false;

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);
        var practices = await _mistakePracticeRepository.GetByMistakeIdAsync(id);

        var allImagesToDelete = new List<string>();

        if (detail != null && detail.OriginalImages.Count > 0)
        {
            allImagesToDelete.AddRange(detail.OriginalImages);
        }

        foreach (var practice in practices)
        {
            if (practice.ImagePaths != null && practice.ImagePaths.Count > 0)
            {
                allImagesToDelete.AddRange(practice.ImagePaths);
            }
        }

        await _mistakePracticeRepository.RemoveByMistakeIdAsync(id);
        await _processLogRepository.RemoveByMistakeIdAsync(id);

        if (detail != null)
        {
            await _mistakeDetailRepository.DeleteByMistakeIdAsync(id);
        }

        _mistakeRepository.Remove(mistake);
        await _unitOfWork.SaveChangesAsync();

        if (allImagesToDelete.Count > 0)
        {
            await SafeDeleteOssFilesAsync(allImagesToDelete, "DeleteMistake");
        }

        return true;
    }

    public async Task<(bool success, string? studentId, List<string>? imagePaths, string? comments)> GetMistakeForReturnAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return (false, null, null, null);

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);

        var imagePaths = detail?.OriginalImages ?? new List<string>();

        return (true, mistake.StudentId, imagePaths, detail?.Comments);
    }

    public async Task<bool> DeleteMistakeWithoutDeletingImagesAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);
        var practices = await _mistakePracticeRepository.GetByMistakeIdAsync(id);

        await _mistakePracticeRepository.RemoveByMistakeIdAsync(id);
        await _processLogRepository.RemoveByMistakeIdAsync(id);

        if (detail != null)
        {
            await _mistakeDetailRepository.DeleteByMistakeIdAsync(id);
        }

        _mistakeRepository.Remove(mistake);
        await _unitOfWork.SaveChangesAsync();

        return true;
    }

    public async Task<(MistakeModel? mistake, MistakeDetailModel? detail)> GetMistakeAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return (null, null);

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);
        return (mistake, detail);
    }

    public async Task<bool> UpdateMistakeAsync(Guid id, Action<MistakeModel> updateMistakeAction, Action<MistakeDetailModel>? updateDetailAction = null)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);

        if (detail != null && updateDetailAction != null)
        {
            updateDetailAction(detail);
            await _mistakeDetailRepository.UpdateAsync(detail);
        }

        updateMistakeAction(mistake);

        ValidateMistakeData(mistake, detail?.OriginalImages, detail?.Comments, "更新后的错题");

        mistake.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkMistakeProcessedAsync(Guid id, string? questionId = null)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        mistake.Status = (int)MistakeStatus.Processed;
        mistake.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(questionId))
        {
            mistake.QuestionId = questionId;
        }

        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeId = mistake.Id,
            Action = (int)ProcessAction.Analyzed,
            Details = "Mistake marked as processed",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _processLogRepository.AddAsync(processLog);

        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<(List<MistakeModel> Items, int TotalCount)> GetMistakeListAsync(
        string studentId, int subject, int grade, int status, int page, int size)
    {
        if (page <= 0) page = 1;
        if (size <= 0) size = 10;
        if (size > 100) size = 100;

        return await _mistakeRepository.GetListAsync(studentId, subject, grade, status, page, size);
    }

    public async Task<MistakePracticeModel> AddPracticeAsync(MistakePracticeModel practice, List<string>? imagePaths = null)
    {
        practice.Id = Guid.NewGuid();
        practice.PracticeDate = DateTimeOffset.UtcNow;

        if (imagePaths != null && imagePaths.Count > 0)
        {
            practice.ImagePaths = imagePaths;
        }

        await _mistakePracticeRepository.AddAsync(practice);

        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeId = practice.MistakeId,
            Action = (int)ProcessAction.Practiced,
            Details = "Student practiced mistake",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _processLogRepository.AddAsync(processLog);

        await _unitOfWork.SaveChangesAsync();
        return practice;
    }

    public async Task<List<MistakePracticeModel>> GetPracticesAsync(Guid mistakeId)
    {
        return await _mistakePracticeRepository.GetByMistakeIdAsync(mistakeId);
    }

    public async Task<MistakeTypeResponse> AnalyzeMistakeTypeAsync(Guid mistakeId)
    {
        var type = 5;
        var typeName = "其他错误";
        var analysisReason = "根据答案对比无法明确分类";

        var mistake = await _mistakeRepository.GetByIdAsync(mistakeId);
        if (mistake != null)
        {
            var processLog = new ProcessLogModel
            {
                Id = Guid.NewGuid(),
                MistakeId = mistake.Id,
                Action = (int)ProcessAction.Analyzed,
                Details = $"Mistake analyzed as {typeName}",
                CreatedAt = DateTimeOffset.UtcNow
            };
            await _processLogRepository.AddAsync(processLog);
            await _unitOfWork.SaveChangesAsync();
        }

        return new MistakeTypeResponse
        {
            Type = type,
            TypeName = typeName,
            AnalysisReason = analysisReason
        };
    }

    public async Task<bool> LogMistakeProcessAsync(Guid mistakeId, string studentId, ProcessAction action, string details)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(mistakeId);
        if (mistake == null) return false;

        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeId = mistakeId,
            Action = (int)action,
            Details = details,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _processLogRepository.AddAsync(processLog);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeId)
    {
        return await _processLogRepository.GetByMistakeIdAsync(mistakeId);
    }

    private async Task SafeDeleteOssFileAsync(string objectPath, string operation)
    {
        try
        {
            await _ossService.DeleteAsync(objectPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete OSS file '{ObjectPath}' during '{Operation}'. Manual cleanup may be required.", objectPath, operation);
        }
    }

    private async Task SafeDeleteOssFilesAsync(IEnumerable<string> objectPaths, string operation)
    {
        foreach (var path in objectPaths)
        {
            await SafeDeleteOssFileAsync(path, operation);
        }
    }

    public async Task<(MistakeModel mistake, MistakeDetailModel detail)> CreateMistakeAsync(
        MistakeModel mistake, List<string>? imagePaths, string? comments)
    {
        ValidateMistakeData(mistake, imagePaths, comments, "错题");

        mistake.Id = Guid.NewGuid();
        mistake.CreatedAt = DateTimeOffset.UtcNow;
        mistake.Status = (int)MistakeStatus.Pending;

        await _mistakeRepository.AddAsync(mistake);

        var detail = new MistakeDetailModel
        {
            Id = Guid.NewGuid(),
            MistakeId = mistake.Id,
            Comments = comments,
            OriginalImages = imagePaths ?? new List<string>()
        };
        await _mistakeDetailRepository.AddAsync(detail);

        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeId = mistake.Id,
            Action = (int)ProcessAction.Created,
            Details = "Mistake created",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _processLogRepository.AddAsync(processLog);

        await _unitOfWork.SaveChangesAsync();

        return (mistake, detail);
    }

    private static void ValidateMistakeData(MistakeModel mistake, List<string>? imagePaths, string? comments, string prefix)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(mistake.StudentId))
            errors.Add($"{prefix}缺少学生ID");

        if (mistake.Subject <= 0)
            errors.Add($"{prefix}必须指定学科");
        else if (mistake.Subject > 9)
            errors.Add($"{prefix}学科值无效（有效范围：1-9）");

        if (mistake.Grade <= 0)
            errors.Add($"{prefix}必须指定年级");
        else if (mistake.Grade > 12)
            errors.Add($"{prefix}年级值无效（有效范围：1-12）");

        var hasImages = imagePaths != null && imagePaths.Count > 0;
        var hasComments = !string.IsNullOrWhiteSpace(comments);

        if (!hasImages && !hasComments)
            errors.Add($"{prefix}错题图片和错题正文不能同时为空，至少需要提供一项");

        if (hasImages && imagePaths!.Count > 9)
            errors.Add($"{prefix}错题图片不能超过9张");

        if (hasComments && comments!.Length > 2000)
            errors.Add($"{prefix}错题正文不能超过2000字");

        if (errors.Count > 0)
            throw new MistakeValidationException(string.Join("; ", errors));
    }
}
