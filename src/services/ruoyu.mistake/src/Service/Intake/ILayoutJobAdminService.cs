namespace Ruoyu.Study.Mistake.Service.Intake;

/// <summary>The typed administrative layout failure (IKQA2P): a stable error kind plus
/// the HTTP-style status the administrative surface maps onto. Messages never contain
/// image bytes, the credential or raw provider text.</summary>
public sealed class LayoutAdminException(string errorKind, int statusCode = 409) : Exception(errorKind)
{
    public string ErrorKind { get; } = errorKind;
    public int StatusCode { get; } = statusCode;
}

public sealed record LayoutAdminRegion(int RegionId, string Label, double X0, double Y0, double X1, double Y1);

/// <summary>One accepted layout result on the administrative view: the consumer-owned
/// contract identity, upstream observation ids, dimensions, validated regions and the
/// exact retained byte identity it was accepted for.</summary>
public sealed record LayoutAdminResult(Guid ResultId, string ImagePath, int ResultRevision, int Attempt,
    string UpstreamRequestId, string ContractId, string CoordinateFrame, int Width, int Height, int? Orientation,
    string? Engine, string? Model, string? Version, int? SourceRotation, long BytesLength, string BytesSha256,
    DateTimeOffset CreatedAt, IReadOnlyList<string> Diagnostics, IReadOnlyList<LayoutAdminRegion> Regions);

/// <summary>One per-image durable work item: the retained byte identity the worker
/// re-verifies before every transmission, its status and bounded attempt state.</summary>
public sealed record LayoutAdminImage(string ImagePath, int Position, long ExpectedLength, string ExpectedSha256,
    string Status, int Attempts, string? LastError, DateTimeOffset? RetryNotBefore);

/// <summary>The durable state of one layout job for the administrative surface,
/// including the captured non-secret processing configuration. It never carries a
/// credential, a credential value or a signed URL.</summary>
public sealed record LayoutAdminJob(Guid JobId, Guid SourceUploadId, Guid ContentRevision, Guid SnapshotId,
    string StudentId, string Actor, int ResultRevision, string Status, string Config, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, string? ErrorKind, IReadOnlyList<LayoutAdminImage> Images,
    IReadOnlyList<LayoutAdminResult> Results);

/// <summary>
/// The administrative layout operations surface consumed by the HTTP endpoints (IKQA2P):
/// the enqueue command with request-key replay over an existing managed source revision
/// and the durable read views. Implemented by the host's layout job service family
/// (IKQA2O); reading stays available with the capability turned off, and a replay of
/// the same key always returns the original job with its captured configuration.
/// </summary>
public interface ILayoutJobAdminService
{
    /// <exception cref="LayoutAdminException">invalid_request, layout_disabled,
    /// source_not_managed, request_payload_conflict, invalid_source_manifest or
    /// no_layout_images.</exception>
    Task<LayoutAdminJob> EnqueueAsync(Guid sourceId, Guid revision, Guid requestKey, string actor,
        CancellationToken ct = default);

    /// <summary>Reads every job of one source revision ordered by result revision.</summary>
    Task<List<LayoutAdminJob>> GetJobsAsync(Guid sourceId, Guid revision, CancellationToken ct = default);
}
