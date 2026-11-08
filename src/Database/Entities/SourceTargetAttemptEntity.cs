using System.ComponentModel.DataAnnotations.Schema;
namespace Emenda.Database.Entities;

[Table("source_target_attempt")]
public sealed class SourceTargetAttemptEntity
{
    [Column("attempt_id")]
    public Guid AttemptId { get; set; }
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("source_path")]
    public string SourcePath { get; set; } = "";
    [Column("target_key")]
    public string TargetKey { get; set; } = "";
    [Column("owner_id")]
    public Guid OwnerId { get; set; }
    [Column("fence")]
    public long Fence { get; set; }
    [Column("phase")]
    public string Phase { get; set; } = "";
    [Column("verified_length")]
    public long? VerifiedLength { get; set; }
    [Column("verified_sha256")]
    public string? VerifiedSha256 { get; set; }
}
