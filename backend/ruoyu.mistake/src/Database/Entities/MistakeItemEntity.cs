using System;
using System.Collections.Generic;
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

    [Column("source_regions")]
    [Required]
    public List<SourceRegionEntry> SourceRegions { get; set; } = new();

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

public class SourceRegionEntry
{
    public string SourceImagePath { get; set; } = string.Empty;
    public BoundingBoxEntry? BoundingBox { get; set; }
}

public class BoundingBoxEntry
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
}
