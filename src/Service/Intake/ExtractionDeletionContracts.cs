namespace Emenda.Service.Intake;

public sealed record ExtractionDeletionView(Guid JobId, DateTimeOffset DeletedAt,
    int DeletedMistakes, int RetainedConfirmed, int PendingObjects, string Status);

public interface IExtractionDeletionService
{
    Task<ExtractionDeletionView> DeleteAsync(ExtractionAccess access, Guid id, CancellationToken ct = default);
    Task<ExtractionDeletionView> GetDeletionAsync(ExtractionAccess access, Guid id, CancellationToken ct = default);
}
