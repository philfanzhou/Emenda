using Ruoyu.Study.Mistake.Domain.Models;
namespace Ruoyu.Study.Mistake.Service.Intake;

public interface ISourceIntakeService
{
    bool Enabled { get; }
    Task<List<MistakeItemModel>> CreateManualAsync(Guid sourceId, Guid revision, Guid requestKey,
        string actor, string studentId, int subject, int grade, string? rootCause,
        List<SourceRegionModel> regions, CancellationToken ct = default);
    Task<List<MistakeItemModel>> CreateAutomaticAsync(Guid sourceId, Guid revision, CancellationToken ct = default);
    Task<string?> GetManagedItemStudentAsync(Guid itemId, CancellationToken ct = default);
    Task<List<string>> GetManagedJobReviewersAsync(IReadOnlyList<Guid> jobIds, CancellationToken ct = default);
    Task<bool> HasManagedDataAsync(string? studentId = null, string? sourceId = null, CancellationToken ct = default);
    Task<IntakeView?> GetAsync(Guid sourceId, Guid revision, CancellationToken ct = default);
    Task<string?> GetManagedObjectStudentAsync(string path, CancellationToken ct = default);
    Task<bool> IsManagedPracticeObjectAsync(string path, CancellationToken ct = default);
    Task VerifyManagedObjectAsync(string path, CancellationToken ct = default);
}

public sealed record IntakeView(SourceManifest Manifest, string Mode, long Fence,
    DateTimeOffset? LeaseUntil, bool Materialized, string SourceState, List<Guid> ItemIds, List<SourceTargetView>? Targets = null, bool TargetReady = false, bool HandoffCompleted = false, List<SourceReturnView>? Returns = null, SourceHandoffView? Handoff = null);
