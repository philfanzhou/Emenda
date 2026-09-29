using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Ruoyu.Study.MistakeBff.HttpClients;

namespace Ruoyu.Study.Mistake.Host.Services;

public sealed class StudentUploadSourceGateway : IUploadSourceGateway
{
    private readonly IStudentHttpClient _client;

    public StudentUploadSourceGateway(IStudentHttpClient client) => _client = client;

    public async Task<ProcessingUploadsPage> GetProcessingUploadsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var source = await _client.GetProcessingUploadRecordsAsync(page, pageSize, cancellationToken).ConfigureAwait(false);
        return new ProcessingUploadsPage
        {
            Items = source.Items.Select(record => new ProcessingUploadRecord
            {
                Id = record.Id,
                StudentId = record.StudentId,
                ImageEntries = record.ImageEntries.Select(entry => new UploadImageEntry
                {
                    Path = entry.Path,
                    Type = entry.Type
                }).ToList()
            }).ToList(),
            TotalCount = source.TotalCount,
            Page = source.Page,
            PageSize = source.PageSize
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
}
