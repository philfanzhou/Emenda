using Emenda.Domain.Models;

namespace Emenda.Host.Services.Layout;

/// <summary>One per-image work item of a layout job as exposed on views. The expected
/// byte identity is the retained manifest truth the worker re-verifies before every
/// transmission.</summary>
public sealed record LayoutJobImageView(string ImagePath, int Position, long ExpectedLength, string ExpectedSha256,
    string Status, int Attempts, string? LastError, DateTimeOffset? RetryNotBefore);

/// <summary>One validated region of an accepted layout result: the response-local id
/// (the preserved upstream detection sequence), the generic label and the normalized box
/// in the stored pixel frame.</summary>
public sealed record LayoutResultRegionView(int RegionId, string Label, double X0, double Y0, double X1, double Y1);

/// <summary>One accepted layout result, self-contained: upstream request id, the
/// consumer-owned contract identifier, engine/model/version observations, image
/// dimensions, coordinate frame, validated regions, bounded diagnostics, exact byte
/// identity, relevant source rotation and the captured processing configuration.</summary>
public sealed record LayoutResultView(Guid ResultId, Guid JobId, string ImagePath, int ResultRevision, int Attempt,
    string UpstreamRequestId, string ContractId, string CoordinateFrame, int Width, int Height, int? Orientation,
    string? Engine, string? Model, string? Version, int? SourceRotation, long BytesLength, string BytesSha256,
    string Config, DateTimeOffset CreatedAt, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<LayoutResultRegionView> Regions);

/// <summary>The full durable state of one layout job.</summary>
public sealed record LayoutJobView(Guid JobId, Guid SourceUploadId, Guid ContentRevision, Guid SnapshotId,
    string StudentId, string Actor, int ResultRevision, string Status, string Config, string CredentialReference,
    string ImageRotations, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorKind,
    IReadOnlyList<LayoutJobImageView> Images, IReadOnlyList<LayoutResultView> Results);

/// <summary>A claimed job generation: the claim identity fencing every fenced write.</summary>
public sealed record LayoutJobClaim(Guid JobId, Guid Owner, long Fence, LayoutJobView Job);

/// <summary>
/// The layout job application surface (IKQA2O): the enqueue command with request-key
/// replay, durable read views, and the lease/heartbeat/fencing primitives the execution
/// worker drives. Inference itself never runs inside these methods: the worker downloads
/// and byte-verifies images and calls <see cref="ILayoutDetectionClient"/> outside any
/// database transaction, then publishes through the fenced methods.
/// </summary>
public interface ILayoutJobService
{
    /// <summary>Creates (or replays) a layout job for an existing managed source upload
    /// revision. A reused request key with the same caller/action/source returns the same
    /// job and its captured non-secret configuration; a conflicting input is rejected. A
    /// deliberate rerun uses a new key and produces the next separate result
    /// revision.</summary>
    /// <exception cref="LayoutJobException">invalid_request, layout_disabled,
    /// source_not_managed, request_payload_conflict, invalid_source_manifest or
    /// no_layout_images.</exception>
    Task<LayoutJobView> EnqueueAsync(Guid sourceId, Guid revision, Guid requestKey, string actor,
        CancellationToken ct = default);

    /// <summary>Reads one job with its images and accepted results. Reading stays
    /// available with the capability turned off; existing results are always
    /// retained.</summary>
    Task<LayoutJobView?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Reads every job of one source revision, ordered by result revision.</summary>
    Task<List<LayoutJobView>> GetJobsAsync(Guid sourceId, Guid revision, CancellationToken ct = default);

    /// <summary>Claims the next eligible job (queued, or running with an expired lease)
    /// and returns its claim identity, or null when no work is claimable.</summary>
    Task<LayoutJobClaim?> ClaimAsync(CancellationToken ct = default);

    /// <summary>Extends the claim's lease. False means the claim is no longer the current
    /// owner (lease lost, superseded or completed); the caller must stop working.</summary>
    Task<bool> RenewLeaseAsync(Guid jobId, Guid owner, long fence, CancellationToken ct = default);

    /// <summary>Publishes one accepted inference answer for a claimed image. The write is
    /// fenced by owner, fencing version and lease: a stale attempt cannot publish after
    /// lease loss, and the first accepted result per job image wins — a late duplicate is
    /// discarded. Returns false when the claim was stale or an accepted result already
    /// existed for a different generation.</summary>
    Task<bool> PublishResultAsync(LayoutJobClaim claim, string imagePath, LayoutDetectionResult result,
        CancellationToken ct = default);

    /// <summary>Records one failed attempt for a claimed image. Retryable failures keep
    /// the image eligible under the bounded backoff (honoring an upstream Retry-After
    /// hint); permanent failures exhaust the attempt budget immediately. Fenced like
    /// publishing: a stale attempt records nothing.</summary>
    Task<bool> RecordImageFailureAsync(LayoutJobClaim claim, string imagePath, string errorKind, bool retryable,
        TimeSpan? retryAfter, CancellationToken ct = default);

    /// <summary>Closes a claimed job: Succeeded when every image has an accepted result,
    /// otherwise Failed with the first recorded error kind. Results already published
    /// stay visible and retained either way. Fenced.</summary>
    Task<bool> CompleteJobAsync(LayoutJobClaim claim, CancellationToken ct = default);

    /// <summary>Releases the claim and requeues the job without a verdict: used for
    /// pending per-image backoffs and for parking work when the capability is turned
    /// off. Fenced.</summary>
    Task<bool> ReleaseAsync(LayoutJobClaim claim, CancellationToken ct = default);
}
