using System.ComponentModel.DataAnnotations.Schema;
namespace Emenda.Database.Entities;

[Table("source_handoff")]
public sealed class SourceHandoffEntity
{
    [Column("source_upload_id")] public Guid SourceUploadId { get; set; }
    [Column("content_revision")] public Guid ContentRevision { get; set; }
    [Column("snapshot_id")] public Guid SnapshotId { get; set; }
    [Column("student_id")] public string StudentId { get; set; } = "";
    [Column("operation_id")] public string OperationId { get; set; } = "";
    [Column("actor")] public string Actor { get; set; } = "";
    [Column("sealed_at")] public DateTimeOffset SealedAt { get; set; }
    [Column("groups_canonical")] public string GroupsCanonical { get; set; } = "";
    [Column("groups_digest")] public string GroupsDigest { get; set; } = "";
    [Column("targets")] public string Targets { get; set; } = "";
    [Column("payload_canonical")] public string PayloadCanonical { get; set; } = "";
    [Column("payload_digest")] public string PayloadDigest { get; set; } = "";
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("fence")] public long Fence { get; set; }
    [Column("phase")] public string Phase { get; set; } = "Prepared";
    [Column("receipt")] public string? Receipt { get; set; }
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
    [Column("error_kind")] public string? ErrorKind { get; set; }
}
