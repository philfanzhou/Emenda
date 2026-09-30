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

    /// <summary>
    /// List variant filtered by a set of student ids instead of a single
    /// student id. Used by the /api list endpoint to inject the learner's
    /// allowed-student scope when no studentId parameter is supplied
    /// (IKJEPZ). Callers must pass a non-empty set.
    /// </summary>
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetListByStudentIdsAsync(
        IReadOnlyCollection<string> studentIds, int subject, int grade, int reviewStatus, int page, int size);

    /// <summary>
    /// Whether <paramref name="objectPath"/> is referenced (Ordinal exact)
    /// by a mistake item's source-region image path or a mistake practice's
    /// image path owned by one of <paramref name="studentIds"/> (IKJEPZ
    /// presigned-url ownership check). Callers must pass a non-empty set.
    /// </summary>
    Task<bool> IsObjectPathReferencedByStudentsAsync(
        string objectPath, IReadOnlyCollection<string> studentIds, CancellationToken ct = default);

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
