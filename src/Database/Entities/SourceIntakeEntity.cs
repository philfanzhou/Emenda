using System.ComponentModel.DataAnnotations.Schema;
namespace Emenda.Database.Entities;

[Table("source_intake")]
public sealed class SourceIntakeEntity
{
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("snapshot_id")]
    public Guid SnapshotId { get; set; }
    [Column("student_id")]
    public string StudentId { get; set; } = "";
    [Column("manifest")]
    public string Manifest { get; set; } = "";
    [Column("mode")]
    public string Mode { get; set; } = "";
    [Column("owner_id")]
    public Guid OwnerId { get; set; }
    [Column("fence")]
    public long Fence { get; set; }
    [Column("lease_until")]
    public DateTimeOffset? LeaseUntil { get; set; }
    [Column("materialized")]
    public bool Materialized { get; set; }
}
