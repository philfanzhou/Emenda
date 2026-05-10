using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("mistake_item")]
public class MistakeItemEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("student_id")]
    [Required]
    [MaxLength(36)]
    public string StudentId { get; set; } = string.Empty;

    [Column("subject")]
    public int Subject { get; set; }

    [Column("grade")]
    public int Grade { get; set; }

    [Column("source_upload_id")]
    [Required]
    [MaxLength(64)]
    public string SourceUploadId { get; set; } = string.Empty;

    [Column("source_image_path")]
    [Required]
    public string SourceImagePath { get; set; } = string.Empty;

    [Column("bbox_x1")]
    public int? BboxX1 { get; set; }

    [Column("bbox_y1")]
    public int? BboxY1 { get; set; }

    [Column("bbox_x2")]
    public int? BboxX2 { get; set; }

    [Column("bbox_y2")]
    public int? BboxY2 { get; set; }

    [Column("question_number")]
    [MaxLength(20)]
    public string? QuestionNumber { get; set; }

    [Column("question_text")]
    public string? QuestionText { get; set; }

    [Column("student_answer")]
    public string? StudentAnswer { get; set; }

    [Column("ai_confidence")]
    public double AiConfidence { get; set; }

    [Column("review_status")]
    public int ReviewStatus { get; set; }

    [Column("reviewer_id")]
    [MaxLength(36)]
    public string? ReviewerId { get; set; }

    [Column("reviewed_at")]
    public DateTimeOffset? ReviewedAt { get; set; }

    [Column("review_comment")]
    public string? ReviewComment { get; set; }

    [Column("type")]
    public int Type { get; set; }

    [Column("question_id")]
    [MaxLength(36)]
    public string? QuestionId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset? UpdatedAt { get; set; }
}
