using System;

namespace Ruoyu.Study.Mistake.Domain.Models;

/// <summary>
/// Reanalyze job lifecycle status. The state machine is:
///   Pending -> Running -> Completed | Failed
///   Expired Running can be reclaimed with a new owner and fencing version.
/// Cancelled is intentionally not supported (per product decision).
/// Renamed from ReanalyzeJobStatus to avoid name collision with the
/// proto-generated enum in Ruoyu.Study.Mistake.Contract.Protos.
/// </summary>
public enum ReanalyzeJobState
{
    Unspecified = 0,
    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

public class ReanalyzeJobModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; } = Guid.Empty;
    public string ReviewerId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string? TeacherDescription { get; set; }
    public string? PreviousRootCause { get; set; }
    public ReanalyzeJobState Status { get; set; } = ReanalyzeJobState.Pending;
    public string? ErrorMessage { get; set; }
    public string? NewRootCause { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? VlDurationMs { get; set; }
    public Guid? OwnerId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long FencingVersion { get; set; }
    public int Attempt { get; set; }
    public string? ExecutionRootCause { get; set; }
    public string? ExecutionInputDigest { get; set; }
    // Populated only when a worker claims the job. Never stored in the job table.
    public IReadOnlyList<string> ExecutionImagePaths { get; set; } = Array.Empty<string>();
    public string? ExecutionStudentId { get; set; }
}
