using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("mistake")]
public class MistakeEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("student_id")]
    [Required]
    public string StudentId { get; set; } = string.Empty;

    [Column("subject")]
    public int Subject { get; set; }

    [Column("grade")]
    public int Grade { get; set; }

    [Column("date")]
    public DateTime Date { get; set; }

    [Column("image_paths")]
    public List<string> ImagePaths { get; set; } = new();

    [Column("content")]
    public string? Content { get; set; }

    [Column("correct_answer")]
    [MaxLength(500)]
    public string? CorrectAnswer { get; set; }

    [Column("student_answer")]
    [MaxLength(500)]
    public string? StudentAnswer { get; set; }

    [Column("type")]
    public int Type { get; set; }

    [Column("question_id")]
    public string? QuestionId { get; set; }

    [Column("comments")]
    public string? Comments { get; set; }

    [Column("status")]
    public int Status { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
