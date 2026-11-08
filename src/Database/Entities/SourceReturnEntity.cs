using System.ComponentModel.DataAnnotations.Schema;
namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("source_return")]
public sealed class SourceReturnEntity
{
    [Column("item_id")]
    public Guid ItemId { get; set; }
    [Column("operation_id")]
    public string OperationId { get; set; } = "";
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("snapshot_id")]
    public Guid SnapshotId { get; set; }
    [Column("student_id")]
    public string StudentId { get; set; } = "";
    [Column("actor")]
    public string Actor { get; set; } = "";
    [Column("canonical")]
    public string Canonical { get; set; } = "";
    [Column("original_item")]
    public string OriginalItem { get; set; } = "";
    [Column("return_reason")]
    public string ReturnReason { get; set; } = "";
    [Column("root_cause")]
    public string? RootCause { get; set; }
    [Column("grade")]
    public int? Grade { get; set; }
    [Column("subject")]
    public int? Subject { get; set; }
    [Column("owner_id")]
    public Guid OwnerId { get; set; }
    [Column("fence")]
    public long Fence { get; set; }
    [Column("phase")]
    public string Phase { get; set; } = "Prepared";
    [Column("payload")]
    public string? Payload { get; set; }
    [Column("payload_digest")]
    public string? PayloadDigest { get; set; }
    [Column("error_kind")]
    public string? ErrorKind { get; set; }
    [Column("confirmed_record_id")]
    public Guid? ConfirmedRecordId { get; set; }
    [Column("receipt_created_at")]
    public DateTimeOffset? ReceiptCreatedAt { get; set; }
    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
    [Column("final_log_id")]
    public Guid? FinalLogId { get; set; }
}
