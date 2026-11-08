using Emenda.Domain.Models;
namespace Emenda.Domain.Services;

public interface IMistakeSourceGuard
{
    Task EnsureLegacySourceAsync(string sourceId, CancellationToken ct = default);
    Task EnsureDestructiveItemAsync(Guid itemId, CancellationToken ct = default);
    Task EnsureOwnedWriteAsync(Guid itemId, string studentId, CancellationToken ct = default);
    Task EnsureIdentityAsync(MistakeItemModel item, CancellationToken ct = default);
}
