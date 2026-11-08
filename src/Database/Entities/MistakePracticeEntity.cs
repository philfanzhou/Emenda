using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("mistake_practice")]
public class MistakePracticeEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("mistake_item_id")]
    [Required]
    public Guid MistakeItemId { get; set; } = Guid.Empty;

    [Column("student_id")]
    [Required]
    [MaxLength(36)]
    public string StudentId { get; set; } = string.Empty;

    [Column("practice_date")]
    public DateTimeOffset PracticeDate { get; set; } = DateTimeOffset.UtcNow;

    [Column("image_paths")]
    public List<string> ImagePaths { get; set; } = new();

    [Column("student_answer")]
    public string? StudentAnswer { get; set; }

    [Column("is_correct")]
    public bool IsCorrect { get; set; }

    [Column("comments")]
    public string? Comments { get; set; }

    [ForeignKey("MistakeItemId")]
    public virtual MistakeItemEntity MistakeItem { get; set; } = null!;
}
