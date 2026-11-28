using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("mistake_item")]
public class MistakeItemEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("conversation_active")] public bool ConversationActive { get; set; }

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

    [Column("review_status")]
    public int ReviewStatus { get; set; }

    [Column("reviewer_id")]
    [MaxLength(36)]
    public string? ReviewerId { get; set; }

    [Column("reviewed_at")]
    public DateTimeOffset? ReviewedAt { get; set; }

    [Column("root_cause")]
    public string? RootCause { get; set; }

    // The unified source (schema v26 dropped the source-specific columns analysis_snapshot,
    // conversation_source and conversation_caller). Write the source through MistakeSource
    // (SetConversation, SetAnalysis, SetRetainedFiles); MistakeMaterializer completes the rest on every EF save:
    // the kind of a non-conversation row, the extraction task and caller, and the content images.

    /// <summary><see cref="MistakeSource.Extraction"/>, <see cref="MistakeSource.Conversation"/> or <see cref="MistakeSource.Legacy"/>.</summary>
    [Column("source_kind")]
    [Required]
    public string SourceKind { get; set; } = MistakeSource.Legacy;

    /// <summary>The owning product caller; NULL for legacy rows and unresolved deleted-task extractions.</summary>
    [Column("source_caller")]
    public string? SourceCaller { get; set; }

    /// <summary>The question as one JSON shape whose <c>images</c> match <see cref="SourceRegions"/> one to one.</summary>
    [Column("content", TypeName = "jsonb")]
    [Required]
    public string Content { get; set; } = MistakeSource.EmptyContent;

    /// <summary>Source-only data: an extraction's task and retained files, or the conversation evidence; NULL for legacy.</summary>
    [Column("source_detail", TypeName = "jsonb")]
    public string? SourceDetail { get; set; }

    // 注意：历史上存在 [Column("type")] 的"错误类型"字段（int 1-6 枚举），
    // 因无法覆盖全部错题场景已于 2026 年 6 月下线，相关 DB 列通过迁移文件删除。
    // 未来若需要 tag 能力，请通过新列承载而非复用此 tag 号。

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
