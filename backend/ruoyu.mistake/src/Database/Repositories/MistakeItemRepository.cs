using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Repositories;

namespace Ruoyu.Study.Mistake.Database.Repositories;

public class MistakeItemRepository : IMistakeItemRepository
{
    private readonly MistakeDbContext _dbContext;

    public MistakeItemRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MistakeItemModel model)
    {
        var entity = MapToEntity(model);
        await _dbContext.MistakeItems.AddAsync(entity);
    }

    public async Task<MistakeItemModel?> GetByIdAsync(Guid id)
    {
        var entity = await _dbContext.MistakeItems.FindAsync(id);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<bool> UpdateAsync(MistakeItemModel model)
    {
        var entity = await _dbContext.MistakeItems.FindAsync(model.Id);
        if (entity == null) return false;

        entity.StudentId = model.StudentId;
        entity.Subject = model.Subject;
        entity.Grade = model.Grade;
        entity.SourceUploadId = model.SourceUploadId;
        entity.SourceImagePath = model.SourceImagePath;
        entity.BboxX1 = model.BoundingBox?.X1;
        entity.BboxY1 = model.BoundingBox?.Y1;
        entity.BboxX2 = model.BoundingBox?.X2;
        entity.BboxY2 = model.BoundingBox?.Y2;
        entity.QuestionNumber = model.QuestionNumber;
        entity.QuestionText = model.QuestionText;
        entity.StudentAnswer = model.StudentAnswer;
        entity.AiConfidence = model.AiConfidence;
        entity.ReviewStatus = model.ReviewStatus;
        entity.ReviewerId = model.ReviewerId;
        entity.ReviewedAt = model.ReviewedAt;
        entity.ReviewComment = model.ReviewComment;
        entity.Type = model.Type;
        entity.QuestionId = model.QuestionId;
        entity.UpdatedAt = model.UpdatedAt;

        _dbContext.MistakeItems.Update(entity);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var entity = await _dbContext.MistakeItems.FindAsync(id);
        if (entity == null) return false;

        _dbContext.MistakeItems.Remove(entity);
        return true;
    }

    public async Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId)
    {
        var entities = await _dbContext.MistakeItems
            .Where(m => m.SourceUploadId == sourceUploadId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
        return entities.Select(MapToModel).ToList();
    }

    public async Task<bool> ExistsBySourceUploadIdAsync(string sourceUploadId)
    {
        return await _dbContext.MistakeItems
            .AnyAsync(m => m.SourceUploadId == sourceUploadId);
    }

    public async Task<(List<MistakeItemModel> Items, int TotalCount)> GetListAsync(
        string studentId, int subject, int grade, int reviewStatus, int page, int size)
    {
        var query = _dbContext.MistakeItems.Where(m => m.StudentId == studentId);

        if (subject >= 0) query = query.Where(m => m.Subject == subject);
        if (grade >= 0) query = query.Where(m => m.Grade == grade);
        if (reviewStatus > 0) query = query.Where(m => m.ReviewStatus == reviewStatus);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items.Select(MapToModel).ToList(), totalCount);
    }

    public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size)
    {
        var grouped = await _dbContext.MistakeItems
            .Where(m => m.ReviewStatus == (int)ReviewStatus.PendingReview)
            .GroupBy(m => new { m.SourceUploadId, m.StudentId, m.Subject, m.Grade })
            .Select(g => new
            {
                g.Key.SourceUploadId,
                g.Key.StudentId,
                g.Key.Subject,
                g.Key.Grade,
                PendingCount = g.Count(),
                EarliestCreatedAt = g.Min(x => x.CreatedAt)
            })
            .OrderBy(x => x.EarliestCreatedAt)
            .ToListAsync();

        var totalCount = grouped.Count;
        var paged = grouped
            .Skip((page - 1) * size)
            .Take(size)
            .Select(g => new PendingReviewUploadModel
            {
                SourceUploadId = g.SourceUploadId,
                StudentId = g.StudentId,
                Subject = g.Subject,
                Grade = g.Grade,
                PendingCount = g.PendingCount,
                TotalCount = g.PendingCount,
                EarliestCreatedAt = g.EarliestCreatedAt
            })
            .ToList();

        return (paged, totalCount);
    }

    public async Task<int> CountPendingBySourceUploadIdAsync(string sourceUploadId)
    {
        return await _dbContext.MistakeItems
            .CountAsync(m => m.SourceUploadId == sourceUploadId
                          && m.ReviewStatus == (int)ReviewStatus.PendingReview);
    }

    private static MistakeItemEntity MapToEntity(MistakeItemModel model)
    {
        return new MistakeItemEntity
        {
            Id = model.Id,
            StudentId = model.StudentId,
            Subject = model.Subject,
            Grade = model.Grade,
            SourceUploadId = model.SourceUploadId,
            SourceImagePath = model.SourceImagePath,
            BboxX1 = model.BoundingBox?.X1,
            BboxY1 = model.BoundingBox?.Y1,
            BboxX2 = model.BoundingBox?.X2,
            BboxY2 = model.BoundingBox?.Y2,
            QuestionNumber = model.QuestionNumber,
            QuestionText = model.QuestionText,
            StudentAnswer = model.StudentAnswer,
            AiConfidence = model.AiConfidence,
            ReviewStatus = model.ReviewStatus,
            ReviewerId = model.ReviewerId,
            ReviewedAt = model.ReviewedAt,
            ReviewComment = model.ReviewComment,
            Type = model.Type,
            QuestionId = model.QuestionId,
            CreatedAt = model.CreatedAt,
            UpdatedAt = model.UpdatedAt
        };
    }

    private static MistakeItemModel MapToModel(MistakeItemEntity entity)
    {
        return new MistakeItemModel
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            Subject = entity.Subject,
            Grade = entity.Grade,
            SourceUploadId = entity.SourceUploadId,
            SourceImagePath = entity.SourceImagePath,
            BoundingBox = entity.BboxX1.HasValue
                ? new BoundingBoxModel
                {
                    X1 = entity.BboxX1!.Value,
                    Y1 = entity.BboxY1!.Value,
                    X2 = entity.BboxX2!.Value,
                    Y2 = entity.BboxY2!.Value
                }
                : null,
            QuestionNumber = entity.QuestionNumber,
            QuestionText = entity.QuestionText,
            StudentAnswer = entity.StudentAnswer,
            AiConfidence = entity.AiConfidence,
            ReviewStatus = entity.ReviewStatus,
            ReviewerId = entity.ReviewerId,
            ReviewedAt = entity.ReviewedAt,
            ReviewComment = entity.ReviewComment,
            Type = entity.Type,
            QuestionId = entity.QuestionId,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }
}
