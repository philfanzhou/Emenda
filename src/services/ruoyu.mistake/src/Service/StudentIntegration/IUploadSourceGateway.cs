namespace Ruoyu.Study.Mistake.Service.StudentIntegration;

public interface IUploadSourceGateway
{
    Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default);
    Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default);
    Task CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, CancellationToken cancellationToken = default);
}

public sealed class UploadImageEntry
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = "mistake";
}

public sealed class ProcessingUploadRecord
{
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
}
