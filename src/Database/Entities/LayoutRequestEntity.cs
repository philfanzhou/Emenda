using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

/// <summary>Request-key replay record for layout jobs (IKQA2O), mirroring
/// source_intake_request: the same caller/action/request key replays the same job; a
/// different canonical input for the same key is a conflict. Immutable once written.</summary>
[Table("layout_request")]
public sealed class LayoutRequestEntity
{
    [Column("actor")]
    public string Actor { get; set; } = "";
    [Column("action")]
    public string Action { get; set; } = "";
    [Column("request_key")]
    public Guid RequestKey { get; set; }
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("canonical")]
    public string Canonical { get; set; } = "";
    [Column("job_id")]
    public Guid JobId { get; set; }
}
