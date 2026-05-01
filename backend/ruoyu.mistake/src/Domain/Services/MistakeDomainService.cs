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
    private readonly IMistakeDetailRepository _mistakeDetailRepository;
    private readonly IMistakePracticeRepository _mistakePracticeRepository;
    private readonly IProcessLogRepository _processLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOssService _ossService;

    public MistakeDomainService(
        IMistakeRepository mistakeRepository,
        IMistakeDetailRepository mistakeDetailRepository,
        IMistakePracticeRepository mistakePracticeRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        IOssService ossService)
    {
        _mistakeRepository = mistakeRepository;
        _mistakeDetailRepository = mistakeDetailRepository;
        _mistakePracticeRepository = mistakePracticeRepository;
        _processLogRepository = processLogRepository;
        _unitOfWork = unitOfWork;
        _ossService = ossService;
    }

    public async Task<(MistakeModel mistake, MistakeDetailModel detail)> UploadMistakeAsync(MistakeModel mistake, List<byte[]>? images)
    {
        List<string>? uploadedPaths = null;

        try
        {
            mistake.Id = Guid.NewGuid();
            mistake.CreatedAt = DateTime.UtcNow;
            mistake.Status = (int)MistakeStatus.Pending;

            var detail = new MistakeDetailModel
            {
                Id = Guid.NewGuid(),
                MistakeId = mistake.Id
            };

            if (images != null && images.Count > 0)
            {
                var imagePaths = new List<string>();
                for (int i = 0; i < images.Count; i++)
                {
                    var fileName = $"m_{mistake.Id}_{i}.jpg";
                    var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                    imagePaths.Add(path);
                }
                uploadedPaths = imagePaths;
                detail.OriginalImages = imagePaths;
            }

            await _mistakeRepository.AddAsync(mistake);
            await _mistakeDetailRepository.AddAsync(detail);

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
            return (mistake, detail);
        }
        catch
        {
            if (uploadedPaths != null && uploadedPaths.Count > 0)
            {
                await _ossService.DeleteManyAsync(uploadedPaths);
            }
            throw;
        }
    }

    public async Task<bool> DeleteMistakeAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        if (mistake.Status != (int)MistakeStatus.Pending) return false;

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);
        List<string>? imagesToDelete = detail != null && detail.OriginalImages.Count > 0
            ? new List<string>(detail.OriginalImages)
            : null;

        if (imagesToDelete != null)
        {
            await _mistakeDetailRepository.DeleteByMistakeIdAsync(id);
        }

        _mistakeRepository.Remove(mistake);
        await _unitOfWork.SaveChangesAsync();

        if (imagesToDelete != null && imagesToDelete.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await _ossService.DeleteManyAsync(imagesToDelete); }
                catch { }
            });
        }

        return true;
    }

    public async Task<bool> SplitMistakeAsync(Guid originalMistakeId, List<(MistakeModel mistake, List<byte[]>? images, string? comments)> newMistakes)
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

        List<string>? newlyUploadedPaths = null;

        try
        {
            await _mistakePracticeRepository.RemoveByMistakeIdAsync(originalMistakeId);
            await _processLogRepository.RemoveByMistakeIdAsync(originalMistakeId);

            if (originalImagesToDelete != null)
            {
                await _mistakeDetailRepository.DeleteByMistakeIdAsync(originalMistakeId);
            }

            _mistakeRepository.Remove(originalMistake);

            foreach (var item in newMistakes)
            {
                var newMistake = item.mistake;
                var images = item.images;
                var comments = item.comments;

                newMistake.Id = Guid.NewGuid();
                newMistake.CreatedAt = DateTime.UtcNow;
                newMistake.Status = (int)MistakeStatus.Pending;

                await _mistakeRepository.AddAsync(newMistake);

                var newDetail = new MistakeDetailModel
                {
                    Id = Guid.NewGuid(),
                    MistakeId = newMistake.Id,
                    Comments = comments
                };

                if (images != null && images.Count > 0)
                {
                    var imagePaths = new List<string>();
                    for (int i = 0; i < images.Count; i++)
                    {
                        var fileName = $"m_{newMistake.Id}_{i}.jpg";
                        var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                        imagePaths.Add(path);
                    }
                    newDetail.OriginalImages = imagePaths;
                    newlyUploadedPaths ??= new List<string>();
                    newlyUploadedPaths.AddRange(imagePaths);
                }

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
                _ = Task.Run(async () =>
                {
                    try { await _ossService.DeleteManyAsync(originalImagesToDelete); }
                    catch { }
                });
            }
            if (practiceImagesToDelete.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await _ossService.DeleteManyAsync(practiceImagesToDelete); }
                    catch { }
                });
            }

            return true;
        }
        catch
        {
            if (newlyUploadedPaths != null && newlyUploadedPaths.Count > 0)
            {
                await _ossService.DeleteManyAsync(newlyUploadedPaths);
            }
            throw;
        }
    }

    public async Task<(MistakeModel? mistake, MistakeDetailModel? detail)> GetMistakeAsync(Guid id)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return (null, null);

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);
        return (mistake, detail);
    }

    public async Task<bool> UpdateMistakeAsync(Guid id, Action<MistakeModel> updateMistakeAction, Action<MistakeDetailModel>? updateDetailAction = null, List<byte[]>? images = null)
    {
        var mistake = await _mistakeRepository.GetByIdAsync(id);
        if (mistake == null) return false;

        var detail = await _mistakeDetailRepository.GetByMistakeIdAsync(id);

        if (images != null && images.Count > 0)
        {
            List<string>? oldImagesToDelete = null;
            List<string>? newlyUploadedPaths = null;

            try
            {
                var imagePaths = new List<string>();
                for (int i = 0; i < images.Count; i++)
                {
                    var fileName = $"m_{mistake.Id}_{i}.jpg";
                    var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                    imagePaths.Add(path);
                }
                newlyUploadedPaths = imagePaths;

                if (detail != null && detail.OriginalImages.Count > 0)
                {
                    oldImagesToDelete = new List<string>(detail.OriginalImages);
                }

                if (detail == null)
                {
                    detail = new MistakeDetailModel
                    {
                        Id = Guid.NewGuid(),
                        MistakeId = mistake.Id,
                        OriginalImages = imagePaths
                    };
                    await _mistakeDetailRepository.AddAsync(detail);
                }
                else
                {
                    detail.OriginalImages = imagePaths;
                    await _mistakeDetailRepository.UpdateAsync(detail);
                }

                updateMistakeAction(mistake);
                mistake.UpdatedAt = DateTime.UtcNow;
                await _unitOfWork.SaveChangesAsync();

                if (oldImagesToDelete != null && oldImagesToDelete.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await _ossService.DeleteManyAsync(oldImagesToDelete); }
                        catch { }
                    });
                }

                return true;
            }
            catch
            {
                if (newlyUploadedPaths != null && newlyUploadedPaths.Count > 0)
                {
                    await _ossService.DeleteManyAsync(newlyUploadedPaths);
                }
                throw;
            }
        }
        else if (detail != null && updateDetailAction != null)
        {
            updateDetailAction(detail);
            await _mistakeDetailRepository.UpdateAsync(detail);
            updateMistakeAction(mistake);
            mistake.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync();
            return true;
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

    public async Task<MistakePracticeModel> AddPracticeAsync(MistakePracticeModel practice, List<byte[]>? images = null)
    {
        List<string>? uploadedPaths = null;

        try
        {
            practice.Id = Guid.NewGuid();
            practice.PracticeDate = DateTime.UtcNow;

            if (images != null && images.Count > 0)
            {
                var imagePaths = new List<string>();
                for (int i = 0; i < images.Count; i++)
                {
                    var fileName = $"p_{practice.Id}_{i}.jpg";
                    var path = await _ossService.UploadAsync(images[i], fileName, "image/jpeg", OssBucket.Mistakes);
                    imagePaths.Add(path);
                }
                uploadedPaths = imagePaths;
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
        catch
        {
            if (uploadedPaths != null && uploadedPaths.Count > 0)
            {
                await _ossService.DeleteManyAsync(uploadedPaths);
            }
            throw;
        }
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
}
