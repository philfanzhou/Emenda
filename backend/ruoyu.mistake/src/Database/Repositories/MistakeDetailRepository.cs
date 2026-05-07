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

public class MistakeDetailRepository : IMistakeDetailRepository
{
    private readonly MistakeDbContext _dbContext;

    public MistakeDetailRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MistakeDetailModel model)
    {
        var entity = model.Adapt<MistakeDetailEntity>();
        await _dbContext.MistakeDetails.AddAsync(entity);
    }

    public async Task<MistakeDetailModel?> GetByMistakeIdAsync(Guid mistakeId)
    {
        var entity = await _dbContext.MistakeDetails.FirstOrDefaultAsync(d => d.MistakeId == mistakeId);
        return entity?.Adapt<MistakeDetailModel>();
    }

    public async Task<bool> UpdateAsync(MistakeDetailModel model)
    {
        var entity = await _dbContext.MistakeDetails.FirstOrDefaultAsync(d => d.MistakeId == model.MistakeId);
        if (entity == null) return false;

        entity.OriginalImages = model.OriginalImages;
        entity.Comments = model.Comments;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteByMistakeIdAsync(Guid mistakeId)
    {
        var entity = await _dbContext.MistakeDetails.FirstOrDefaultAsync(d => d.MistakeId == mistakeId);
        if (entity == null) return false;

        _dbContext.MistakeDetails.Remove(entity);
        return true;
    }

    public async Task RemoveByMistakeIdAsync(Guid mistakeId)
    {
        var entity = await _dbContext.MistakeDetails.FirstOrDefaultAsync(d => d.MistakeId == mistakeId);
        if (entity != null)
        {
            _dbContext.MistakeDetails.Remove(entity);
        }
    }

    public async Task<bool> RemoveImageAsync(Guid mistakeId, string imagePath)
    {
        var entity = await _dbContext.MistakeDetails.FirstOrDefaultAsync(d => d.MistakeId == mistakeId);
        if (entity == null) return false;

        var removed = entity.OriginalImages.Remove(imagePath);
        if (!removed) return false;

        await _dbContext.SaveChangesAsync();
        return true;
    }
}
