using System.Text.RegularExpressions;

namespace Emenda.Service.Intake;

/// <summary>Product-owned, caller-scoped upload intake with durable request replay.</summary>
public interface IProductIntakeService
{
    Task<ProductIntakeResult> CreateAsync(ProductIntakeRequest request, CancellationToken ct = default);
}

/// <summary>Stable intake error codes returned to API callers.</summary>
public static class ProductIntakeErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string ProcessingFailed = "processing_failed";
    public const string VlFailed = "vl_failed";
    public const string Conflict = "source_conflict";
    public const string InProgress = "source_in_progress";
}

/// <summary>Stable intake error messages (no internal exception details).</summary>
public static class ProductIntakeMessages
{
    public const string StudentIdRequired = "studentId is required and must be at most 36 characters long.";
    public const string SourceKeyInvalid = "sourceKey must match ^[A-Za-z0-9._-]{1,64}$ when provided.";
    public const string ImagesRequired = "At least one image is required.";
    public const string TooManyImages = "At most 20 images are allowed per request.";
    public const string ImageSizeInvalid = "Each image must be between 1 byte and 15 MiB.";
    public const string ContentTypeInvalid = "Image content type must be image/jpeg or image/png.";
    public const string MultipartRequired = "A multipart/form-data request is required.";
    public const string MultipartInvalid = "The multipart request body is invalid or too large.";
    public const string VlFailed = "Image analysis failed; the upload was not processed.";
    public const string ProcessingFailed = "The upload could not be processed.";
    public const string Conflict = "This sourceKey is already bound to a different student or image payload.";
    public const string InProgress = "This sourceKey is being processed; retry the same request after it finishes.";
}

/// <summary>Documented product intake limits shared by validation and the HTTP surface.</summary>
public static class ProductIntakeLimits
{
    public const int MaxImages = 20;
    public const int MaxStudentIdLength = 36;
    public const long MaxImageBytes = 15L * 1024 * 1024;

    /// <summary>Multipart envelope allowance (boundaries, headers, form fields).</summary>
    public const long MultipartOverheadBytes = 1024 * 1024;

    /// <summary>Maximum accepted multipart body for one intake request.</summary>
    public const long MaxRequestBytes = MaxImages * MaxImageBytes + MultipartOverheadBytes;

    private static readonly Regex SourceKeyRegex =
        new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    public static bool IsValidSourceKey(string? value) => value is null || SourceKeyRegex.IsMatch(value);
}

/// <summary>One product intake request: a student's upload of 1..20 images.</summary>
public class ProductIntakeRequest
{
    public string CallerName { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string? SourceKey { get; set; }
    public List<ProductIntakeImage> Images { get; set; } = new();
}

/// <summary>One image supplied by the caller; <see cref="Content"/> is copied to OSS as-is.</summary>
public class ProductIntakeImage
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>Result of a product intake request.</summary>
public class ProductIntakeResult
{
    public bool Success { get; set; }
    public string? SourceId { get; set; }
    public List<ProductIntakeItem> Items { get; set; } = new();
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static ProductIntakeResult Completed(string sourceId, List<ProductIntakeItem> items) => new()
    {
        Success = true,
        SourceId = sourceId,
        Items = items
    };

    public static ProductIntakeResult Failed(string errorCode, string errorMessage) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

/// <summary>One processed mistake item created (or previously created) by the intake.</summary>
public class ProductIntakeItem
{
    public Guid Id { get; set; }
    public int Subject { get; set; }
    public int Grade { get; set; }
    public int ReviewStatus { get; set; }
    public List<string> ImagePaths { get; set; } = new();
}