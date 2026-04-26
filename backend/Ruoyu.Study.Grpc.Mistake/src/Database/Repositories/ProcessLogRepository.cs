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

public class ProcessLogRepository : IProcessLogRepository
{
    private readonly MistakeDbContext _dbContext;

    public ProcessLogRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(ProcessLogModel model)
    {
        var entity = model.Adapt<ProcessLogEntity>();
        await _dbContext.ProcessLogs.AddAsync(entity);
    }

    public async Task<List<ProcessLogModel>> GetByMistakeIdAsync(Guid mistakeId)
    {
        var items = await _dbContext.ProcessLogs
            .Where(p => p.MistakeId == mistakeId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        return items.Adapt<List<ProcessLogModel>>();
    }

    public async Task RemoveByMistakeIdAsync(Guid mistakeId)
    {
        var entities = await _dbContext.ProcessLogs
            .Where(p => p.MistakeId == mistakeId)
            .ToListAsync();
            
        if (entities.Count > 0)
        {
            _dbContext.ProcessLogs.RemoveRange(entities);
        }
    }
}
