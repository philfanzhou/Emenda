namespace Ruoyu.Study.Mistake.Service.Intake;

/// <summary>
/// Read-only legacy anomaly reconciliation over the mistake reference set and
/// its persisted state. The report classifies cross-resource anomalies into
/// decidable categories and previews repairs without executing any of them.
/// </summary>
public interface ISourceReconciliationService
{
    /// <param name="sourceUploadId">Optional scope filter; null reports the whole domain.</param>
    Task<ReconciliationReport> ReportAsync(Guid? sourceUploadId, CancellationToken ct = default);
}

/// <summary>The complete report; every category counts the full matching rows, entries are capped.</summary>
public sealed record ReconciliationReport(
    DateTimeOffset GeneratedAt,
    ReconciliationScope Scope,
    ReconciliationCategory<ReconciliationDanglingReference> DanglingReferences,
    ReconciliationCategory<ReconciliationConflict> PermanentConflicts,
    ReconciliationCategory<ReconciliationPending> PendingRecovery,
    ReconciliationCategory<ReconciliationUnmanaged> UnmanagedReferences,
    ReconciliationRepairPreview RepairPreview);

/// <summary>Scope of the run and the size of the parsed reference key set.</summary>
public sealed record ReconciliationScope(Guid? SourceUploadId, bool Filtered, int ReferenceKeyCount);

/// <summary>One anomaly category: full count, truncation flag and capped entries.</summary>
public sealed record ReconciliationCategory<TEntry>(int Count, bool Truncated, List<TEntry> Entries);

/// <summary>A referenced object key whose actual OSS read is missing or conflicts with persisted expectations.</summary>
public sealed record ReconciliationDanglingReference(
    string Key, string Reason, long? ExpectedLength, string? ExpectedSha256,
    bool ReferencesTruncated, List<ReconciliationReferenceOrigin> References);

/// <summary>The persisted row (table/row/column) that references a key.</summary>
public sealed record ReconciliationReferenceOrigin(string Table, string Row, string Column);

/// <summary>A permanent Conflict record (handoff or return) that only an owner decision can unseal.</summary>
public sealed record ReconciliationConflict(
    string Kind, Guid SourceUploadId, Guid ContentRevision, Guid? ItemId, string OperationId,
    string? ErrorKind, DateTimeOffset? SealedAt);

/// <summary>A non-terminal durable state an existing worker or admin entry point can still advance.</summary>
public sealed record ReconciliationPending(
    string Kind, Guid? SourceUploadId, Guid? ContentRevision, Guid? ItemId, string State,
    DateTimeOffset? Since, long? AgeSeconds);

/// <summary>An item/practice record or key that cannot be attributed to the managed source chain; classification unknown.</summary>
public sealed record ReconciliationUnmanaged(string Kind, string Row, string SourceUploadId, List<string> Keys);

/// <summary>Repair preview for unmanaged references: only the difference inventory and the required inputs.</summary>
public sealed record ReconciliationRepairPreview(string Classification, int UnmanagedCount, IReadOnlyList<string> RequiredInputs);
