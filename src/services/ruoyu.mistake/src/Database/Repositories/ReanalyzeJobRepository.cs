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

public class ReanalyzeJobRepository : IReanalyzeJobRepository
{
    private readonly MistakeDbContext _dbContext;

    public ReanalyzeJobRepository(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(ReanalyzeJobModel model, CancellationToken ct = default)
    {
        var entity = ToEntity(model);
        await _dbContext.ReanalyzeJobs.AddAsync(entity, ct);
    }

    public async Task<ReanalyzeJobModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _dbContext.ReanalyzeJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, ct);
        return entity == null ? null : ToModel(entity);
    }

    public async Task<List<ReanalyzeJobModel>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new List<ReanalyzeJobModel>();

        var entities = await _dbContext.ReanalyzeJobs
            .AsNoTracking()
            .Where(j => idList.Contains(j.Id))
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<List<ReanalyzeJobModel>> GetActiveByReviewerAsync(string reviewerId, CancellationToken ct = default)
    {
        var entities = await _dbContext.ReanalyzeJobs
            .AsNoTracking()
            .Where(j => j.ReviewerId == reviewerId
                && (j.Status == (int)ReanalyzeJobState.Pending
                    || j.Status == (int)ReanalyzeJobState.Running))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<List<ReanalyzeJobModel>> ClaimPendingAsync(int maxCount, CancellationToken ct = default)
    {
        var entities = await _dbContext.ReanalyzeJobs
            .AsNoTracking()
            .Where(j => j.Status == (int)ReanalyzeJobState.Pending)
            .OrderBy(j => j.CreatedAt)
            .Take(maxCount)
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<List<ReanalyzeJobModel>> GetStaleRunningAsync(CancellationToken ct = default)
    {
        var entities = await _dbContext.ReanalyzeJobs
            .AsNoTracking()
            .Where(j => j.Status == (int)ReanalyzeJobState.Running)
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<int> UpdateStatusAsync(ReanalyzeJobModel model, CancellationToken ct = default)
    {
        var entity = await _dbContext.ReanalyzeJobs.FirstOrDefaultAsync(j => j.Id == model.Id, ct);
        if (entity == null) return 0;

        entity.Status = (int)model.Status;
        entity.ErrorMessage = model.ErrorMessage;
        entity.NewRootCause = model.NewRootCause;
        entity.StartedAt = model.StartedAt;
        entity.CompletedAt = model.CompletedAt;
        entity.VlDurationMs = model.VlDurationMs;

        // Caller is responsible for invoking IUnitOfWork.SaveChangesAsync so that
        // status changes + item updates + process_log are committed atomically.
        return 1;
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        return await _dbContext.ReanalyzeJobs
            .Where(j => (j.Status == (int)ReanalyzeJobState.Completed
                      || j.Status == (int)ReanalyzeJobState.Failed)
                     && j.CompletedAt != null
                     && j.CompletedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static ReanalyzeJobEntity ToEntity(ReanalyzeJobModel m) => new()
    {
        Id = m.Id,
        ItemId = m.ItemId,
        ReviewerId = m.ReviewerId,
        StudentId = m.StudentId,
        TeacherDescription = m.TeacherDescription,
        PreviousRootCause = m.PreviousRootCause,
        Status = (int)m.Status,
        ErrorMessage = m.ErrorMessage,
        NewRootCause = m.NewRootCause,
        CreatedAt = m.CreatedAt,
        StartedAt = m.StartedAt,
        CompletedAt = m.CompletedAt,
        VlDurationMs = m.VlDurationMs,
    };

    private static ReanalyzeJobModel ToModel(ReanalyzeJobEntity e) => new()
    {
        Id = e.Id,
        ItemId = e.ItemId,
        ReviewerId = e.ReviewerId,
        StudentId = e.StudentId,
        TeacherDescription = e.TeacherDescription,
        PreviousRootCause = e.PreviousRootCause,
        Status = (ReanalyzeJobState)e.Status,
        ErrorMessage = e.ErrorMessage,
        NewRootCause = e.NewRootCause,
        CreatedAt = e.CreatedAt,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        VlDurationMs = e.VlDurationMs,
    };
}
