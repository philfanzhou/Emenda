using Emenda.Domain.Models;

namespace Emenda.Domain.Repositories;

public interface IMistakeItemRepository
{
    Task AddAsync(MistakeItemModel model, CancellationToken ct = default);
    Task<MistakeItemModel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> UpdateAsync(MistakeItemModel model, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);
    Task<bool> ExistsBySourceUploadIdAsync(string sourceUploadId);
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);
    Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null, CancellationToken ct = default);

    /// <summary>True when a conversation-backed item of <paramref name="caller"/> references the exact object path.</summary>
    Task<bool> IsConversationObjectReferencedAsync(string objectPath, string caller, CancellationToken ct = default);
}
