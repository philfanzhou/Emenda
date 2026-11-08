namespace Emenda.Service.Intake;

public interface ISourceTargetService
{
    bool TargetsEnabled { get; }
    Task<IntakeView> ResumeTargetsAsync(Guid sourceId, Guid revision, CancellationToken ct = default);
    Task<List<(Guid SourceId, Guid Revision)>> PendingTargetsAsync(CancellationToken ct = default);
}

public sealed record SourceTargetView(string SourcePath, string State, string? TargetKey);
