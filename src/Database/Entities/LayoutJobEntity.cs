using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

/// <summary>One durable layout-detection job over an existing managed source revision
/// (IKQA2O). Carries the lease/heartbeat/fencing claim state, the retained manifest, the
/// captured non-secret processing configuration and the credential reference (a
/// configuration key name, never the credential itself).</summary>
[Table("layout_job")]
public sealed class LayoutJobEntity
{
    [Column("job_id")]
    public Guid JobId { get; set; }
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
    [Column("result_revision")]
    public int ResultRevision { get; set; }
    [Column("status")]
    public string Status { get; set; } = "";
    [Column("manifest")]
    public string Manifest { get; set; } = "";
    [Column("config")]
    public string Config { get; set; } = "";
    [Column("credential_ref")]
    public string CredentialRef { get; set; } = "";
    [Column("image_rotations")]
    public string ImageRotations { get; set; } = "[]";
    [Column("owner_id")]
    public Guid OwnerId { get; set; }
    [Column("fence")]
    public long Fence { get; set; }
    [Column("lease_until")]
    public DateTimeOffset? LeaseUntil { get; set; }
    [Column("attempt")]
    public int Attempt { get; set; }
    [Column("error_kind")]
    public string? ErrorKind { get; set; }
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}
