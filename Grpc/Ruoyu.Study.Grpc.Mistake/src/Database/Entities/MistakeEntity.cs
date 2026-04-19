using System;
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

    [Column("type")]
    public int Type { get; set; }

    [Column("question_id")]
    public string? QuestionId { get; set; }

    [Column("status")]
    public int Status { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
