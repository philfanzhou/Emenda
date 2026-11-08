namespace Ruoyu.Study.Mistake.Service.Intake;

public interface ILegacyReturnService
{
    Task<SourceReturnView?> GetAsync(Guid itemId, CancellationToken ct = default);
    Task<SourceReturnView> ReturnAsync(Guid itemId, string actor, string? reason, string? cause,
        int grade, int subject, CancellationToken ct = default);
    Task<SourceReturnView> ResumeAsync(Guid itemId, CancellationToken ct = default);
    Task<List<Guid>> PendingAsync(CancellationToken ct = default);
}
