using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Ruoyu.Study.Mistake.Database.Repositories;

public class MistakeItemRepository : IMistakeItemRepository
{
    private readonly MistakeDbContext _dbContext;

    public MistakeItemRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MistakeItemModel model, CancellationToken ct = default)
    {
        var entity = MapToEntity(model);
        await _dbContext.MistakeItems.AddAsync(entity, ct);
    }

    public async Task<MistakeItemModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _dbContext.MistakeItems.FindAsync(new object?[] { id }, ct);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<bool> UpdateAsync(MistakeItemModel model, CancellationToken ct = default)
    {
        var entity = await _dbContext.MistakeItems.FindAsync(model.Id);
        if (entity == null) return false;

        entity.StudentId = model.StudentId;
        entity.Subject = model.Subject;
        entity.Grade = model.Grade;
        entity.SourceUploadId = model.SourceUploadId;
        entity.SourceRegions = MapSourceRegions(model.SourceRegions);
        entity.ReviewStatus = model.ReviewStatus;
        entity.ReviewerId = model.ReviewerId;
        entity.ReviewedAt = model.ReviewedAt;
        entity.RootCause = model.RootCause;
        entity.QuestionId = model.QuestionId;
        entity.UpdatedAt = model.UpdatedAt;

        _dbContext.MistakeItems.Update(entity);
        return true;
    }

    public async Task SetRootCauseDeferredAsync(Guid itemId, string newRootCause, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var entity = await _dbContext.MistakeItems.FindAsync(new object?[] { itemId }, ct);
        if (entity == null) return;
        entity.RootCause = newRootCause;
        entity.UpdatedAt = updatedAt;
        // Caller must invoke IUnitOfWork.SaveChangesAsync to commit.
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
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
            .ToListAsync();
        // 在客户端排序，因为 SQLite 不支持 DateTimeOffset 的 ORDER BY
        return entities.OrderBy(m => m.CreatedAt).Select(MapToModel).ToList();
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

        if (subject > 0) query = query.Where(m => m.Subject == subject);
        if (grade > 0) query = query.Where(m => m.Grade == grade);
        if (reviewStatus > 0) query = query.Where(m => m.ReviewStatus == reviewStatus);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items.Select(MapToModel).ToList(), totalCount);
    }

    public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null)
    {
        var query = _dbContext.MistakeItems
            .Where(m => m.ReviewStatus == (int)ReviewStatus.PendingReview);

        if (subjects != null && subjects.Count > 0)
        {
            query = query.Where(m => subjects.Contains(m.Subject));
        }

        // 获取所有数据在客户端进行分组和聚合，因为 SQLite 不支持 DateTimeOffset 的 Min
        var items = await query.ToListAsync();
        
        var grouped = items
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
            .ToList();

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

    public async Task<List<SourceUploadSummary>> GetSourceUploadSummariesAsync()
    {
        return await _dbContext.MistakeItems
            .GroupBy(m => new { m.SourceUploadId, m.StudentId })
            .Select(g => new SourceUploadSummary
            {
                SourceUploadId = g.Key.SourceUploadId,
                StudentId = g.Key.StudentId,
                HasPendingReview = g.Any(x => x.ReviewStatus == (int)ReviewStatus.PendingReview),
                HasConfirmedOrRejected = g.Any(x => x.ReviewStatus == (int)ReviewStatus.Confirmed
                                                  || x.ReviewStatus == (int)ReviewStatus.Rejected)
            })
            .ToListAsync();
    }

    public async Task<List<MistakeItemModel>> GetAllAsync()
    {
        var entities = await _dbContext.MistakeItems.ToListAsync();
        return entities.Select(MapToModel).ToList();
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
            SourceRegions = MapSourceRegions(model.SourceRegions),
            ReviewStatus = model.ReviewStatus,
            ReviewerId = model.ReviewerId,
            ReviewedAt = model.ReviewedAt,
            RootCause = model.RootCause,
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
            SourceRegions = entity.SourceRegions?.Select(r => new SourceRegionModel
            {
                SourceImagePath = r.SourceImagePath ?? string.Empty,
                BoundingBox = r.BoundingBox != null
                    ? new BoundingBoxModel
                    {
                        X1 = r.BoundingBox.X1,
                        Y1 = r.BoundingBox.Y1,
                        X2 = r.BoundingBox.X2,
                        Y2 = r.BoundingBox.Y2
                    }
                    : null
            }).ToList() ?? new(),
            ReviewStatus = entity.ReviewStatus,
            ReviewerId = entity.ReviewerId,
            ReviewedAt = entity.ReviewedAt,
            RootCause = entity.RootCause,
            QuestionId = entity.QuestionId,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private static List<SourceRegionEntry> MapSourceRegions(List<SourceRegionModel> regions)
    {
        return regions?.Select(r => new SourceRegionEntry
        {
            SourceImagePath = r.SourceImagePath ?? string.Empty,
            BoundingBox = r.BoundingBox != null
                ? new BoundingBoxEntry
                {
                    X1 = r.BoundingBox.X1,
                    Y1 = r.BoundingBox.Y1,
                    X2 = r.BoundingBox.X2,
                    Y2 = r.BoundingBox.Y2
                }
                : null
        }).ToList() ?? new();
    }
}
