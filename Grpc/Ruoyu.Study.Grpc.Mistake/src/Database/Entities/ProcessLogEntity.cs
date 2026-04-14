using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("process_log")]
public class ProcessLogEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("mistake_id")]
    [Required]
    public Guid MistakeId { get; set; } = Guid.Empty;

    [Column("action")]
    public int Action { get; set; }

    [Column("details")]
    public string? Details { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("MistakeId")]
    public virtual MistakeEntity Mistake { get; set; } = null!;
}
