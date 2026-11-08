using Microsoft.Extensions.Options;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.Intake;
using Ruoyu.Study.Mistake.Service.StudentIntegration;

namespace Ruoyu.Study.Mistake.Host.Services;

/// <summary>
/// Background service that polls Student service for Processing upload records
/// and processes them: VL analysis, MistakeItem creation, OSS migration.
/// Replaces the circular Mistake->Student->Mistake call chain with unidirectional polling.
/// Legacy record processing is delegated to <see cref="LegacyUploadProcessor"/>; this
/// worker owns the polling loop, the managed-source branch and its guard.
/// </summary>
public class UploadPollingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<VlAnalyzerOptions> _vlOptions;
    private readonly ILogger<UploadPollingWorker> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int PageSize = 50;

    public UploadPollingWorker(
        IServiceProvider serviceProvider,
        IOptions<VlAnalyzerOptions> vlOptions,
        ILogger<UploadPollingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _vlOptions = vlOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UploadPollingWorker started. VL configured={IsConfigured}, poll interval={Interval}s",
            _vlOptions.Value.IsConfigured, PollInterval.TotalSeconds);

        // Initial delay to let the service fully start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndProcessAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadPollingWorker: Unexpected error during poll cycle");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("UploadPollingWorker stopped");
    }

    private async Task PollAndProcessAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var studentClient = scope.ServiceProvider.GetRequiredService<IUploadSourceGateway>();

        string? cursor = null;
        var hasMore = true;

        while (hasMore)
        {
            ct.ThrowIfCancellationRequested();
            ProcessingUploadsPage pageResult;
            try
            {
                pageResult = await studentClient.GetProcessingUploadsAsync(
                    cursor, PageSize, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadPollingWorker: Failed to fetch Processing records");
                return;
            }

            var records = pageResult.Items;
            if (records.Count == 0)
            {
                _logger.LogDebug("UploadPollingWorker: No Processing records found");
                return;
            }

            _logger.LogInformation("UploadPollingWorker: Found {Count} Processing records", records.Count);

            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await ProcessRecordAsync(record, scope.ServiceProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UploadPollingWorker: Failed to process record {RecordId}, skipping",
                        record.Id);
                }
            }

            hasMore = pageResult.HasMore;
            cursor = pageResult.NextCursor;
        }
    }

    private async Task ProcessRecordAsync(
        ProcessingUploadRecord record,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var intake = scopedServices.GetService<ISourceIntakeService>();
        if (intake?.Enabled == true)
        {
            if (!Guid.TryParse(record.Id, out var source) || source == Guid.Empty || record.ContentRevision == Guid.Empty)
            {
                _logger.LogWarning("Source intake skipped invalid scan condition");
                return;
            }
            try { await intake.CreateAutomaticAsync(source, record.ContentRevision, ct); }
            catch (SourceIntakeException ex) { _logger.LogWarning("Source intake stopped: {Reason}", ex.ErrorKind); }
            return;
        }
        // Managed sources never fall back to destructive legacy processing after disable/restart.
        var guard = scopedServices.GetService<IMistakeSourceGuard>();
        if (guard != null) await guard.EnsureLegacySourceAsync(record.Id, ct);

        // Legacy pipeline extraction: the worker no longer owns the processing steps.
        var processor = scopedServices.GetRequiredService<LegacyUploadProcessor>();
        await processor.ProcessAsync(
            record,
            (recordId, imagePaths, token) =>
                NotifyStudentToRemoveImagesAsync(scopedServices, recordId, imagePaths.ToList(), token),
            ct).ConfigureAwait(false);
    }

    private async Task NotifyStudentToRemoveImagesAsync(
        IServiceProvider scopedServices,
        string recordId,
        List<string> imagePaths,
        CancellationToken ct)
    {
        try
        {
            var studentClient = scopedServices.GetRequiredService<IUploadSourceGateway>();
            var removeResponse = await studentClient.RemoveProcessedImagesAsync(
                recordId, imagePaths, ct).ConfigureAwait(false);

            if (removeResponse.Success)
            {
                // RecordDeleted=true covers both a fresh full removal and an
                // idempotent replay after a lost response (Student answers
                // replays from the removal tombstone): both are terminal
                // confirmations, not new removal actions.
                _logger.LogInformation(
                    "UploadPollingWorker: Removed processed images from Student record {RecordId}. " +
                    "RecordDeleted={Deleted}, Remaining={Remaining}",
                    recordId, removeResponse.RecordDeleted, removeResponse.RemainingImageCount);
            }
            else if (removeResponse.UnknownRecord)
            {
                // Student reports the record as unknown: no record and no
                // removal tombstone proves a prior completion. This is a data
                // inconsistency between the migrated objects and the Student
                // record that no retry can resolve — manual investigation of
                // the source record is required (reconciliation belongs to
                // IKJ0IO, not this worker).
                _logger.LogWarning(
                    "UploadPollingWorker: Student reported unknown record {RecordId} for processed-image removal " +
                    "(no record and no removal tombstone); manual investigation of the source record is required",
                    recordId);
            }
            else
            {
                _logger.LogWarning(
                    "UploadPollingWorker: Failed to remove processed images from Student record {RecordId}: {Error}",
                    recordId, removeResponse.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UploadPollingWorker: Failed to call RemoveProcessedImages for record {RecordId}",
                recordId);
        }
    }
}