using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Emenda.Common.Oss;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Host.Services.VlImageAnalysis;
using Emenda.Service.Services;
using Emenda.Service.StudentIntegration;

namespace Emenda.Host.Services.Intake;

/// <summary>Outcome of one legacy upload record processing pass.</summary>
public enum LegacyUploadProcessingStatus
{
    /// <summary>The record was processed (possibly with per-group failures already logged).</summary>
    Completed = 0,

    /// <summary>The record has no images of type "mistake"; nothing was processed.</summary>
    NoMistakeImages = 1,

    /// <summary>Every image was already processed by an earlier pass; only removal was reported.</summary>
    AllImagesAlreadyProcessed = 2,

    /// <summary>VL analysis failed, so nothing was created and the record stays pending.</summary>
    VlFailed = 3,
}

/// <summary>
/// Reusable legacy (Student upload) intake pipeline extracted from
/// <see cref="UploadPollingWorker"/>: it filters "mistake" images, applies the
/// idempotency check over existing items, runs VL analysis (with the skipped
/// single-group fallback), creates MistakeItems, migrates the OSS objects from
/// uploads/ to mistakes/ and rewrites the item SourceRegions.
///
/// Removal of the original source images is caller-owned: the polling worker
/// passes a callback that calls the Student removal API, while the
/// product-owned intake passes null because no Student record exists. The class
/// is scoped because it depends on the scoped domain service.
/// </summary>
public sealed class LegacyUploadProcessor
{
    private const string MistakeImageType = "mistake";
    private const int DefaultSubject = 1; // Chinese
    private const int DefaultGrade = 7;   // Middle school year 1
    private const string SkippedRootCause = "Mistake upload (VL skipped)";

    private readonly IMistakeDomainService _mistakeService;
    private readonly IOssService _ossService;
    private readonly IVlAnalyzer _analyzer;
    private readonly VlAnalyzerOptions _vlOptions;
    private readonly ILogger<LegacyUploadProcessor> _logger;

    public LegacyUploadProcessor(
        IMistakeDomainService mistakeService,
        IOssService ossService,
        IVlAnalyzer analyzer,
        IOptions<VlAnalyzerOptions> vlOptions,
        ILogger<LegacyUploadProcessor> logger)
    {
        _mistakeService = mistakeService;
        _ossService = ossService;
        _analyzer = analyzer;
        _vlOptions = vlOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Processes one Processing upload record in the legacy (unmanaged) mode.
    /// </summary>
    /// <param name="notifySource">
    /// Optional callback invoked with the record id and the image paths that no
    /// longer need to be retained by the source (all processed images). The
    /// callback is tried only when there is at least one path; the polling
    /// worker uses it to call the Student removal API.
    /// </param>
    public async Task<LegacyUploadProcessingStatus> ProcessAsync(
        ProcessingUploadRecord record,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? notifySource = null,
        CancellationToken ct = default)
    {
        var recordId = record.Id;
        _logger.LogInformation(
            "LegacyUploadProcessor: Processing record {RecordId}, student={StudentId}, imageCount={ImageCount}",
            recordId, record.StudentId, record.ImageEntries.Count);

        // Step 1: Filter images with type == "mistake"
        var mistakeEntries = record.ImageEntries
            .Where(e => string.Equals(e.Type, MistakeImageType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mistakeEntries.Count == 0)
        {
            _logger.LogInformation(
                "LegacyUploadProcessor: Record {RecordId} has no 'mistake' type images, skipping", recordId);
            return LegacyUploadProcessingStatus.NoMistakeImages;
        }

        var mistakeImagePaths = mistakeEntries.Select(e => e.Path).ToList();
        if (mistakeImagePaths.Any(p =>
                ReservedSourceTargetPath.IsReserved(p) ||
                ReservedSourceTargetPath.IsReserved(ConvertUploadPathToMistakePath(p))))
        {
            throw new SourceIntakeException("reserved_target", 409);
        }

        _logger.LogDebug("LegacyUploadProcessor: Record {RecordId} has {Count} mistake images: {Paths}",
            recordId, mistakeImagePaths.Count, string.Join(", ", mistakeImagePaths));

        // Step 2: Idempotency check — find already-processed images
        var existingItems = await _mistakeService.GetMistakeItemsByUploadAsync(recordId).ConfigureAwait(false);
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existingItems)
        {
            foreach (var region in item.SourceRegions)
            {
                if (!string.IsNullOrWhiteSpace(region.SourceImagePath))
                {
                    // SourceImagePath may have been updated to mistakes/ prefix after OSS migration;
                    // normalize back to uploads/ for comparison with the original record paths
                    var path = region.SourceImagePath;
                    if (path.StartsWith("mistakes/", StringComparison.OrdinalIgnoreCase))
                    {
                        path = "uploads/" + path.Substring("mistakes/".Length);
                    }
                    processedPaths.Add(path);
                }
            }
        }

        var unprocessedPaths = mistakeImagePaths
            .Where(p => !processedPaths.Contains(p))
            .ToList();

        _logger.LogInformation(
            "LegacyUploadProcessor: Record {RecordId} idempotency check: {Total} images, {Processed} already processed, {Unprocessed} to process",
            recordId, mistakeImagePaths.Count, processedPaths.Count, unprocessedPaths.Count);

        // Step 3: If all images already processed, skip VL/OSS and just notify the source
        if (unprocessedPaths.Count == 0)
        {
            _logger.LogInformation(
                "LegacyUploadProcessor: Record {RecordId} all images already processed, notifying source to delete",
                recordId);

            var allPathsToDelete = processedPaths.ToList();
            if (allPathsToDelete.Count > 0)
            {
                await NotifySourceAsync(notifySource, recordId, allPathsToDelete, ct).ConfigureAwait(false);
            }

            return LegacyUploadProcessingStatus.AllImagesAlreadyProcessed;
        }

        // Step 4: VL analysis (only unprocessed images)
        VlAnalysisResult analysisResult;
        if (_vlOptions.IsConfigured)
        {
            try
            {
                analysisResult = await _analyzer.AnalyzeAsync(record.StudentId, unprocessedPaths, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LegacyUploadProcessor: VL analysis failed for record {RecordId}", recordId);
                return LegacyUploadProcessingStatus.VlFailed;
            }
        }
        else
        {
            _logger.LogInformation(
                "LegacyUploadProcessor: VL not configured, treating all images as single group for record {RecordId}",
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
            _logger.LogInformation("LegacyUploadProcessor: VL analysis returned {GroupCount} groups for record {RecordId}",
                groups.Count, recordId);
        }
        else
        {
            _logger.LogInformation(
                "LegacyUploadProcessor: VL analysis returned no groups or was skipped, " +
                "treating all {Count} images as single group for record {RecordId}",
                unprocessedPaths.Count, recordId);
            groups = new List<VlImageGroup>
            {
                new()
                {
                    ImageIndices = Enumerable.Range(0, unprocessedPaths.Count).ToList(),
                    Subject = DefaultSubject,
                    Grade = DefaultGrade,
                    RootCause = SkippedRootCause
                }
            };
        }

        // Step 5: For each group, create MistakeItem and migrate OSS images
        var allProcessedPaths = new List<string>(processedPaths); // Include previously processed paths

        foreach (var group in groups)
        {
            if (group.ImageIndices == null || group.ImageIndices.Count == 0)
            {
                _logger.LogWarning("LegacyUploadProcessor: Group in record {RecordId} has no image indices, skipping",
                    recordId);
                continue;
            }

            var selectedPaths = group.ImageIndices
                .Where(idx => idx >= 0 && idx < unprocessedPaths.Count)
                .Distinct()
                .Select(idx => unprocessedPaths[idx])
                .ToList();

            if (selectedPaths.Count == 0)
            {
                _logger.LogWarning("LegacyUploadProcessor: Group in record {RecordId} selected no valid paths, skipping",
                    recordId);
                continue;
            }

            var rootCause = !string.IsNullOrWhiteSpace(group.RootCause)
                ? group.RootCause
                : string.Empty;

            try
            {
                // Step 5a: Create MistakeItem (images still at uploads/ paths)
                var items = await _mistakeService.SubmitMistakeUploadAsync(
                    record.StudentId,
                    group.Subject,
                    group.Grade,
                    selectedPaths,
                    rootCause,
                    recordId).ConfigureAwait(false);

                _logger.LogInformation(
                    "LegacyUploadProcessor: Created {ItemCount} MistakeItem(s) for group in record {RecordId}",
                    items.Count, recordId);

                // Step 5b: Migrate OSS images from uploads/ to mistakes/
                var migratedPaths = new List<(string OldPath, string NewPath)>();
                foreach (var oldPath in selectedPaths)
                {
                    try
                    {
                        var newPath = ConvertUploadPathToMistakePath(oldPath);
                        ReservedSourceTargetPath.EnsureLegacy(oldPath);
                        ReservedSourceTargetPath.EnsureLegacy(newPath);
                        await _ossService.CopyObjectAsync(oldPath, newPath).ConfigureAwait(false);
                        await _ossService.DeleteAsync(oldPath).ConfigureAwait(false);
                        migratedPaths.Add((oldPath, newPath));
                        allProcessedPaths.Add(oldPath);

                        _logger.LogDebug("LegacyUploadProcessor: Migrated {OldPath} -> {NewPath}", oldPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "LegacyUploadProcessor: Failed to migrate image {Path} for record {RecordId}",
                            oldPath, recordId);
                    }
                }

                // Step 5c: Update MistakeItem SourceRegions with new paths
                if (migratedPaths.Count > 0)
                {
                    var pathMap = migratedPaths.ToDictionary(
                        p => p.OldPath, p => p.NewPath, StringComparer.OrdinalIgnoreCase);
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
                            await _mistakeService.UpdateMistakeItemAsync(item).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LegacyUploadProcessor: Failed to process group in record {RecordId}",
                    recordId);
            }
        }

        // Step 6: Notify the source to remove processed images (both old and new paths)
        if (allProcessedPaths.Count > 0)
        {
            await NotifySourceAsync(notifySource, recordId, allProcessedPaths, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("LegacyUploadProcessor: Completed processing record {RecordId}", recordId);
        return LegacyUploadProcessingStatus.Completed;
    }

    private static Task NotifySourceAsync(
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? notifySource,
        string recordId,
        IReadOnlyList<string> imagePaths,
        CancellationToken ct) =>
        notifySource is null || imagePaths.Count == 0
            ? Task.CompletedTask
            : notifySource(recordId, imagePaths, ct);

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