using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("mistake_detail")]
public class MistakeDetailEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("mistake_id")]
    [Required]
    public Guid MistakeId { get; set; }

    [Column("original_images")]
    public List<string> OriginalImages { get; set; } = new();

    [Column("comments")]
    public string? Comments { get; set; }

    [ForeignKey("MistakeId")]
    public virtual MistakeEntity Mistake { get; set; } = null!;
}
