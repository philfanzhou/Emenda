// TEMPORARY: One-time fingerprint backfill — safe to delete after deployment verification
//
// Rebuilds image fingerprints for historical confirmed-mistake images whose fingerprints
// were cascade-deleted before the image_fingerprint.upload_record_id FK was changed to
// ON DELETE SET NULL. After this one-time backfill runs on deployment, dedup coverage
// extends to all confirmed mistake images stored under mistakes/.
//
// Removal instructions: delete this file and remove the AddHostedService<FingerprintBackfillWorker>()
// line in Program.cs after verifying the backfill log shows completion.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Database;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Host.Services;

/// <summary>
/// TEMPORARY: One-time fingerprint backfill worker.
/// Runs once at startup to rebuild fingerprints for confirmed mistake images.
/// </summary>
public class FingerprintBackfillWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private const int BatchSize = 50;
    private const int ConfirmedReviewStatus = (int)ReviewStatus.Confirmed;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FingerprintBackfillWorker> _logger;

    public FingerprintBackfillWorker(
        IServiceProvider serviceProvider,
        ILogger<FingerprintBackfillWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FingerprintBackfillWorker: TEMPORARY one-time backfill, starting in {Delay}s", StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            await BackfillAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Fingerprint backfill completed");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            // Backfill failure must NOT crash the mistake service.
            _logger.LogError(ex, "FingerprintBackfillWorker: one-time backfill failed, service will continue normally");
        }
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        var ossService = scope.ServiceProvider.GetRequiredService<IOssService>();
        var studentClient = scope.ServiceProvider
            .GetRequiredService<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient>();

        // Step 1: Load confirmed mistake items and extract (studentId, ossPath) pairs.
        // Only confirmed mistakes have their images migrated to mistakes/ and survive
        // upload-record deletion; pending/rejected items still have uploads/ paths and
        // their fingerprints are maintained by the normal upload dedup path.
        // NOTE: SourceRegions is a JSONB column — cannot use SelectMany server-side,
        // so we materialize first then extract paths client-side.
        var items = await dbContext.MistakeItems
            .Where(i => i.ReviewStatus == ConfirmedReviewStatus)
            .Select(i => new
            {
                i.StudentId,
                i.SourceRegions
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var pairs = new List<(string StudentId, string OssPath)>();
        foreach (var item in items)
        {
            if (item.SourceRegions == null) continue;
            foreach (var region in item.SourceRegions)
            {
                if (!string.IsNullOrWhiteSpace(region.SourceImagePath))
                {
                    pairs.Add((item.StudentId, region.SourceImagePath));
                }
            }
        }

        _logger.LogInformation("FingerprintBackfillWorker: {PairCount} confirmed-mistake images to backfill (from {ItemCount} items)",
            pairs.Count, items.Count);

        if (pairs.Count == 0)
        {
            return;
        }

        // Step 2: Process in batches — download each image, compute SHA-256, collect items.
        var totalCreated = 0;
        var totalSkipped = 0;
        var totalFailed = 0;

        for (var i = 0; i < pairs.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = pairs.Skip(i).Take(BatchSize).ToList();
            var batchItems = new List<(string Hash, string StudentId, string OssPath)>();

            foreach (var (studentId, ossPath) in batch)
            {
                try
                {
                    using var stream = await ossService.DownloadAsync(ossPath).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                    var bytes = ms.ToArray();
                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    batchItems.Add((hash, studentId, ossPath));
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    _logger.LogWarning(ex,
                        "FingerprintBackfillWorker: failed to download/hash image {Path}, skipping",
                        ossPath);
                }
            }

            if (batchItems.Count == 0)
            {
                continue;
            }

            // Step 3: Call student gRPC BatchCreateFingerprints for this batch.
            var request = new StudentProto.BatchCreateFingerprintsRequest();
            foreach (var (hash, studentId, ossPath) in batchItems)
            {
                request.Fingerprints.Add(new StudentProto.FingerprintBackfillItem
                {
                    Hash = hash,
                    StudentId = studentId,
                    OssPath = ossPath
                });
            }

            try
            {
                var response = await studentClient.BatchCreateFingerprintsAsync(request, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (response.Success)
                {
                    totalCreated += response.CreatedCount;
                    totalSkipped += response.SkippedCount;
                    _logger.LogInformation(
                        "FingerprintBackfillWorker: batch {Start}-{End} done (created={Created}, skipped={Skipped})",
                        i, i + batch.Count, response.CreatedCount, response.SkippedCount);
                }
                else
                {
                    totalFailed += batchItems.Count;
                    _logger.LogWarning("FingerprintBackfillWorker: batch {Start}-{End} gRPC failed: {Error}",
                        i, i + batch.Count, response.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                totalFailed += batchItems.Count;
                _logger.LogError(ex,
                    "FingerprintBackfillWorker: batch {Start}-{End} gRPC call failed",
                    i, i + batch.Count);
            }
        }

        _logger.LogInformation(
            "FingerprintBackfillWorker: summary — created={Created}, skipped={Skipped}, failed={Failed}, total={Total}",
            totalCreated, totalSkipped, totalFailed, pairs.Count);
    }
}
