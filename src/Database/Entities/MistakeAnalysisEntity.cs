using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("mistake_analysis")]
public sealed class MistakeAnalysisEntity
{
    [Key, Column("mistake_item_id")]
    public Guid MistakeItemId { get; set; }
    [Column("revision")]
    public long Revision { get; set; }
    [Column("draft_report", TypeName = "jsonb")]
    public string? DraftReport { get; set; }
    [Column("confirmed_report", TypeName = "jsonb")]
    public string? ConfirmedReport { get; set; }
    [Column("teacher_feedback", TypeName = "jsonb")]
    public string TeacherFeedback { get; set; } = "[]";
    [Column("draft_job_id")]
    public Guid? DraftJobId { get; set; }
    [Column("confirmed_job_id")]
    public Guid? ConfirmedJobId { get; set; }
    [Column("input_digest")]
    public string? InputDigest { get; set; }
    [Column("generated_at")]
    public DateTimeOffset? GeneratedAt { get; set; }
    [Column("model")]
    public string? Model { get; set; }
    [Column("prompt_version")]
    public string? PromptVersion { get; set; }
    [Column("confirmed_by")]
    public string? ConfirmedBy { get; set; }
    [Column("confirmed_at")]
    public DateTimeOffset? ConfirmedAt { get; set; }
}
