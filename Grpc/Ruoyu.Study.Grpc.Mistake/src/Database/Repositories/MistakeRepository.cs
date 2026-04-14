using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Mapster;

namespace Ruoyu.Study.Mistake.Database.Repositories;

public class MistakeRepository : IMistakeRepository
{
    private readonly MistakeDbContext _dbContext;

    public MistakeRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MistakeModel model)
    {
        var entity = model.Adapt<MistakeEntity>();
        await _dbContext.Mistakes.AddAsync(entity);
    }

    public async Task<MistakeModel?> GetByIdAsync(Guid id)
    {
        var entity = await _dbContext.Mistakes.FindAsync(id);
        return entity?.Adapt<MistakeModel>();
    }

    public async Task<MistakeModel?> GetByContentAsync(string content)
    {
        var entity = await _dbContext.Mistakes.FirstOrDefaultAsync(m => m.Content == content);
        return entity?.Adapt<MistakeModel>();
    }

    public void Remove(MistakeModel model)
    {
        // When removing, we just need the entity with the ID to be tracked/deleted
        var entity = model.Adapt<MistakeEntity>();
        _dbContext.Mistakes.Remove(entity);
    }

    public async Task<(List<MistakeModel> Items, int TotalCount)> GetListAsync(string studentId, int subject, int grade, int status, int page, int size)
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

        return (items.Adapt<List<MistakeModel>>(), totalCount);
    }
}
