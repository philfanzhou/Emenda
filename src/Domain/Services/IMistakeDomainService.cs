using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emenda.Domain.Models;

namespace Emenda.Domain.Services;

public interface IMistakeDomainService
{
    Task<List<MistakeItemModel>> SubmitMistakeUploadAsync(string studentId, int subject, int grade, List<string> imagePaths, string? rootCause, string sourceUploadId);
    Task<MistakeItemModel?> GetMistakeItemAsync(Guid id);
    Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId);
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);

    /// <summary>
    /// List variant scoped to a set of student ids (IKJEPZ learner default
    /// scope injection). <paramref name="studentIds"/> must be non-empty.
    /// </summary>
    Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListByStudentsAsync(
        IReadOnlyCollection<string> studentIds, int subject, int grade, int reviewStatus, int page, int size);

    /// <summary>
    /// Whether the object path is referenced by an item source region or a
    /// practice image path owned by one of the given students (IKJEPZ
    /// presigned-url ownership check). <paramref name="studentIds"/> must be
    /// non-empty.
    /// </summary>
    Task<bool> IsObjectPathOwnedByStudentsAsync(string objectPath, IReadOnlyCollection<string> studentIds, CancellationToken ct = default);

    Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(int page, int size, List<int>? subjects = null, CancellationToken ct = default);
    Task<MistakeItemModel?> ReviewMistakeItemAsync(Guid id, int reviewStatus, string? reviewerId, string? rootCause, int? grade = null, int? subject = null);
    Task<MistakeReanalysisResult> ReanalyzeMistakeItemAsync(Guid id, string? reviewerId, string? teacherDescription, CancellationToken ct = default);
    Task PersistReanalyzedRootCauseAsync(MistakeItemModel item, string previousRootCause, string? reviewerId, string? teacherDescription, string newRootCause, CancellationToken ct = default);
    Task LogReanalyzeFailureAsync(MistakeItemModel item, string? reviewerId, string? teacherDescription, string failureReason, CancellationToken ct = default);
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

public class MistakeReanalysisResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public MistakeItemModel? Item { get; set; }
}
