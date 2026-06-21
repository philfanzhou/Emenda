using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Host.Services;

/// <summary>
/// Background service that polls Student service for Processing upload records
/// and processes them: VL analysis, MistakeItem creation, OSS migration.
/// Replaces the circular Mistake->Student->Mistake call chain with unidirectional polling.
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
        var studentClient = scope.ServiceProvider.GetRequiredService<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient>();

        var page = 1;
        var hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            StudentProto.UploadRecordsPageResult pageResult;
            try
            {
                pageResult = await studentClient.GetProcessingUploadRecordsAsync(
                    new StudentProto.GetProcessingUploadRecordsRequest
                    {
                        Page = page,
                        PageSize = PageSize
                    }, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadPollingWorker: Failed to fetch Processing records (page {Page})", page);
                return;
            }

            var records = pageResult.Items;
            if (records.Count == 0)
            {
                _logger.LogDebug("UploadPollingWorker: No Processing records found");
                return;
            }

            _logger.LogInformation("UploadPollingWorker: Found {Count} Processing records (page {Page}, total {Total})",
                records.Count, page, pageResult.TotalCount);

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

            hasMore = page * PageSize < pageResult.TotalCount;
            page++;
        }
    }

    private async Task ProcessRecordAsync(
        StudentProto.UploadRecordDto record,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var recordId = record.Id;
        _logger.LogInformation("UploadPollingWorker: Processing record {RecordId}, student={StudentId}, imageCount={ImageCount}",
            recordId, record.StudentId, record.ImageEntries.Count);

        // Step 1: Filter images with type == "mistake"
        var mistakeEntries = record.ImageEntries
            .Where(e => string.Equals(e.Type, "mistake", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mistakeEntries.Count == 0)
        {
            _logger.LogInformation("UploadPollingWorker: Record {RecordId} has no 'mistake' type images, skipping",
                recordId);
            return;
        }

        var mistakeImagePaths = mistakeEntries.Select(e => e.Path).ToList();
        _logger.LogDebug("UploadPollingWorker: Record {RecordId} has {Count} mistake images: {Paths}",
            recordId, mistakeImagePaths.Count, string.Join(", ", mistakeImagePaths));

        // Step 2: VL analysis
        var analyzer = scopedServices.GetRequiredService<IVlAnalyzer>();
        VlAnalysisResult analysisResult;

        if (_vlOptions.Value.IsConfigured)
        {
            try
            {
                analysisResult = await analyzer.AnalyzeAsync(record.StudentId, mistakeImagePaths, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadPollingWorker: VL analysis failed for record {RecordId}", recordId);
                return;
            }
        }
        else
        {
            _logger.LogInformation("UploadPollingWorker: VL not configured, treating all images as single group for record {RecordId}",
                recordId);
            analysisResult = new VlAnalysisResult
            {
                Skipped = true
            };
        }

        // Build groups: if VL skipped or returned no groups, treat all images as a single group
        List<VlImageGroup> groups;
        if (analysisResult.HasGroups)
        {
            groups = analysisResult.Groups;
            _logger.LogInformation("UploadPollingWorker: VL analysis returned {GroupCount} groups for record {RecordId}",
                groups.Count, recordId);
        }
        else
        {
            _logger.LogInformation("UploadPollingWorker: VL analysis returned no groups or was skipped, " +
                "treating all {Count} images as single group for record {RecordId}",
                mistakeImagePaths.Count, recordId);
            groups = new List<VlImageGroup>
            {
                new VlImageGroup
                {
                    ImageIndices = Enumerable.Range(0, mistakeImagePaths.Count).ToList(),
                    Subject = 1,  // Default: Chinese
                    Grade = 7,    // Default: Middle school year 1
                    RootCause = "Mistake upload (VL skipped)"
                }
            };
        }

        // Step 3: For each group, create MistakeItem and migrate OSS images
        var mistakeService = scopedServices.GetRequiredService<IMistakeDomainService>();
        var ossService = scopedServices.GetRequiredService<IOssService>();
        var allProcessedPaths = new List<string>();

        foreach (var group in groups)
        {
            if (group.ImageIndices == null || group.ImageIndices.Count == 0)
            {
                _logger.LogWarning("UploadPollingWorker: Group in record {RecordId} has no image indices, skipping",
                    recordId);
                continue;
            }

            var selectedPaths = group.ImageIndices
                .Where(idx => idx >= 0 && idx < mistakeImagePaths.Count)
                .Distinct()
                .Select(idx => mistakeImagePaths[idx])
                .ToList();

            if (selectedPaths.Count == 0)
            {
                _logger.LogWarning("UploadPollingWorker: Group in record {RecordId} selected no valid paths, skipping",
                    recordId);
                continue;
            }

            var rootCause = !string.IsNullOrWhiteSpace(group.RootCause)
                ? group.RootCause
                : string.Empty;

            try
            {
                // Step 3a: Create MistakeItem (images still at uploads/ paths)
                var items = await mistakeService.SubmitMistakeUploadAsync(
                    record.StudentId,
                    group.Subject,
                    group.Grade,
                    selectedPaths,
                    rootCause,
                    recordId).ConfigureAwait(false);

                _logger.LogInformation("UploadPollingWorker: Created {ItemCount} MistakeItem(s) for group in record {RecordId}",
                    items.Count, recordId);

                // Step 3b: Migrate OSS images from uploads/ to mistakes/
                var migratedPaths = new List<(string OldPath, string NewPath)>();
                foreach (var oldPath in selectedPaths)
                {
                    try
                    {
                        var newPath = ConvertUploadPathToMistakePath(oldPath);
                        await ossService.CopyObjectAsync(oldPath, newPath).ConfigureAwait(false);
                        await ossService.DeleteAsync(oldPath).ConfigureAwait(false);
                        migratedPaths.Add((oldPath, newPath));
                        allProcessedPaths.Add(oldPath);

                        _logger.LogDebug("UploadPollingWorker: Migrated {OldPath} -> {NewPath}",
                            oldPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "UploadPollingWorker: Failed to migrate image {Path} for record {RecordId}",
                            oldPath, recordId);
                    }
                }

                // Step 3c: Update MistakeItem SourceRegions with new paths
                if (migratedPaths.Count > 0)
                {
                    var pathMap = migratedPaths.ToDictionary(p => p.OldPath, p => p.NewPath, StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        var updated = false;
                        foreach (var region in item.SourceRegions)
                        {
                            if (!string.IsNullOrWhiteSpace(region.SourceImagePath) &&
                                pathMap.TryGetValue(region.SourceImagePath, out var newPath))
                            {
                                region.SourceImagePath = newPath;
                                updated = true;
                            }
                        }

                        if (updated)
                        {
                            await mistakeService.UpdateMistakeItemAsync(item).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadPollingWorker: Failed to process group in record {RecordId}",
                    recordId);
            }
        }

        // Step 4: Notify Student service to remove processed images
        if (allProcessedPaths.Count > 0)
        {
            try
            {
                var studentClient = scopedServices.GetRequiredService<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
                var removeRequest = new StudentProto.RemoveProcessedImagesRequest
                {
                    RecordId = recordId
                };
                removeRequest.ImagePaths.AddRange(allProcessedPaths);
                var removeResponse = await studentClient.RemoveProcessedImagesAsync(
                    removeRequest, cancellationToken: ct).ConfigureAwait(false);

                if (removeResponse.Success)
                {
                    _logger.LogInformation(
                        "UploadPollingWorker: Removed processed images from Student record {RecordId}. " +
                        "RecordDeleted={Deleted}, Remaining={Remaining}",
                        recordId, removeResponse.RecordDeleted, removeResponse.RemainingImageCount);
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

        _logger.LogInformation("UploadPollingWorker: Completed processing record {RecordId}", recordId);
    }

    /// <summary>
    /// Converts an uploads/ path to a mistakes/ path by replacing the prefix.
    /// Example: uploads/student123/abc.jpg -> mistakes/student123/abc.jpg
    /// </summary>
    private static string ConvertUploadPathToMistakePath(string uploadPath)
    {
        if (uploadPath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return "mistakes/" + uploadPath.Substring("uploads/".Length);
        }
        return "mistakes/" + uploadPath;
    }
}
