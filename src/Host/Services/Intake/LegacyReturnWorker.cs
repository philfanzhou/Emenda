using Emenda.Service.Intake;

namespace Emenda.Host.Services.Intake;

public sealed class LegacyReturnWorker(IServiceScopeFactory scopes, ILogger<LegacyReturnWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILegacyReturnService>();
                foreach (var item in await service.PendingAsync(stoppingToken))
                {
                    try { await service.ResumeAsync(item, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception) { logger.LogWarning("Legacy return deferred; durable operation retained"); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Legacy return scan unavailable; durable operations retained"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
