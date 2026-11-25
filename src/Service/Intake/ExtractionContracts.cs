using System.Text.Json.Serialization;

namespace Emenda.Service.Intake;

/// <summary>Normalized regions always use the upright displayed image, not stored EXIF pixels.</summary>
public sealed record ExtractionRegion(Guid SourceId, double X0, double Y0, double X1, double Y1);
public sealed record ExtractionSource(Guid Id, string Path, string ContentType, long Length, string Sha256,
    int Width, int Height, int? Orientation);
public sealed record ExtractionQuestionDraft
{
    public Guid Id { get; init; }
    // Omit unknown labels so retained pre-numbering request hashes remain valid.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuestionNumber { get; init; }
    public int Subject { get; init; }
    // Legacy snapshots and revision hashes have no content grade.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? Grade { get; init; }
    public string Statement { get; init; } = "";
    public string? StudentAnswer { get; init; }
    public string ReferenceAnswer { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string Outcome { get; init; } = "unknown";
    public string Evidence { get; init; } = "";
    public List<ExtractionRegion> Regions { get; init; } = [];
}
public sealed record ExtractionCrop(string Path, int Width, int Height, Guid SourceId, ExtractionRegion Region,
    long Length, string Sha256);
public sealed record ExtractionQuestionView(ExtractionQuestionDraft Question, Guid? ItemId, bool ItemDeleted,
    List<ExtractionCrop> Images)
{
    public ExtractionSupplementView? Supplement { get; init; }
}
public sealed record ExtractionSnapshot(List<ExtractionQuestionView> Questions);
public sealed record ExtractionVersionView(int Version, Guid RequestKey, string Actor, string Reason,
    string Model, DateTimeOffset CreatedAt);
public sealed record ExtractionJobView(Guid Id, string StudentId, int Grade, string Status, int Version,
    int Attempt, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    List<ExtractionSource> Sources, List<ExtractionQuestionView> Questions, List<ExtractionVersionView> Versions)
{
    public bool Superseded { get; init; }
}
public sealed record ExtractionRevisionRequest(Guid RequestKey, int ExpectedVersion, string Reason,
    List<ExtractionQuestionDraft> Questions);
public sealed record ExtractionQuestionRevisionRequest(Guid RequestKey, int ExpectedVersion, string Reason,
    ExtractionQuestionDraft Question);
public sealed record ExtractionQuestionRevisionView(Guid Id, string StudentId, int Version,
    ExtractionQuestionView? Question)
{
    public bool Superseded { get; init; }
}
public sealed record ExtractionRetryRequest(Guid RequestKey);
public sealed record ExtractionSupplementRequest(Guid RequestKey, int ExpectedVersion, Guid ItemId, string Note);
public sealed record ExtractionSupplementUpload(Guid RequestKey, int ExpectedVersion, Guid ItemId, List<ExtractionImage> Images);
public sealed record ExtractionSupplementView(Guid Id, Guid QuestionId, Guid ItemId, string Status, string Note,
    int Version, List<ExtractionSource> Sources);
public sealed record ExtractionProposalRequest(Guid RequestKey, int ExpectedVersion, string Instruction, string Scope,
    List<Guid> QuestionIds, List<Guid> SourceIds, List<ExtractionRegion> Regions, Guid? PreviousProposalId = null);
public sealed record ExtractionProposalView(Guid Id, Guid JobId, Guid RequestKey, int ExpectedVersion, string Status,
    string? Scope, List<Guid> QuestionIds, List<ExtractionQuestionDraft> Questions, string? ErrorCode)
{
    public ExtractionProposalRequest? Request { get; init; }
}
public sealed record ExtractionProposalAdoptionRequest(Guid RequestKey, int ExpectedVersion,
    List<ExtractionQuestionDraft>? Questions = null);
public sealed record ExtractionProposalAdoptionView(Guid Id, Guid JobId, Guid RequestKey, int Version, bool Superseded);
public sealed record ExtractionReviewRequest(int ExpectedVersion, Guid ItemId, int ReviewStatus,
    string ReviewerId, string? RootCause, string? ReturnReason);
public sealed record ExtractionAccess(string Caller, string Actor, bool Administrator = false);
public sealed record ExtractionUpload(string StudentId, int Grade, string SourceKey, List<ExtractionImage> Images);

/// <summary>One original image supplied by the caller; <see cref="Content"/> is stored as-is.</summary>
public sealed class ExtractionImage
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public interface IExtractionService
{
    Task<ExtractionJobView> SubmitAsync(ExtractionAccess access, ExtractionUpload upload, CancellationToken ct = default);
    Task<List<ExtractionJobView>> ListAsync(ExtractionAccess access, string? studentId, int page = 1, CancellationToken ct = default);
    Task<ExtractionJobView> GetAsync(ExtractionAccess access, Guid id, CancellationToken ct = default);
    Task<ExtractionJobView> ReviseAsync(ExtractionAccess access, Guid id, ExtractionRevisionRequest request, CancellationToken ct = default);
    Task<ExtractionQuestionRevisionView> ReviseQuestionAsync(ExtractionAccess access, Guid id, Guid questionId,
        ExtractionQuestionRevisionRequest request, CancellationToken ct = default);
    Task<ExtractionSupplementView> RequestSupplementAsync(ExtractionAccess access, Guid id, Guid questionId,
        ExtractionSupplementRequest request, CancellationToken ct = default);
    Task<ExtractionSupplementView> AppendSupplementAsync(ExtractionAccess access, Guid id, Guid questionId, Guid supplementId,
        ExtractionSupplementUpload request, CancellationToken ct = default);
    Task<ExtractionSupplementView> GetSupplementAsync(ExtractionAccess access, Guid id, Guid questionId, Guid supplementId,
        CancellationToken ct = default);
    Task<ExtractionProposalView> GenerateProposalAsync(ExtractionAccess access, Guid id, ExtractionProposalRequest request, CancellationToken ct = default);
    Task<List<ExtractionProposalView>> ListProposalsAsync(ExtractionAccess access, Guid id, CancellationToken ct = default);
    Task<ExtractionProposalView> GetProposalAsync(ExtractionAccess access, Guid id, Guid proposalId, CancellationToken ct = default);
    Task<ExtractionProposalView> DiscardProposalAsync(ExtractionAccess access, Guid id, Guid proposalId, CancellationToken ct = default);
    Task<ExtractionProposalAdoptionView> AdoptProposalAsync(ExtractionAccess access, Guid id, Guid proposalId,
        ExtractionProposalAdoptionRequest request, CancellationToken ct = default);
    Task<ExtractionJobView> RetryAsync(ExtractionAccess access, Guid id, ExtractionRetryRequest request, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)> ReadSourceAsync(ExtractionAccess access, Guid id, Guid sourceId, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)> ReadCropAsync(ExtractionAccess access, Guid id, Guid questionId, int version, int index, CancellationToken ct = default);
    /// <summary>Size is <c>full</c> (default), <c>medium</c>, <c>small</c> or <c>thumbnail</c>; variants are JPEG.</summary>
    Task<(byte[] Bytes, string ContentType)> ReadSourceAsync(ExtractionAccess access, Guid id, Guid sourceId, string? size, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)> ReadCropAsync(ExtractionAccess access, Guid id, Guid questionId, int version, int index, string? size, CancellationToken ct = default);
    Task<ExtractionSnapshot> GetVersionAsync(ExtractionAccess access, Guid id, int version, CancellationToken ct = default);
}

public sealed class ExtractionException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record QuestionAnalysisImage(Guid SourceId, string DataUrl, int Width, int Height,
    List<ExtractionRegion> Regions);
public sealed record QuestionAnalysisRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Grade,
    List<QuestionAnalysisImage> Images)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuestionAnalysisRevision? Revision { get; init; }
}
public sealed record QuestionAnalysisRevision(string Instruction, string Scope, List<ExtractionQuestionDraft> Questions);
public sealed record QuestionAnalysisResult(string Model, List<ExtractionQuestionDraft> Questions);
public interface IQuestionAnalysisClient
{
    Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default);
}
