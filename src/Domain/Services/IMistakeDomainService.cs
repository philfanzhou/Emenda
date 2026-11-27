using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emenda.Domain.Models;

namespace Emenda.Domain.Services;

public interface IMistakeDomainService
{
    Task<MistakeItemModel?> GetMistakeItemAsync(Guid id);
    Task<bool> IsConversationObjectReferencedAsync(string objectPath, string caller, CancellationToken ct = default);
    Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId);
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);

    Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null, CancellationToken ct = default);
    Task<MistakeItemModel?> ReviewMistakeItemAsync(Guid id, int reviewStatus, string? reviewerId, string? rootCause, int? grade = null, int? subject = null, string? reviewReason = null);
    Task<bool> DeleteMistakeItemAsync(Guid id);
    Task<MistakeItemModel?> UpdateMistakeItemPartialAsync(Guid id, string? studentId = null, int? subject = null, int? grade = null, List<SourceRegionModel>? sourceRegions = null);
    Task<bool> LogMistakeProcessAsync(Guid mistakeItemId, string studentId, ProcessAction action, string details);
    Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeItemId);
}
