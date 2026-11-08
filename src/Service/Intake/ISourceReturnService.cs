namespace Emenda.Service.Intake;

public interface ISourceReturnService
{
    Task<SourceReturnView> ReturnAsync(Guid itemId, string actor, string? reason, string? cause, int grade, int subject, CancellationToken ct = default);
    Task<SourceReturnView> ResumeReturnAsync(Guid itemId, CancellationToken ct = default);
    Task<List<Guid>> PendingReturnsAsync(CancellationToken ct = default);
}

public sealed record SourceReturnView(Guid ItemId, string OperationId, string State, Guid? ConfirmedRecordId, DateTimeOffset? CompletedAt, string? ErrorKind)
{
    public bool Success => State == "Completed";
}
