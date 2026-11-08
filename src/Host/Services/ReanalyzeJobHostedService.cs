using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Services;

namespace Ruoyu.Study.Mistake.Host.Services;

/// <summary>Two local consumers share the database's two global lease slots.</summary>
public class ReanalyzeJobHostedService : BackgroundService
{
    private const int MaxConcurrency = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private readonly IServiceProvider _services;
    private readonly ILogger<ReanalyzeJobHostedService> _logger;

    public ReanalyzeJobHostedService(IServiceProvider services, ILogger<ReanalyzeJobHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReanalyzeJobHostedService starting: maxConcurrency={MaxConcurrency}", MaxConcurrency);
        var workers = Enumerable.Range(0, MaxConcurrency).Select(_ => RunWorkerAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
                var jobs = await service.ClaimJobsAsync(1, stoppingToken);
                if (jobs.Count == 1) await ProcessAsync(jobs[0], stoppingToken);
                else await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Reanalyze worker cycle failed: {ErrorType}", ex.GetType().Name);
                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ProcessAsync(ReanalyzeJobModel job, CancellationToken stoppingToken)
    {
        if (job.OwnerId is not { } owner) return;
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(job.Id, owner, job.FencingVersion, leaseCts);
        var terminalStarted = false;
        try
        {
            using var scope = _services.CreateScope();
            var analyzer = scope.ServiceProvider.GetRequiredService<IVlAnalyzer>();
            var service = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
            var watch = Stopwatch.StartNew();
            var result = await analyzer.ReAnalyzeAsync(job.ExecutionStudentId ?? job.StudentId, job.ExecutionImagePaths.ToList(),
                job.ExecutionRootCause ?? string.Empty, job.TeacherDescription, leaseCts.Token);
            watch.Stop();
            if (!leaseCts.IsCancellationRequested)
            {
                terminalStarted = true;
                if (result.Skipped || string.IsNullOrWhiteSpace(result.RootCause))
                    await service.MarkFailedAsync(job.Id, owner, job.FencingVersion, "vl_empty", stoppingToken);
                else
                    await service.MarkCompletedAsync(job.Id, owner, job.FencingVersion,
                        result.RootCause, watch.ElapsedMilliseconds, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested)
        {
            _logger.LogInformation("Reanalyze execution interrupted; lease will expire: JobId={JobId}", job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Reanalyze execution failed: JobId={JobId}, ErrorType={ErrorType}", job.Id, ex.GetType().Name);
            if (terminalStarted)
            {
                try
                {
                    using var readScope = _services.CreateScope();
                    var readService = readScope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
                    var saved = await readService.GetJobsAsync(job.ReviewerId, [job.Id], stoppingToken);
                    _logger.LogWarning("Reanalyze terminal result re-read: JobId={JobId}, Status={Status}",
                        job.Id, saved.Count == 1 ? saved[0].Status : ReanalyzeJobState.Unspecified);
                }
                catch (Exception readError)
                {
                    _logger.LogWarning("Reanalyze terminal result unknown: JobId={JobId}, ErrorType={ErrorType}",
                        job.Id, readError.GetType().Name);
                }
                return;
            }
            if (!stoppingToken.IsCancellationRequested && !leaseCts.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
                    await service.MarkFailedAsync(job.Id, owner, job.FencingVersion, "vl_failed", stoppingToken);
                }
                catch (Exception failure)
                {
                    _logger.LogWarning("Reanalyze failure recording deferred to lease recovery: JobId={JobId}, ErrorType={ErrorType}",
                        job.Id, failure.GetType().Name);
                }
            }
        }
        finally
        {
            leaseCts.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task HeartbeatAsync(Guid jobId, Guid owner, long version, CancellationTokenSource leaseCts)
    {
        try
        {
            while (!leaseCts.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, leaseCts.Token);
                using var scope = _services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
                if (!await service.RenewLeaseAsync(jobId, owner, version, leaseCts.Token))
                {
                    leaseCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Reanalyze heartbeat lost: JobId={JobId}, ErrorType={ErrorType}", jobId, ex.GetType().Name);
            leaseCts.Cancel();
        }
    }
}
