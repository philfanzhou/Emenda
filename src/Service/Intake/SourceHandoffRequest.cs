namespace Emenda.Service.Intake;

public sealed record SourceHandoffRequest(Guid SnapshotId, string GroupsDigest, IReadOnlyList<SourceHandoffTarget> Targets);
