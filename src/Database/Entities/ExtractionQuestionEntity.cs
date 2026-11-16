using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_question")]
public sealed class ExtractionQuestionEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("job_id")] public Guid JobId { get; set; }
    // Deliberately not a foreign key: this retained receipt survives item deletion.
    [Column("item_id")] public Guid? ItemId { get; set; }
    [Column("active")] public bool Active { get; set; }
}
