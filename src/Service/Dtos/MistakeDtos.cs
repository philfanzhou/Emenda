using Emenda.Domain.Models;

namespace Emenda.Service.Dtos;

// ===== Enums =====
// ReviewStatus and ProcessAction are defined in Emenda.Domain.Models
// (see Domain/Models/MistakeEnums.cs) and reused by DTOs here.
// ReanalyzeJobStatus is DTO-specific (Domain uses ReanalyzeJobState with the same values).

/// <summary>
/// Reanalyze job lifecycle status. Migrated from proto ReanalyzeJobStatus enum.
/// Values match Domain.Models.ReanalyzeJobState.
/// </summary>
public enum ReanalyzeJobStatus
{
    Unspecified = 0,
    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

// ===== Shared DTOs =====

public class BoundingBoxDto
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
}

public class SourceRegionDto
{
    public string SourceImagePath { get; set; } = string.Empty;
    public BoundingBoxDto? BoundingBox { get; set; }
}

public class PageMetaDto
{
    public int Page { get; set; }
    public int Size { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

// ===== MistakeItem DTOs =====

public class MistakeItemDto
{
    public string Id { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string SourceUploadId { get; set; } = string.Empty;
    public List<SourceRegionDto> SourceRegions { get; set; } = new();
    public ReviewStatus ReviewStatus { get; set; }
    public string ReviewerId { get; set; } = string.Empty;
    public string ReviewedAt { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

// ===== Request DTOs =====

public class StartLayoutJobRequest
{
    public Guid? RequestKey { get; set; }
}

public class SubmitMistakeUploadRequest
{
    public Guid? ExpectedContentRevision { get; set; }
    public Guid? RequestKey { get; set; }
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public List<string> ImagePaths { get; set; } = new();
    public List<SourceRegionDto?>? SourceRegions { get; set; }
    public string? RootCause { get; set; }
    public string SourceUploadId { get; set; } = string.Empty;
}

public class SubmitMistakeUploadResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public List<string> CreatedItemIds { get; set; } = new();
}

public class GetMistakeItemListRequest
{
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public ReviewStatus ReviewStatus { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
}

public class MistakeItemPageResult
{
    public List<MistakeItemDto> Items { get; set; } = new();
    public PageMetaDto PageMeta { get; set; } = new();
}

public class PendingReviewUploadDto
{
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public int PendingCount { get; set; }
    public int TotalCount { get; set; }
    public string EarliestCreatedAt { get; set; } = string.Empty;
}

public class PendingReviewUploadListResponse
{
    public List<PendingReviewUploadDto> Uploads { get; set; } = new();
    public PageMetaDto PageMeta { get; set; } = new();
}

public class ReviewMistakeItemRequest
{
    public string Id { get; set; } = string.Empty;
    public ReviewStatus ReviewStatus { get; set; }
    public string ReviewerId { get; set; } = string.Empty;
    public string? RootCause { get; set; }
    public int Grade { get; set; }
    public int Subject { get; set; }
    public string? ReturnReason { get; set; }
}

public class ReviewMistakeItemResponse
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnState { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ConfirmedRecordId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorKind { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public List<string> RemovedImagePaths { get; set; } = new();
}

public class ReanalyzeMistakeItemRequest
{
    public string Id { get; set; } = string.Empty;
    public string ReviewerId { get; set; } = string.Empty;
    public string? TeacherDescription { get; set; }
    public string StudentId { get; set; } = string.Empty;
}

public class ReanalyzeMistakeItemResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
}

public class ReanalyzeJobDto
{
    public string JobId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public ReanalyzeJobStatus Status { get; set; }
    public string NewRootCause { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
    public string CompletedAt { get; set; } = string.Empty;
    public long VlDurationMs { get; set; }
}

public class GetReanalyzeJobsRequest
{
    public string ReviewerId { get; set; } = string.Empty;
    public List<string> JobIds { get; set; } = new();
}

public class GetReanalyzeJobsResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public List<ReanalyzeJobDto> Jobs { get; set; } = new();
}

public class AddMistakeItemRequest
{
    public Guid? ExpectedContentRevision { get; set; }
    public Guid? RequestKey { get; set; }
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string SourceUploadId { get; set; } = string.Empty;
    public List<SourceRegionDto> SourceRegions { get; set; } = new();
    public string? RootCause { get; set; }
}

public class UpdateMistakeItemRequest
{
    public string Id { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public List<SourceRegionDto> SourceRegions { get; set; } = new();
}

public class CompleteUploadReviewRequest
{
    public string SourceUploadId { get; set; } = string.Empty;
    public string ReviewerId { get; set; } = string.Empty;
}

public class CompleteUploadReviewResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public List<string> RemovedImagePaths { get; set; } = new();
}

public class AddMistakePracticeRequest
{
    public string MistakeItemId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public List<string> ImagePaths { get; set; } = new();
    public string? StudentAnswer { get; set; }
    public bool IsCorrect { get; set; }
    public string? Comments { get; set; }
}

public class MistakePracticeDto
{
    public string Id { get; set; } = string.Empty;
    public string MistakeItemId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string PracticeDate { get; set; } = string.Empty;
    public List<string> ImagePaths { get; set; } = new();
    public string StudentAnswer { get; set; } = string.Empty;
    public bool IsCorrect { get; set; }
    public string Comments { get; set; } = string.Empty;
}

public class MistakePracticeListResponse
{
    public List<MistakePracticeDto> Practices { get; set; } = new();
}

public class LogMistakeProcessRequest
{
    public string MistakeItemId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public ProcessAction Action { get; set; }
    public string? Details { get; set; }
}

public class ProcessLogDto
{
    public string Id { get; set; } = string.Empty;
    public string MistakeItemId { get; set; } = string.Empty;
    public ProcessAction Action { get; set; }
    public string Details { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
}

public class ProcessLogListResponse
{
    public List<ProcessLogDto> Logs { get; set; } = new();
}

// ===== OSS Proxy DTOs =====

public class GetPresignedUrlRequest
{
    public string ObjectPath { get; set; } = string.Empty;
    public int ExpirySeconds { get; set; }
    public string? Size { get; set; }
}

public class PresignedUrlResponse
{
    public string Url { get; set; } = string.Empty;
    public int ExpirySeconds { get; set; }
}
