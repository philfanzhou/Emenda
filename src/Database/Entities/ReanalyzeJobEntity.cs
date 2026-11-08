using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("reanalyze_job")]
public class ReanalyzeJobEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("item_id")]
    public Guid ItemId { get; set; } = Guid.Empty;

    [Column("reviewer_id")]
    [Required]
    [MaxLength(36)]
    public string ReviewerId { get; set; } = string.Empty;

    [Column("student_id")]
    [Required]
    [MaxLength(36)]
    public string StudentId { get; set; } = string.Empty;

    [Column("teacher_description")]
    public string? TeacherDescription { get; set; }

    [Column("previous_root_cause")]
    public string? PreviousRootCause { get; set; }

    [Column("status")]
    public int Status { get; set; } = 1; // ReanalyzeJobStatus.Pending

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("new_root_cause")]
    public string? NewRootCause { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("vl_duration_ms")]
    public long? VlDurationMs { get; set; }

    [Column("owner_id")]
    public Guid? OwnerId { get; set; }

    [Column("lease_until")]
    public DateTimeOffset? LeaseUntil { get; set; }

    [Column("fencing_version")]
    public long FencingVersion { get; set; }

    [Column("attempt")]
    public int Attempt { get; set; }

    [Column("execution_root_cause")]
    public string? ExecutionRootCause { get; set; }

    [Column("execution_input_digest")]
    [MaxLength(64)]
    public string? ExecutionInputDigest { get; set; }
}
