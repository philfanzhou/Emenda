using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IMistakeItemRepository
{
    Task AddAsync(MistakeItemModel model);
    Task<MistakeItemModel?> GetByIdAsync(Guid id);
    Task<bool> UpdateAsync(MistakeItemModel model);
    Task<bool> DeleteAsync(Guid id);
    Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);
    Task<bool> ExistsBySourceUploadIdAsync(string sourceUploadId);
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);
    Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size);
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
