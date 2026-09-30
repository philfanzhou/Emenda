using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Ruoyu.Study.MistakeBff.HttpClients;

namespace Ruoyu.Study.Mistake.Host.Services;

public sealed class StudentUploadSourceGateway : IUploadSourceGateway
{
    private readonly IStudentHttpClient _client;
    private readonly StudentCapabilityOptions _studentCapability;

    public StudentUploadSourceGateway(IStudentHttpClient client, StudentCapabilityOptions studentCapability)
    {
        _client = client;
        _studentCapability = studentCapability;
    }

    public async Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        var source = await _client.ScanProcessingUploadRecordsAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false);
        return new ProcessingUploadsPage
        {
            Items = source.Items!.Select(record => new ProcessingUploadRecord
            {
                Id = record.Id,
                StudentId = record.StudentId,
                ImageEntries = record.ImageEntries.Select(entry => new UploadImageEntry
                {
                    Path = entry.Path,
                    Type = entry.Type
                }).ToList()
            }).ToList(),
            HasMore = source.HasMore,
            NextCursor = source.NextCursor
        };
    }

    public async Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default)
    {
        var source = await _client.RemoveProcessedImagesAsync(recordId, imagePaths.ToList(), cancellationToken).ConfigureAwait(false);
        return new RemoveProcessedUploadImagesResult
        {
            Success = source.Success,
            RecordDeleted = source.RecordDeleted,
            RemainingImageCount = source.RemainingImageCount,
            ErrorMessage = source.ErrorMessage
        };
    }

    public Task CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, CancellationToken cancellationToken = default)
    {
        var sourceEntries = imageEntries.Select(entry => new ImageEntryDto
        {
            Path = entry.Path,
            Type = entry.Type
        }).ToList();
        return _client.CreateReturnedRecordAsync(studentId, sourceEntries, comments, returnReason, cancellationToken);
    }

    public async Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(string identityAccountId, CancellationToken cancellationToken = default)
    {
        // Fail closed before any network call when the Student capability is
        // disabled: no learner-scope request may fall back to an empty or
        // anonymous allowance.
        if (!_studentCapability.Enabled)
            return AllowedStudentsResolution.Unavailable;

        try
        {
            // Strict variant: a failed call must surface as unavailable, not
            // as an empty mapping.
            var result = await _client.GetStudentsByIdentityAccountIdStrictAsync(identityAccountId, cancellationToken)
                .ConfigureAwait(false);
            var studentIds = result.Students
                .Select(student => student.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return AllowedStudentsResolution.FromIds(studentIds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Transport failure or timeout: the resolution is unavailable,
            // never an implicit allow or deny-by-empty-mapping.
            return AllowedStudentsResolution.Unavailable;
        }
    }
}
