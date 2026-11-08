using System.ComponentModel.DataAnnotations.Schema;
namespace Emenda.Database.Entities;

[Table("source_intake_request")]
public sealed class SourceIntakeRequestEntity
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
    [Column("item_ids")]
    public Guid[]? ItemIds { get; set; }
}
