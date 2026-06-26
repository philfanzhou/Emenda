using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IMistakeItemRepository
{
    Task AddAsync(MistakeItemModel model, CancellationToken ct = default);
    Task<MistakeItemModel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> UpdateAsync(MistakeItemModel model, CancellationToken ct = default);

    /// <summary>
    /// Update only the root_cause + updated_at fields. Does NOT call SaveChanges;
    /// the caller is responsible for committing via IUnitOfWork. Used by
    /// ReanalyzeJobService to keep the item update, job status change, and
    /// ProcessLog insert atomic in a single transaction.
    /// </summary>
    Task SetRootCauseDeferredAsync(Guid itemId, string newRootCause, DateTimeOffset updatedAt, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);
    Task<bool> ExistsBySourceUploadIdAsync(string sourceUploadId);
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);
    Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null);
    Task<int> CountPendingBySourceUploadIdAsync(string sourceUploadId);
    Task<List<SourceUploadSummary>> GetSourceUploadSummariesAsync();
    Task<List<MistakeItemModel>> GetAllAsync();
}

public class SourceUploadSummary
{
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public bool HasPendingReview { get; set; }
    public bool HasConfirmedOrRejected { get; set; }
}
