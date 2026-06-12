using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;

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
        var entity = new MistakePracticeEntity
        {
            Id = model.Id,
            MistakeItemId = model.MistakeItemId,
            StudentId = model.StudentId,
            PracticeDate = model.PracticeDate,
            ImagePaths = model.ImagePaths,
            StudentAnswer = model.StudentAnswer,
            IsCorrect = model.IsCorrect,
            Comments = model.Comments
        };
        await _dbContext.MistakePractices.AddAsync(entity);
    }

    public async Task<List<MistakePracticeModel>> GetByMistakeItemIdAsync(Guid mistakeItemId)
    {
        var items = await _dbContext.MistakePractices
            .Where(p => p.MistakeItemId == mistakeItemId)
            .OrderBy(p => p.PracticeDate)
            .ToListAsync();
        return items.Select(e => new MistakePracticeModel
        {
            Id = e.Id,
            MistakeItemId = e.MistakeItemId,
            StudentId = e.StudentId,
            PracticeDate = e.PracticeDate,
            ImagePaths = e.ImagePaths,
            StudentAnswer = e.StudentAnswer,
            IsCorrect = e.IsCorrect,
            Comments = e.Comments
        }).ToList();
    }

    public async Task RemoveByMistakeItemIdAsync(Guid mistakeItemId)
    {
        var entities = await _dbContext.MistakePractices
            .Where(p => p.MistakeItemId == mistakeItemId)
            .ToListAsync();

        if (entities.Count > 0)
        {
            _dbContext.MistakePractices.RemoveRange(entities);
        }
    }

    public async Task<List<MistakePracticeModel>> GetAllAsync()
    {
        var entities = await _dbContext.MistakePractices.ToListAsync();
        return entities.Select(e => new MistakePracticeModel
        {
            Id = e.Id,
            MistakeItemId = e.MistakeItemId,
            StudentId = e.StudentId,
            PracticeDate = e.PracticeDate,
            ImagePaths = e.ImagePaths,
            StudentAnswer = e.StudentAnswer,
            IsCorrect = e.IsCorrect,
            Comments = e.Comments
        }).ToList();
    }

    public async Task<List<string>> GetAllPracticeImagePathsAsync()
    {
        // Only select the image_paths column to avoid loading full entities
        var imagePathsList = await _dbContext.MistakePractices
            .Select(x => x.ImagePaths)
            .ToListAsync();

        var paths = new List<string>();
        foreach (var imagePaths in imagePathsList)
        {
            if (imagePaths == null) continue;
            foreach (var path in imagePaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
        }
        return paths;
    }
}
