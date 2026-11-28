using Emenda.Domain.Models;

namespace Emenda.Service.Dtos;

// ReviewStatus and ProcessAction are defined in Emenda.Domain.Models
// (see Domain/Models/MistakeEnums.cs) and reused by DTOs here.

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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Emenda.HttpContracts.ConversationQuestionSnapshot? ConversationSource { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConversationActive { get; set; }
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
    /// <summary>mistake-http-v7: extraction, conversation or legacy.</summary>
    public string SourceKind { get; set; } = MistakeSourceKinds.Legacy;
    /// <summary>mistake-http-v7: the question in one shape for every source; images match sourceRegions one to one.</summary>
    public MistakeContentDto Content { get; set; } = MistakeContentDto.Empty;
}

/// <summary>
/// The unified question content (content version 1). A text member is null when the source has no such text
/// or when it is withheld: an extraction mistake shows its text only while pending review or confirmed.
/// </summary>
public sealed record MistakeContentDto(
    int Version,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? Statement,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? StudentAnswer,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? ReferenceAnswer,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? Explanation,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? Evidence,
    IReadOnlyList<string> KnowledgePoints,
    IReadOnlyList<MistakeContentImageDto> Images)
{
    public static MistakeContentDto Empty { get; } = new(1, null, null, null, null, null, [], []);
}

/// <summary>One image of <see cref="MistakeContentDto"/>: role is "question" (the question crop) or "source" (an original kept as evidence).</summary>
public sealed record MistakeContentImageDto(
    string Path,
    string Role,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] int[]? Box);

/// <summary>
/// An extraction mistake's analysis as the extraction pipeline records it (the former analysis_snapshot shape).
/// Not part of any HTTP response since mistake-http-v8; clients read <see cref="MistakeItemDto.Content"/>.
/// </summary>
public sealed record MistakeAnalysisSnapshotDto(string Statement, string? StudentAnswer, string ReferenceAnswer,
    string Explanation, string Evidence, int SourceVersion);

// ===== Request DTOs =====

public class MistakeItemPageResult
{
    public List<MistakeItemDto> Items { get; set; } = new();
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
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public List<string> RemovedImagePaths { get; set; } = new();
}

public class UpdateMistakeItemRequest
{
    public string Id { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public List<SourceRegionDto> SourceRegions { get; set; } = new();
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
