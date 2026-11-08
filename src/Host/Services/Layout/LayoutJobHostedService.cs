using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;

namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>
/// Durable execution worker for layout jobs (IKQA2O), following the
/// ReanalyzeJobHostedService claim → 20 s heartbeat → fenced terminal transition
/// conventions with a fixed concurrency of 2 workers. Every inference exchange runs
/// outside any database transaction: bytes are downloaded from retained storage and
/// verified against the retained length and SHA-256 before transmission, so missing or
/// mismatched bytes fail that one image with zero upstream requests. Per-image progress
/// is independent — completed images are skipped, only eligible failed/pending images
/// are retried under the bounded backoff, and turning the capability off requeues the
/// job without a verdict so it resumes when work is accepted again.
/// </summary>
public sealed class LayoutJobHostedService(IServiceProvider services,
    ILogger<LayoutJobHostedService> logger, ILayoutDetectionClient client, IOssService oss) : BackgroundService
{
    private const int MaxConcurrency = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LayoutJobHostedService starting: maxConcurrency={MaxConcurrency}", MaxConcurrency);
        var workers = Enumerable.Range(0, MaxConcurrency).Select(_ => RunWorkerAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILayoutJobService>();
                var claim = await service.ClaimAsync(stoppingToken);
                if (claim != null)
                {
                    var requeued = await ProcessAsync(claim, stoppingToken);
                    // A requeued job has no eligible work right now (image backoffs); do
                    // not spin on it — wait out one poll interval before claiming again.
                    if (requeued) await Task.Delay(PollInterval, stoppingToken);
                }
                else await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Layout job worker cycle deferred: {ErrorType}", ex.GetType().Name);
                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>Processes one claimed job generation. Returns true when the job was
    /// requeued without a verdict (per-image backoff pending or capability parked).</summary>
    private async Task<bool> ProcessAsync(LayoutJobClaim claim, CancellationToken stoppingToken)
    {
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(claim, leaseCts);
        var requeued = false;
        try
        {
            using var scope = services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ILayoutJobService>();
            var manifestRotations = JsonSerializer.Deserialize<int[]>(claim.Job.ImageRotations,
                (JsonSerializerOptions?)null) ?? [];
            foreach (var image in claim.Job.Images.OrderBy(i => i.Position))
            {
                if (leaseCts.IsCancellationRequested) break;
                if (image.Status == LayoutJobCanonical.Succeeded) continue;
                if (!LayoutJobCanonical.IsEligible(image.Status, image.Attempts, image.RetryNotBefore,
                        DateTimeOffset.UtcNow))
                    continue;
                try
                {
                    var bytes = await DownloadVerifiedAsync(image.ImagePath,
                        (image.ExpectedLength, image.ExpectedSha256), leaseCts.Token);
                    if (!LayoutImageInspection.TryInspect(bytes, out var mediaType, out _, out _, out var rejection))
                    {
                        await service.RecordImageFailureAsync(claim, image.ImagePath,
                            "layout_image_unsupported", false, null, leaseCts.Token);
                        continue;
                    }
                    var result = await client.DetectLayoutAsync(
                        new LayoutDetectionImage(bytes, mediaType), leaseCts.Token);
                    if (!await service.PublishResultAsync(claim, image.ImagePath, result, leaseCts.Token))
                        logger.LogInformation(
                            "Layout result discarded (stale claim or already accepted): JobId={JobId}, Image={ImagePosition}",
                            claim.JobId, image.Position);
                }
                catch (LayoutDetectionException ex) when (ex.Failure is LayoutDetectionFailure.CapabilityDisabled
                    or LayoutDetectionFailure.Configuration)
                {
                    // The capability is off for this process generation: park the job
                    // without a verdict and without consuming attempts; it resumes when
                    // work is accepted again (existing results stay visible throughout).
                    requeued = await service.ReleaseAsync(claim, CancellationToken.None) || requeued;
                    logger.LogInformation("Layout job parked; capability off: JobId={JobId}", claim.JobId);
                    break;
                }
                catch (LayoutDetectionException ex)
                {
                    await service.RecordImageFailureAsync(claim, image.ImagePath, ex.Failure.ToString(),
                        ex.IsTransient, ex.RetryAfter, leaseCts.Token);
                }
                catch (LayoutJobException ex)
                {
                    // Byte verification refused this image: a mismatch against the
                    // retained identity never becomes upstream inference and is not
                    // retried; an unavailable dependency stays retryable.
                    var retryable = ex.ErrorKind == "source_unavailable";
                    await service.RecordImageFailureAsync(claim, image.ImagePath, ex.ErrorKind, retryable,
                        null, leaseCts.Token);
                }
            }
            if (!requeued && !leaseCts.IsCancellationRequested)
            {
                var fresh = await service.GetJobAsync(claim.JobId, stoppingToken);
                if (fresh != null)
                {
                    // Images that may still retry (backoff pending with budget left)
                    // requeue the job for a later claim; only a fully settled image set
                    // closes the generation with the derived verdict.
                    var mayRetry = fresh.Images.Any(image => LayoutJobCanonical.MayRetryLater(
                        image.Status, image.Attempts));
                    if (mayRetry) requeued = await service.ReleaseAsync(claim, stoppingToken);
                    else await service.CompleteJobAsync(claim, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Layout job lease lost; work resumes after lease expiry: JobId={JobId}", claim.JobId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Layout job processing deferred: JobId={JobId}, ErrorType={ErrorType}",
                claim.JobId, ex.GetType().Name);
        }
        finally
        {
            leaseCts.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
        }
        return requeued;
    }

    private async Task HeartbeatAsync(LayoutJobClaim claim, CancellationTokenSource leaseCts)
    {
        try
        {
            while (!leaseCts.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, leaseCts.Token);
                using var scope = services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILayoutJobService>();
                if (!await service.RenewLeaseAsync(claim.JobId, claim.Owner, claim.Fence, leaseCts.Token))
                {
                    leaseCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("Layout job heartbeat lost: JobId={JobId}, ErrorType={ErrorType}",
                claim.JobId, ex.GetType().Name);
            leaseCts.Cancel();
        }
    }

    /// <summary>Downloads one retained image and verifies it against the retained byte
    /// identity before any transmission. Runs outside any database transaction; a
    /// mismatch fails only this image and never touches the source identity.</summary>
    private async Task<byte[]> DownloadVerifiedAsync(string path,
        (long Length, string Sha256) identity, CancellationToken ct)
    {
        byte[] data;
        try
        {
            using var stream = await oss.DownloadAsync(path);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, ct);
            data = bytes.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new LayoutJobException("source_unavailable", 503); }
        if (data.LongLength != identity.Length ||
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != identity.Sha256)
            throw new LayoutJobException("source_bytes_conflict", 503);
        return data;
    }
}
