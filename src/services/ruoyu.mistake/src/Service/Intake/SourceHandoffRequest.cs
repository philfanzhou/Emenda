namespace Ruoyu.Study.Mistake.Service.Intake;

public sealed record SourceHandoffRequest(Guid SnapshotId, string GroupsDigest, IReadOnlyList<SourceHandoffTarget> Targets);
