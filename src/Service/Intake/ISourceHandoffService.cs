namespace Ruoyu.Study.Mistake.Service.Intake;

public interface ISourceHandoffService
{
    bool HandoffsEnabled { get; }
    Task<SourceHandoffView> HandoffAsync(Guid source,Guid revision,string actor,bool automatic=false,CancellationToken ct=default);
    Task<List<(Guid Source,Guid Revision,bool Sealed)>> PendingHandoffsAsync(CancellationToken ct=default);
}
