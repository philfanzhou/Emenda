using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;

namespace Emenda.Database.Repositories;

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
        entity.ConversationCaller = model.ConversationCaller;
        entity.ConversationSource = model.ConversationSource;
        entity.ConversationActive = model.ConversationActive;
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
        // Sort on client side to ensure consistent ordering
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
        if (reviewStatus == 4) query = query.Where(m => m.ReviewStatus == (int)ReviewStatus.Confirmed || m.ReviewStatus == (int)ReviewStatus.PendingReview && m.ConversationActive && m.ConversationSource != null);
        else if (reviewStatus > 0) query = query.Where(m => m.ReviewStatus == reviewStatus);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items.Select(MapToModel).ToList(), totalCount);
    }

    public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null, CancellationToken ct = default)
    {
        var query = _dbContext.MistakeItems
            .Where(m => m.ReviewStatus == (int)ReviewStatus.PendingReview && m.ConversationSource == null);

        if (subjects != null && subjects.Count > 0)
        {
            query = query.Where(m => subjects.Contains(m.Subject));
        }

        var grouped = query.AsNoTracking()
            .GroupBy(m => new { m.SourceUploadId, m.StudentId, m.Subject, m.Grade })
            .Select(g => new PendingReviewUploadModel
            {
                SourceUploadId = g.Key.SourceUploadId,
                StudentId = g.Key.StudentId,
                Subject = g.Key.Subject,
                Grade = g.Key.Grade,
                PendingCount = g.Count(),
                TotalCount = g.Count(),
                EarliestCreatedAt = g.Min(x => x.CreatedAt),
            });
        var totalCount = await grouped.CountAsync(ct);
        ct.ThrowIfCancellationRequested();
        var offset = ((long)page - 1) * size;
        if (offset >= totalCount) return ([], totalCount);
        var paged = await grouped.OrderBy(g => g.EarliestCreatedAt).ThenBy(g => g.SourceUploadId)
            .ThenBy(g => g.StudentId).ThenBy(g => g.Subject).ThenBy(g => g.Grade)
            .Skip((int)offset).Take(size).ToListAsync(ct);

        return (paged, totalCount);
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
            ConversationCaller = model.ConversationCaller,
            ConversationSource = model.ConversationSource,
            ConversationActive = model.ConversationActive,
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
            ConversationCaller = entity.ConversationCaller,
            ConversationSource = entity.ConversationSource,
            ConversationActive = entity.ConversationActive,
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

    public async Task<bool> IsConversationObjectReferencedAsync(string objectPath, string caller, CancellationToken ct = default)
    {
        return await _dbContext.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM mistake_item WHERE conversation_source IS NOT NULL
              AND conversation_caller={caller}
              AND source_regions @> jsonb_build_array(jsonb_build_object('sourceImagePath', {objectPath}))) AS "Value"
            """).SingleAsync(ct);
    }
}
