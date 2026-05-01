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

public class ImageFingerprintRepository : IImageFingerprintRepository
{
    private readonly MistakeDbContext _dbContext;

    public ImageFingerprintRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddRangeAsync(List<ImageFingerprintModel> models)
    {
        var entities = models.Adapt<List<ImageFingerprintEntity>>();
        await _dbContext.ImageFingerprints.AddRangeAsync(entities);
    }

    public async Task<List<ImageFingerprintModel>> GetByHashesAndStudentIdAsync(List<string> hashes, string studentId)
    {
        var entities = await _dbContext.ImageFingerprints
            .Where(f => hashes.Contains(f.Hash) && f.StudentId == studentId)
            .ToListAsync();
        return entities.Adapt<List<ImageFingerprintModel>>();
    }

    public async Task RemoveByMistakeIdAsync(Guid mistakeId)
    {
        var entities = await _dbContext.ImageFingerprints
            .Where(f => f.MistakeId == mistakeId)
            .ToListAsync();
        _dbContext.ImageFingerprints.RemoveRange(entities);
    }

    public async Task RemoveByOssPathAsync(string ossPath)
    {
        var entities = await _dbContext.ImageFingerprints
            .Where(f => f.OssPath == ossPath)
            .ToListAsync();
        _dbContext.ImageFingerprints.RemoveRange(entities);
    }
}
