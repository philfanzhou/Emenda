using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entity;

namespace Ruoyu.Study.Mistake.Domain.Services;

public class MistakeDomainService
{
    private readonly MistakeDbContext _dbContext;

    public MistakeDomainService(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MistakeEntity> UploadMistakeAsync(MistakeEntity mistake)
    {
        mistake.Id = Guid.NewGuid().ToString();
        mistake.CreatedAt = DateTime.UtcNow;
        mistake.Status = 1; // PENDING
        _dbContext.Mistakes.Add(mistake);
        await _dbContext.SaveChangesAsync();
        return mistake;
    }

    public async Task<bool> DeleteMistakeAsync(string id)
    {
        var mistake = await _dbContext.Mistakes.FindAsync(id);
        if (mistake == null) return false;
        
        // 只能删除 PENDING 状态的错题
        if (mistake.Status != 1) return false;

        _dbContext.Mistakes.Remove(mistake);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<MistakeEntity?> GetMistakeAsync(string id)
    {
        return await _dbContext.Mistakes.FindAsync(id);
    }

    public async Task<bool> UpdateMistakeAsync(string id, Action<MistakeEntity> updateAction)
    {
        var mistake = await _dbContext.Mistakes.FindAsync(id);
        if (mistake == null) return false;

        updateAction(mistake);
        mistake.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkMistakeProcessedAsync(string id)
    {
        var mistake = await _dbContext.Mistakes.FindAsync(id);
        if (mistake == null) return false;

        mistake.Status = 2; // PROCESSED
        mistake.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<(List<MistakeEntity> Items, int TotalCount)> GetMistakeListAsync(
        string studentId, int subject, int grade, int status, int page, int size)
    {
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

    public async Task<MistakePracticeEntity> AddPracticeAsync(MistakePracticeEntity practice)
    {
        practice.Id = Guid.NewGuid().ToString();
        practice.PracticeDate = DateTime.UtcNow;
        _dbContext.MistakePractices.Add(practice);
        await _dbContext.SaveChangesAsync();
        return practice;
    }

    public async Task<List<MistakePracticeEntity>> GetPracticesAsync(string mistakeId)
    {
        return await _dbContext.MistakePractices
            .Where(p => p.MistakeId == mistakeId)
            .OrderBy(p => p.PracticeDate)
            .ToListAsync();
    }
}
