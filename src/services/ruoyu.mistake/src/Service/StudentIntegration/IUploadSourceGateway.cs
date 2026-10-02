namespace Ruoyu.Study.Mistake.Service.StudentIntegration;

public interface IUploadSourceGateway
{
    Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default);
    Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default);
    Task<CreateReturnedUploadRecordResult> CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only ownership resolution for per-resource authorization
    /// (IKJEPZ): resolves the students an identity account is allowed to act
    /// for. Resolved per request, never cached. Fails closed: when the
    /// resolution is unavailable (Student capability disabled, transport
    /// failure or timeout) the result reports <see cref="AllowedStudentsResolution.Available"/>
    /// = false so the caller denies student-scope access with an explicit
    /// unavailable response instead of treating the failure as an empty
    /// mapping. An available result with an empty list means the account maps
    /// to no student (denied as a normal 403).
    /// </summary>
    Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(string identityAccountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of <see cref="IUploadSourceGateway.GetAllowedStudentIdsAsync"/>:
/// either an available student-id list (possibly empty = no mapping) or an
/// explicit "unavailable" fail-closed marker.
/// </summary>
public sealed class AllowedStudentsResolution
{
    public bool Available { get; set; }
    public List<string> StudentIds { get; set; } = new();

    public static AllowedStudentsResolution Unavailable { get; } = new() { Available = false };

    public static AllowedStudentsResolution FromIds(List<string> studentIds) => new()
    {
        Available = true,
        StudentIds = studentIds
    };
}

public sealed class UploadImageEntry
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = "mistake";
}

public sealed class ProcessingUploadRecord
{
    public Guid ContentRevision { get; set; }
    public string Id { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public List<UploadImageEntry> ImageEntries { get; set; } = new();
}

public sealed class ProcessingUploadsPage
{
    public List<ProcessingUploadRecord> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public string? NextCursor { get; set; }
}

public sealed class RemoveProcessedUploadImagesResult
{
    public bool Success { get; set; }
    public bool RecordDeleted { get; set; }
    public int RemainingImageCount { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Student reported the record as unknown (IKJEY6): it does not exist and
    /// no removal tombstone proves a prior completion. The caller must
    /// investigate the source record instead of retrying the removal.
    /// </summary>
    public bool UnknownRecord { get; set; }
}

/// <summary>
/// Outcome classification of the returned-record creation callback
/// (IKJEY4): Succeeded (created or replayed), PayloadConflict (the
/// operationId was already used with a different payload) or Unknown
/// (transport failure or unreadable response — read back via the
/// operationId before retrying).
/// </summary>
public enum CreateReturnedUploadRecordStatus
{
    Succeeded = 0,
    PayloadConflict = 1,
    Unknown = 2
}

public sealed class CreateReturnedUploadRecordResult
{
    public CreateReturnedUploadRecordStatus Status { get; set; } = CreateReturnedUploadRecordStatus.Unknown;
    public bool Replayed { get; set; }
    public string Message { get; set; } = string.Empty;
}
