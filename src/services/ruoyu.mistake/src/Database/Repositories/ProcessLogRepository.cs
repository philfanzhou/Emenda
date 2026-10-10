using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;

namespace Ruoyu.Study.Mistake.Database.Repositories;

public class ProcessLogRepository : IProcessLogRepository
{
    private readonly MistakeDbContext _dbContext;

    public ProcessLogRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(ProcessLogModel model, CancellationToken ct = default)
    {
        await LegacyReturnWriteGuard.EnsureAsync(_dbContext, model.MistakeItemId, ct);
        var entity = new ProcessLogEntity
        {
            Id = model.Id,
            MistakeItemId = model.MistakeItemId,
            StudentId = model.StudentId,
            Action = model.Action,
            Details = model.Details,
            CreatedAt = model.CreatedAt
        };
        await _dbContext.ProcessLogs.AddAsync(entity, ct);
    }

    public async Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId)
    {
        var items = await _dbContext.ProcessLogs
            .Where(p => p.MistakeItemId == mistakeItemId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        return items.Select(e => new ProcessLogModel
        {
            Id = e.Id,
            MistakeItemId = e.MistakeItemId,
            StudentId = e.StudentId,
            Action = e.Action,
            Details = e.Details,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task RemoveByMistakeItemIdAsync(Guid mistakeItemId)
    {
        await LegacyReturnWriteGuard.EnsureAsync(_dbContext, mistakeItemId);
        var entities = await _dbContext.ProcessLogs
            .Where(p => p.MistakeItemId == mistakeItemId)
            .ToListAsync();

        if (entities.Count > 0)
        {
            _dbContext.ProcessLogs.RemoveRange(entities);
        }
    }
}
