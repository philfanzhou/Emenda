using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Services;

public interface IMistakeDomainService
{
    Task<List<MistakeItemModel>> SubmitMistakeUploadAsync(string studentId, int subject, int grade, List<string> imagePaths, string? rootCause, string sourceUploadId);
    Task<MistakeItemModel?> GetMistakeItemAsync(Guid id);
    Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId);
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);
    Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null);
    Task<MistakeItemModel?> ReviewMistakeItemAsync(Guid id, int reviewStatus, string? reviewerId, string? rootCause, int? grade = null, int? subject = null);
    Task<MistakeItemModel> AddMistakeItemAsync(MistakeItemModel item);
    Task<bool> DeleteMistakeItemAsync(Guid id);
    Task<MistakeItemModel?> UpdateMistakeItemPartialAsync(Guid id, string? studentId = null, int? subject = null, int? grade = null, List<SourceRegionModel>? sourceRegions = null);
    Task<bool> UpdateMistakeItemAsync(MistakeItemModel model);
    [Obsolete("目标流程中不存在独立的 CompleteUploadReview 步骤。仅保留供 LegacyClean 运维兜底使用。")]
    Task<CompleteUploadReviewResult> CompleteUploadReviewAsync(string sourceUploadId, string? reviewerId);
    Task<MistakePracticeModel> AddPracticeAsync(MistakePracticeModel practice, List<string>? imagePaths = null);
    Task<List<MistakePracticeModel>> GetPracticesAsync(Guid mistakeItemId);
    Task<bool> LogMistakeProcessAsync(Guid mistakeItemId, string studentId, ProcessAction action, string details);
    Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeItemId);
}
