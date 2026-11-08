using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed class SourceHandoffWorker(IServiceScopeFactory scopes,ILogger<SourceHandoffWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                using var scope=scopes.CreateScope();
                var targets=scope.ServiceProvider.GetRequiredService<ISourceHandoffService>();
                foreach(var source in await targets.PendingHandoffsAsync(stoppingToken))
                {
                    try {await targets.HandoffAsync(source.Source,source.Revision,"ruoyu.mistake",automatic:!source.Sealed,ct:stoppingToken);}
                    catch(SourceIntakeException ex) {logger.LogWarning("Source handoff resume deferred: {Kind}",ex.ErrorKind);}
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {return;}
            catch(Exception) {logger.LogWarning("Source handoff scan unavailable; durable candidates retained");}
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
