using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Components.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class MistakeDomainService
{
    private readonly IMistakeRepository _mistakeRepository;
    private readonly IMistakePracticeRepository _mistakePracticeRepository;
    private readonly IProcessLogRepository _processLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOssService _ossService;

    public MistakeDomainService(
        IMistakeRepository mistakeRepository,
        IMistakePracticeRepository mistakePracticeRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        IOssService ossService)
    {
        _mistakeRepository = mistakeRepository;
        _mistakePracticeRepository = mistakePracticeRepository;
        _processLogRepository = processLogRepository;
        _unitOfWork = unitOfWork;
        _ossService = ossService;
    }

    public async Task<MistakeModel> UploadMistakeAsync(MistakeModel mistake, List<byte[]>? images)
    {
        mistake.Id = Guid.NewGuid();
        mistake.CreatedAt = DateTime.UtcNow;
        mistake.Status = (int)MistakeStatus.Pending;

        // Upload images to MinIO
        if (images != null && images.Count > 0)
        {
            var imagePaths = new List<string>();
            for (int i = 0; i < images.Count; i++)
            {
                var fileName = $"m_{mistake.Id}_{i}.jpg";
                var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                imagePaths.Add(path);
            }
            mistake.ImagePaths = imagePaths;
        }

        await _mistakeRepository.AddAsync(mistake);

        // Record process log
        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeId = mistake.Id,
            Action = (int)ProcessAction.Created,
            Details = "Student uploaded mistake",
            CreatedAt = DateTime.UtcNow
        };
        await _processLogRepository.AddAsync(processLog);

        await _unitOfWork.SaveChangesAsync();
        return mistake;
    }

    public async Task<bool> DeleteMistakeAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        // Can only delete PENDING status mistakes
        if (mistake.Status != (int)MistakeStatus.Pending) return false;

        // Delete images from MinIO
        if (mistake.ImagePaths.Count > 0)
        {
            var imagePaths = mistake.ImagePaths;
            if (imagePaths.Count > 0)
            {
                await _ossService.DeleteManyAsync(imagePaths);
            }
        }

        _mistakeRepository.Remove(mistake);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SplitMistakeAsync(Guid originalMistakeId, List<(MistakeModel mistake, List<byte[]>? images)> newMistakes)
    {
        var originalMistake = await _mistakeRepository.GetByIdAsync(originalMistakeId);
        if (originalMistake == null) return false;

        // Delete original images from MinIO
        if (originalMistake.ImagePaths.Count > 0)
        {
            await _ossService.DeleteManyAsync(originalMistake.ImagePaths);
        }

        // Physically delete the original mistake regardless of its status.
        // Cascade delete will handle related practices and process logs.
        _mistakeRepository.Remove(originalMistake);

        // Add new mistakes
        foreach (var item in newMistakes)
        {
            var newMistake = item.mistake;
            var images = item.images;

            newMistake.Id = Guid.NewGuid();
            newMistake.CreatedAt = DateTime.UtcNow;
            newMistake.Status = (int)MistakeStatus.Pending;

            if (images != null && images.Count > 0)
            {
                var imagePaths = new List<string>();
                for (int i = 0; i < images.Count; i++)
                {
                    var fileName = $"m_{newMistake.Id}_{i}.jpg";
                    var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                    imagePaths.Add(path);
                }
                newMistake.ImagePaths = imagePaths;
            }

            await _mistakeRepository.AddAsync(newMistake);

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

        // Commit all changes in one transaction
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<MistakeModel?> GetMistakeAsync(Guid id)
    {
        return await _mistakeRepository.GetByIdAsync(id);
    }

    public async Task<bool> UpdateMistakeAsync(Guid id, Action<MistakeModel> updateAction, List<byte[]>? images = null)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        // Delete old images if new images are provided
        if (images != null && images.Count > 0)
        {
            if (mistake.ImagePaths.Count > 0)
            {
                var oldPaths = mistake.ImagePaths;
                if (oldPaths.Count > 0)
                {
                    await _ossService.DeleteManyAsync(oldPaths);
                }
            }

            // Upload new images to MinIO
            var imagePaths = new List<string>();
            for (int i = 0; i < images.Count; i++)
            {
                var fileName = $"m_{mistake.Id}_{i}.jpg";
                var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                imagePaths.Add(path);
            }
            mistake.ImagePaths = imagePaths;
        }

        updateAction(mistake);
        mistake.UpdatedAt = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkMistakeProcessedAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        mistake.Status = (int)MistakeStatus.Processed;
        mistake.UpdatedAt = DateTime.UtcNow;

        // Record process log
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
        // Pagination validation
        if (page <= 0) page = 1;
        if (size <= 0) size = 10;
        if (size > 100) size = 100;

        return await _mistakeRepository.GetListAsync(studentId, subject, grade, status, page, size);
    }

    public async Task<MistakePracticeModel> AddPracticeAsync(MistakePracticeModel practice, List<byte[]>? images = null)
    {
        practice.Id = Guid.NewGuid();
        practice.PracticeDate = DateTime.UtcNow;

        // Upload images to MinIO
        if (images != null && images.Count > 0)
        {
            var imagePaths = new List<string>();
            for (int i = 0; i < images.Count; i++)
            {
                var fileName = $"p_{practice.Id}_{i}.jpg";
                var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                imagePaths.Add(path);
            }
            practice.ImagePaths = imagePaths;
        }

        await _mistakePracticeRepository.AddAsync(practice);

        // Record process log
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

    public async Task<MistakeTypeResponse> AnalyzeMistakeTypeAsync(Guid mistakeId, string content, string correctAnswer, string studentAnswer)
    {
        // Simple analysis logic - in production this would call an AI service
        var type = 5; // 5 = 其他错误 (Other)
        var typeName = "其他错误";
        var analysisReason = "根据答案对比无法明确分类";

        // Record process log
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
}

