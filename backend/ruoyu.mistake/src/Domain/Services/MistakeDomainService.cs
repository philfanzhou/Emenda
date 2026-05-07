using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Ruoyu.Study.Mistake.Domain.Services;

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

    public async Task<bool> SplitMistakeAsync(Guid originalMistakeId, List<(MistakeModel mistake, List<string>? imagePaths, string? comments)> newMistakes)
    {
        var originalMistake = await _mistakeRepository.GetByIdAsync(originalMistakeId);
        if (originalMistake == null) return false;

        var originalDetail = await _mistakeDetailRepository.GetByMistakeIdAsync(originalMistakeId);
        List<string>? originalImagesToDelete = originalDetail != null && originalDetail.OriginalImages.Count > 0
            ? new List<string>(originalDetail.OriginalImages)
            : null;

        var practices = await _mistakePracticeRepository.GetByMistakeIdAsync(originalMistakeId);
        var practiceImagesToDelete = new List<string>();
        foreach (var practice in practices)
        {
            if (practice.ImagePaths != null && practice.ImagePaths.Count > 0)
            {
                practiceImagesToDelete.AddRange(practice.ImagePaths);
            }
        }

        await _mistakePracticeRepository.RemoveByMistakeIdAsync(originalMistakeId);
        await _processLogRepository.RemoveByMistakeIdAsync(originalMistakeId);

        if (originalDetail != null)
        {
            await _mistakeDetailRepository.DeleteByMistakeIdAsync(originalMistakeId);
        }

        _mistakeRepository.Remove(originalMistake);

        foreach (var item in newMistakes)
        {
            var newMistake = item.mistake;
            var imagePaths = item.imagePaths;
            var comments = item.comments;

            newMistake.Id = Guid.NewGuid();
            newMistake.CreatedAt = DateTime.UtcNow;
            newMistake.Status = (int)MistakeStatus.Pending;

            await _mistakeRepository.AddAsync(newMistake);

            var newDetail = new MistakeDetailModel
            {
                Id = Guid.NewGuid(),
                MistakeId = newMistake.Id,
                Comments = comments,
                OriginalImages = imagePaths ?? new List<string>()
            };

            await _mistakeDetailRepository.AddAsync(newDetail);

            var processLog = new ProcessLogModel
            {
                Id = Guid.NewGuid(),
                MistakeId = newMistake.Id,
                Action = (int)ProcessAction.Created,
                Details = $"Split from original mistake {originalMistakeId}",
                CreatedAt = DateTime.UtcNow
            };
            await _processLogRepository.AddAsync(processLog);
        }

        await _unitOfWork.SaveChangesAsync();

        if (originalImagesToDelete != null && originalImagesToDelete.Count > 0)
        {
            await SafeDeleteOssFilesAsync(originalImagesToDelete, "SplitMistake original images");
        }
        if (practiceImagesToDelete.Count > 0)
        {
            await SafeDeleteOssFilesAsync(practiceImagesToDelete, "SplitMistake practice images");
        }

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
        mistake.UpdatedAt = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkMistakeProcessedAsync(Guid id, string? questionId = null)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        mistake.Status = (int)MistakeStatus.Processed;
        mistake.UpdatedAt = DateTime.UtcNow;
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
            CreatedAt = DateTime.UtcNow
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
        practice.PracticeDate = DateTime.UtcNow;

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
            CreatedAt = DateTime.UtcNow
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
                CreatedAt = DateTime.UtcNow
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
            CreatedAt = DateTime.UtcNow
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
}
