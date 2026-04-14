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

public class MistakePracticeRepository : IMistakePracticeRepository
{
    private readonly MistakeDbContext _dbContext;

    public MistakePracticeRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MistakePracticeModel model)
    {
        var entity = model.Adapt<MistakePracticeEntity>();
        await _dbContext.MistakePractices.AddAsync(entity);
    }

    public async Task<List<MistakePracticeModel>> GetByMistakeIdAsync(Guid mistakeId)
    {
        var items = await _dbContext.MistakePractices
            .Where(p => p.MistakeId == mistakeId)
            .OrderBy(p => p.PracticeDate)
            .ToListAsync();
        return items.Adapt<List<MistakePracticeModel>>();
    }
}
