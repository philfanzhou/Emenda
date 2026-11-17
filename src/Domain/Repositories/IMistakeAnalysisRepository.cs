using Emenda.Domain.Models;

namespace Emenda.Domain.Repositories;

public interface IMistakeAnalysisRepository
{
    Task<Guid> StartAsync(Guid itemId, string reviewerId, string caller, Guid requestId,
        long expectedRevision, string? description, CancellationToken ct = default);
    Task<MistakeAnalysisModel?> GetAsync(Guid itemId, CancellationToken ct = default);
    Task<MistakeAnalysisModel> ConfirmAsync(Guid itemId, long expectedRevision, string reviewerId,
        CancellationToken ct = default);
}
