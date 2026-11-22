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
    List<ExtractionCrop> Images);
public sealed record ExtractionSnapshot(List<ExtractionQuestionView> Questions);
public sealed record ExtractionVersionView(int Version, Guid RequestKey, string Actor, string Reason,
    string Model, DateTimeOffset CreatedAt);
public sealed record ExtractionJobView(Guid Id, string StudentId, int Grade, string Status, int Version,
    int Attempt, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    List<ExtractionSource> Sources, List<ExtractionQuestionView> Questions, List<ExtractionVersionView> Versions);
public sealed record ExtractionRevisionRequest(Guid RequestKey, int ExpectedVersion, string Reason,
    List<ExtractionQuestionDraft> Questions);
public sealed record ExtractionRetryRequest(Guid RequestKey);
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
    Task<ExtractionJobView> RetryAsync(ExtractionAccess access, Guid id, ExtractionRetryRequest request, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)> ReadSourceAsync(ExtractionAccess access, Guid id, Guid sourceId, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)> ReadCropAsync(ExtractionAccess access, Guid id, Guid questionId, int version, int index, CancellationToken ct = default);
    Task<ExtractionSnapshot> GetVersionAsync(ExtractionAccess access, Guid id, int version, CancellationToken ct = default);
}

public sealed class ExtractionException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record QuestionAnalysisImage(Guid SourceId, string DataUrl, int Width, int Height,
    List<ExtractionRegion> Regions);
public sealed record QuestionAnalysisRequest(int Grade, List<QuestionAnalysisImage> Images);
public sealed record QuestionAnalysisResult(string Model, List<ExtractionQuestionDraft> Questions);
public interface IQuestionAnalysisClient
{
    Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default);
}
