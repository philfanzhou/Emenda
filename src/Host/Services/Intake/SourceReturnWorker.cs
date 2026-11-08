using Emenda.Domain.Services;
using Emenda.Service.Intake;
namespace Emenda.Host.Services.Intake;

public sealed class SourceReturnWorker(IServiceScopeFactory scopes,ILogger<SourceReturnWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                using var scope=scopes.CreateScope();
                var targets=scope.ServiceProvider.GetRequiredService<ISourceReturnService>();
                foreach(var source in await targets.PendingReturnsAsync(stoppingToken))
                {
                    try {await targets.ResumeReturnAsync(source,stoppingToken);}
                    catch(SourceIntakeException ex) {logger.LogWarning("Source return resume deferred: {Kind}",ex.ErrorKind);}
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {return;}
            catch(Exception) {logger.LogWarning("Source return scan unavailable; durable candidates retained");}
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
