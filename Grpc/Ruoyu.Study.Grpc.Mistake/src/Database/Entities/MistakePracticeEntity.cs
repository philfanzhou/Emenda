using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("mistake_practice")]
public class MistakePracticeEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("mistake_id")]
    [Required]
    public Guid MistakeId { get; set; } = Guid.Empty;

    [Column("student_id")]
    [Required]
    public string StudentId { get; set; } = string.Empty;

    [Column("practice_date")]
    public DateTime PracticeDate { get; set; } = DateTime.UtcNow;

    [Column("image_paths")]
    public List<string> ImagePaths { get; set; } = new();

    [Column("student_answer")]
    public string? StudentAnswer { get; set; }

    [Column("is_correct")]
    public bool IsCorrect { get; set; }

    [Column("comments")]
    public string? Comments { get; set; }

    [ForeignKey("MistakeId")]
    public virtual MistakeEntity Mistake { get; set; } = null!;
}
