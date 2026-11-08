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

    [Column("mistake_item_id")]
    public Guid MistakeItemId { get; set; }

    [Column("student_id")]
    [MaxLength(36)]
    public string? StudentId { get; set; }

    [Column("action")]
    public int Action { get; set; }

    [Column("details")]
    public string? Details { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey("MistakeItemId")]
    public virtual MistakeItemEntity MistakeItem { get; set; } = null!;
}
