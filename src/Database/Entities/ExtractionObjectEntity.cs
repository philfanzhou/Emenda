using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_object")]
public sealed class ExtractionObjectEntity
{
    [Key, Column("path")] public string Path { get; set; } = "";
    [Column("job_id")] public Guid JobId { get; set; }
    [Column("pending")] public bool Pending { get; set; }
    [Column("legacy")] public bool Legacy { get; set; }
    [Column("attempts")] public int Attempts { get; set; }
    [Column("next_attempt")] public DateTimeOffset NextAttempt { get; set; }
}
