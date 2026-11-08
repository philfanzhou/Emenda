namespace Ruoyu.Study.Mistake.Service.Intake;

public sealed record SourceHandoffReceipt(Guid ReceiptId, string OperationId, Guid SourceUploadId,
    Guid ContentRevision, Guid SnapshotId, string StudentId, string GroupsDigest, string PayloadDigest,
    string DigestVersion, DateTimeOffset CommittedAt, bool RecordDeleted, int RemainingImageCount,
    Guid? ResultingContentRevision);
