using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

/// <summary>One per-image work item of a layout job (IKQA2O): the exact retained byte
/// identity (length + SHA-256) verified before every transmission, independent progress
/// with bounded attempts, the last bounded error kind and the retry backoff deadline.</summary>
[Table("layout_job_image")]
public sealed class LayoutJobImageEntity
{
    [Column("job_id")]
    public Guid JobId { get; set; }
    [Column("image_path")]
    public string ImagePath { get; set; } = "";
    [Column("position")]
    public int Position { get; set; }
    [Column("expected_length")]
    public long ExpectedLength { get; set; }
    [Column("expected_sha256")]
    public string ExpectedSha256 { get; set; } = "";
    [Column("status")]
    public string Status { get; set; } = "";
    [Column("attempts")]
    public int Attempts { get; set; }
    [Column("last_error")]
    public string? LastError { get; set; }
    [Column("retry_not_before")]
    public DateTimeOffset? RetryNotBefore { get; set; }
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
