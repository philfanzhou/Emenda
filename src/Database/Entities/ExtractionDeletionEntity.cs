using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_deletion")]
public sealed class ExtractionDeletionEntity
{
    [Key, Column("job_id")] public Guid JobId { get; set; }
    [Column("admission_hash"), Required, MaxLength(64)] public string AdmissionHash { get; set; } = "";
    [Column("source_binding_hash"), Required, MaxLength(64)] public string SourceBindingHash { get; set; } = "";
    [Column("owner_hash"), Required, MaxLength(64)] public string OwnerHash { get; set; } = "";
    [Column("deleted_at")] public DateTimeOffset DeletedAt { get; set; }
    [Column("deleted_mistakes")] public int DeletedMistakes { get; set; }
    [Column("retained_confirmed")] public int RetainedConfirmed { get; set; }
}
