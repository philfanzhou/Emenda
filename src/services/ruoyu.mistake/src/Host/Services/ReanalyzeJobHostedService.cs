using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Services;

namespace Ruoyu.Study.Mistake.Host.Services;

/// <summary>
/// Background service that processes ReanalyzeJob records.
/// Global concurrency is hardcoded to <see cref="MaxConcurrency"/> per product
/// decision — do not promote to configuration without re-discussion.
/// State machine: Pending -> Running -> Completed | Failed.
/// On host start, any Running jobs from a previous process incarnation are
/// marked Failed (crash recovery).
/// </summary>
public class ReanalyzeJobHostedService : BackgroundService
{
    /// <summary>Hardcoded per product decision (2026-06-26). Do not externalize.</summary>
    private const int MaxConcurrency = 2;

    private const int ClaimBatchSize = 50;
    // Must exceed VL timeout (TimeoutSeconds=60 × MaxRetries=2 = 120s) so that
    // a single job is not enqueued twice while its first execution is still in
    // flight. MarkRunningAsync commits the status change immediately, so the
    // second claim will simply skip the row — but we still want to avoid the
    // duplicate Channel enqueue + worker contention.
    private static readonly TimeSpan ClaimInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReanalyzeJobHostedService> _logger;

    public ReanalyzeJobHostedService(
        IServiceProvider serviceProvider,
        ILogger<ReanalyzeJobHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ReanalyzeJobHostedService starting: maxConcurrency={MaxConcurrency}",
            MaxConcurrency);

        // Crash recovery must happen before the main loop starts accepting work.
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var jobService = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
            await jobService.RecoverStaleRunningJobsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReanalyzeJobHostedService: crash recovery failed (continuing)");
        }

        var queue = Channel.CreateUnbounded<ReanalyzeJobModel>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

        // Spawn MaxConcurrency worker tasks.
        var workers = new Task[MaxConcurrency];
        for (int i = 0; i < MaxConcurrency; i++)
        {
            int workerIndex = i;
            workers[i] = Task.Run(() => RunWorkerAsync(workerIndex, queue.Reader, stoppingToken), stoppingToken);
        }

        // Producer: poll DB for pending jobs and enqueue them.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var jobService = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
                    var pending = await jobService.ClaimPendingJobsAsync(ClaimBatchSize, stoppingToken);
                    if (pending.Count > 0)
                    {
                        _logger.LogDebug("ReanalyzeJobHostedService: claimed {Count} pending jobs", pending.Count);
                        foreach (var job in pending)
                        {
                            await queue.Writer.WriteAsync(job, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReanalyzeJobHostedService: claim cycle failed (continuing)");
                }

                try
                {
                    await Task.Delay(ClaimInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            try
            {
                await Task.WhenAll(workers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReanalyzeJobHostedService: workers exited with error");
            }
        }

        _logger.LogInformation("ReanalyzeJobHostedService stopped");
    }

    private async Task RunWorkerAsync(int workerIndex, ChannelReader<ReanalyzeJobModel> reader, CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReanalyzeJob worker {WorkerIndex} started", workerIndex);

        await foreach (var job in reader.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(job, stoppingToken);
        }

        _logger.LogInformation("ReanalyzeJob worker {WorkerIndex} stopped", workerIndex);
    }

    private async Task ProcessJobAsync(ReanalyzeJobModel job, CancellationToken stoppingToken)
    {
        // Each job gets its own scope so DbContext is not shared.
        using var scope = _serviceProvider.CreateScope();
        var jobService = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
        var vlAnalyzer = scope.ServiceProvider.GetRequiredService<IVlAnalyzer>();
        var mistakeItemRepo = scope.ServiceProvider.GetRequiredService<Ruoyu.Study.Mistake.Domain.Repositories.IMistakeItemRepository>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var workerLogger = loggerFactory.CreateLogger("ReanalyzeJobWorker");

        if (!await jobService.MarkRunningAsync(job.Id, stoppingToken))
        {
            workerLogger.LogWarning(
                "ReanalyzeJob: cannot mark running (already moved). JobId={JobId}",
                job.Id);
            return;
        }

        var item = await mistakeItemRepo.GetByIdAsync(job.ItemId, stoppingToken);
        if (item == null)
        {
            await jobService.MarkFailedAsync(job.Id, "错题已删除", stoppingToken);
            return;
        }

        var imagePaths = item.SourceRegions
            .Where(r => !string.IsNullOrWhiteSpace(r.SourceImagePath))
            .Select(r => r.SourceImagePath)
            .ToList();
        var previousRootCause = job.PreviousRootCause ?? string.Empty;
        var teacherDescription = job.TeacherDescription;

        var sw = Stopwatch.StartNew();
        try
        {
            var vlResult = await vlAnalyzer.ReAnalyzeAsync(
                item.StudentId, imagePaths, previousRootCause, teacherDescription, stoppingToken);
            sw.Stop();

            if (vlResult.Skipped || string.IsNullOrEmpty(vlResult.RootCause))
            {
                await jobService.MarkFailedAsync(job.Id, "VL 未返回根因", stoppingToken);
                return;
            }

            await jobService.MarkCompletedAsync(job.Id, vlResult.RootCause, sw.ElapsedMilliseconds, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service is shutting down. Leave the job in Running state; the next
            // startup's crash recovery will mark it Failed.
            workerLogger.LogWarning(
                "ReanalyzeJob cancelled mid-VL: JobId={JobId}, ItemId={ItemId}",
                job.Id, job.ItemId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            workerLogger.LogError(ex,
                "ReanalyzeJob: VL call failed. JobId={JobId}, ItemId={ItemId}, DurationMs={DurationMs}",
                job.Id, job.ItemId, sw.ElapsedMilliseconds);

            try
            {
                await jobService.MarkFailedAsync(job.Id, $"VL 调用失败: {ex.Message}", stoppingToken);
            }
            catch (Exception failEx)
            {
                workerLogger.LogError(failEx,
                    "ReanalyzeJob: failed to write failure state. JobId={JobId}",
                    job.Id);
            }
        }
    }
}
