using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_revision")]
public sealed class ExtractionRevisionEntity
{
    [Column("job_id")] public Guid JobId { get; set; }
    [Column("version")] public int Version { get; set; }
    [Column("request_key")] public Guid RequestKey { get; set; }
    [Column("payload_hash"), Required, MaxLength(64)] public string PayloadHash { get; set; } = "";
    [Column("snapshot_json", TypeName = "jsonb"), Required] public string SnapshotJson { get; set; } = "{}";
    [Column("actor"), Required] public string Actor { get; set; } = "";
    [Column("reason"), Required] public string Reason { get; set; } = "";
    [Column("model"), Required] public string Model { get; set; } = "";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
