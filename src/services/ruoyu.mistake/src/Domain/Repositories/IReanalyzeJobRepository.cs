using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IReanalyzeJobRepository
{
    Task AddAsync(ReanalyzeJobModel model, CancellationToken ct = default);
    Task<ReanalyzeJobModel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> GetActiveByReviewerAsync(string reviewerId, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> ClaimPendingAsync(int maxCount, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> GetStaleRunningAsync(CancellationToken ct = default);
    Task<int> UpdateStatusAsync(ReanalyzeJobModel model, CancellationToken ct = default);
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
