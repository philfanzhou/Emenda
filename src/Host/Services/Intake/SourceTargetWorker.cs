using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed class SourceTargetWorker(IServiceScopeFactory scopes,ILogger<SourceTargetWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                using var scope=scopes.CreateScope();
                var targets=scope.ServiceProvider.GetRequiredService<ISourceTargetService>();
                foreach(var source in await targets.PendingTargetsAsync(stoppingToken))
                {
                    try {await targets.ResumeTargetsAsync(source.SourceId,source.Revision,stoppingToken);}
                    catch(SourceIntakeException ex) {logger.LogWarning("Source target resume deferred: {Kind}",ex.ErrorKind);}
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {return;}
            catch(Exception ex) {logger.LogWarning(ex,"Source target scan unavailable; durable candidates retained");}
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
