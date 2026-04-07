using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entity;
using Ruoyu.Study.Mistake.Database.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class MistakeDomainService
{
    private readonly MistakeDbContext _dbContext;
    private readonly IOssService _ossService;

    public MistakeDomainService(MistakeDbContext dbContext, IOssService ossService)
    {
        _dbContext = dbContext;
        _ossService = ossService;
    }

    public async Task<MistakeEntity> UploadMistakeAsync(MistakeEntity mistake, List<byte[]> images)
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

        _dbContext.Mistakes.Add(mistake);

        // Record process log
        var processLog = new ProcessLogEntity
        {
            Id = Guid.NewGuid(),
            MistakeId = mistake.Id,
            Action = (int)ProcessAction.Created,
            Details = "Student uploaded mistake",
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.ProcessLogs.Add(processLog);

        await _dbContext.SaveChangesAsync();
        return mistake;
    }

    public async Task<bool> DeleteMistakeAsync(Guid id)
    {
        var mistake = await _dbContext.Mistakes.FindAsync(id);
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

        _dbContext.Mistakes.Remove(mistake);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<MistakeEntity?> GetMistakeAsync(Guid id)
    {
        return await _dbContext.Mistakes.FindAsync(id);
    }

    public async Task<bool> UpdateMistakeAsync(Guid id, Action<MistakeEntity> updateAction, List<byte[]>? images = null)
    {
        var mistake = await _dbContext.Mistakes.FindAsync(id);
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
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkMistakeProcessedAsync(Guid id)
    {
        var mistake = await _dbContext.Mistakes.FindAsync(id);
        if (mistake == null) return false;

        mistake.Status = (int)MistakeStatus.Processed;
        mistake.UpdatedAt = DateTime.UtcNow;

        // Record process log
        var processLog = new ProcessLogEntity
        {
            Id = Guid.NewGuid(),
            MistakeId = mistake.Id,
            Action = (int)ProcessAction.Analyzed,
            Details = "Mistake marked as processed",
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.ProcessLogs.Add(processLog);

        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<(List<MistakeEntity> Items, int TotalCount)> GetMistakeListAsync(
        string studentId, int subject, int grade, int status, int page, int size)
    {
        // Pagination validation
        if (page <= 0) page = 1;
        if (size <= 0) size = 10;
        if (size > 100) size = 100;

        var query = _dbContext.Mistakes.Where(m => m.StudentId == studentId);

        if (subject > 0) query = query.Where(m => m.Subject == subject);
        if (grade > 0) query = query.Where(m => m.Grade == grade);
        if (status > 0) query = query.Where(m => m.Status == status);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.Date)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<MistakePracticeEntity> AddPracticeAsync(MistakePracticeEntity practice, List<byte[]>? images = null)
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

        _dbContext.MistakePractices.Add(practice);

        // Record process log
        var processLog = new ProcessLogEntity
        {
            Id = Guid.NewGuid(),
            MistakeId = practice.MistakeId,
            Action = (int)ProcessAction.Practiced,
            Details = "Student practiced mistake",
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.ProcessLogs.Add(processLog);

        await _dbContext.SaveChangesAsync();
        return practice;
    }

    public async Task<List<MistakePracticeEntity>> GetPracticesAsync(Guid mistakeId)
    {
        return await _dbContext.MistakePractices
            .Where(p => p.MistakeId == mistakeId)
            .OrderBy(p => p.PracticeDate)
            .ToListAsync();
    }

    public async Task<MistakeTypeResponse> AnalyzeMistakeTypeAsync(string content, string correctAnswer, string studentAnswer)
    {
        // Simple analysis logic - in production this would call an AI service
        var type = MistakeType.Other;
        var typeName = "其他错误";
        var analysisReason = "根据答案对比无法明确分类";

        // Record process log
        var mistake = await _dbContext.Mistakes.FirstOrDefaultAsync(m => m.Content == content);
        if (mistake != null)
        {
            var processLog = new ProcessLogEntity
            {
                Id = Guid.NewGuid(),
                MistakeId = mistake.Id,
                Action = (int)ProcessAction.Analyzed,
                Details = $"Mistake analyzed as {typeName}",
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.ProcessLogs.Add(processLog);
            await _dbContext.SaveChangesAsync();
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
        var mistake = await _dbContext.Mistakes.FindAsync(mistakeId);
        if (mistake == null) return false;

        var processLog = new ProcessLogEntity
        {
            Id = Guid.NewGuid(),
            MistakeId = mistakeId,
            Action = (int)action,
            Details = details,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.ProcessLogs.Add(processLog);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<List<ProcessLogEntity>> GetMistakeProcessHistoryAsync(Guid mistakeId)
    {
        return await _dbContext.ProcessLogs
            .Where(p => p.MistakeId == mistakeId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }
}
