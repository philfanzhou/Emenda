using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emenda.Domain.Models;

namespace Emenda.Domain.Repositories;

public interface IReanalyzeJobRepository
{
    Task AddAsync(ReanalyzeJobModel model, CancellationToken ct = default);
    Task<ReanalyzeJobModel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> GetActiveByReviewerAsync(string reviewerId, CancellationToken ct = default);
    Task<List<ReanalyzeJobModel>> ClaimAsync(int maxCount, CancellationToken ct = default);
    Task<bool> RenewAsync(Guid jobId, Guid ownerId, long version, CancellationToken ct = default);
    Task<bool> CompleteAsync(Guid jobId, Guid ownerId, long version, string rootCause, long durationMs, CancellationToken ct = default);
    Task<bool> FailAsync(Guid jobId, Guid ownerId, long version, string reasonCode, CancellationToken ct = default);
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
