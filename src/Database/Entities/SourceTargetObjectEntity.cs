using System.ComponentModel.DataAnnotations.Schema;
namespace Emenda.Database.Entities;

[Table("source_target_object")]
public sealed class SourceTargetObjectEntity
{
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("source_path")]
    public string SourcePath { get; set; } = "";
    [Column("snapshot_id")]
    public Guid SnapshotId { get; set; }
    [Column("student_id")]
    public string StudentId { get; set; } = "";
    [Column("expected_length")]
    public long ExpectedLength { get; set; }
    [Column("expected_sha256")]
    public string ExpectedSha256 { get; set; } = "";
    [Column("selected_attempt_id")]
    public Guid? SelectedAttemptId { get; set; }
}
