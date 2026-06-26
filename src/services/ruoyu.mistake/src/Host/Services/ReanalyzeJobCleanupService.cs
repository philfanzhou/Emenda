using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Domain.Services;

namespace Ruoyu.Study.Mistake.Host.Services;

/// <summary>
/// Periodically deletes Completed/Failed jobs older than 30 days.
/// RetentionDays is hardcoded per product decision.
/// </summary>
public class ReanalyzeJobCleanupService : BackgroundService
{
    private const int RetentionDays = 30;

    private static readonly TimeSpan ProductionInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan DevelopmentInterval = TimeSpan.FromHours(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ReanalyzeJobCleanupService> _logger;

    public ReanalyzeJobCleanupService(
        IServiceProvider serviceProvider,
        IHostEnvironment env,
        ILogger<ReanalyzeJobCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _env.IsDevelopment() ? DevelopmentInterval : ProductionInterval;
        _logger.LogInformation(
            "ReanalyzeJobCleanupService starting: retentionDays={RetentionDays}, interval={Interval}",
            RetentionDays, interval);

        // Run once on start, then on the configured interval.
        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunOnceAsync(stoppingToken);
        }

        _logger.LogInformation("ReanalyzeJobCleanupService stopped");
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var jobService = scope.ServiceProvider.GetRequiredService<IReanalyzeJobService>();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            await jobService.DeleteExpiredJobsAsync(cutoff, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReanalyzeJobCleanupService: cleanup cycle failed (continuing)");
        }
    }
}
