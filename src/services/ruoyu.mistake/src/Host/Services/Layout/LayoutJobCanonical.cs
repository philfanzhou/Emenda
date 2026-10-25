using System.Text.Json;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;

namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>
/// Pure logic of the layout job pipeline (IKQA2O): the request canonical form, the
/// captured non-secret processing configuration, bounded diagnostics, the per-image
/// retry eligibility and backoff, and the relevant-source-rotation lookup. No I/O, no
/// clock: time-dependent decisions take the current timestamp as an argument.
/// </summary>
public static class LayoutJobCanonical
{
    public const string Version = "mistake-layout-v1";

    /// <summary>The consumer-owned contract identifier stamped on every accepted layout
    /// result. It names the local accepted-result contract (distinct from the upstream
    /// request id) so later consumers can version the persisted shape.</summary>
    public const string ContractId = "mistake-layout-v1";

    /// <summary>The credential reference stored on layout jobs: the configuration key
    /// name that holds the scoped StructaDoc credential. The credential value itself is
    /// never persisted in jobs, results, payloads or logs.</summary>
    public const string CredentialReference = "Mistake:Layout:ApiKey";

    public const int MaxAttemptsPerImage = 5;
    public const int MaxDiagnostics = 8;
    public const int MaxDiagnosticLength = 200;
    public const int MaxErrorKindLength = 100;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Pending = "Pending";

    /// <summary>The canonical input of one enqueue command: the contract version plus
    /// the source upload and content revision. The captured processing configuration is
    /// part of the replayed job, not of the request identity, so a later configuration
    /// change never conflicts a replay.</summary>
    public static string Request(Guid source, Guid revision) =>
        IntakeCanonical.Encode(Version, source.ToString("D"), revision.ToString("D"));

    /// <summary>The non-secret consumer processing configuration captured when a job is
    /// created. Deliberately excludes the credential; the stored credential reference
    /// names only its configuration key.</summary>
    public static string CaptureConfig(StructaDocLayoutOptions options) => JsonSerializer.Serialize(new LayoutProcessingConfig
    {
        ContractId = ContractId,
        CredentialReference = CredentialReference,
        CoordinateFrame = LayoutGeometry.CoordinateFrame,
        BaseUrl = options.BaseUrl ?? string.Empty,
        PerImageBudgetSeconds = options.PerImageBudgetSeconds,
        MaxConcurrentExchanges = options.MaxConcurrentExchanges,
        MaxResponseBytes = options.MaxResponseBytes
    }, Json);

    /// <summary>Bounded diagnostics: at most <see cref="MaxDiagnostics"/> entries, each
    /// truncated to <see cref="MaxDiagnosticLength"/> characters.</summary>
    public static IReadOnlyList<string> BoundDiagnostics(IReadOnlyList<string> diagnostics) =>
        diagnostics.Take(MaxDiagnostics)
            .Select(entry => entry.Length <= MaxDiagnosticLength ? entry : entry[..MaxDiagnosticLength])
            .ToList();

    /// <summary>Bounded error kind recorded per image.</summary>
    public static string BoundErrorKind(string errorKind) =>
        errorKind.Length <= MaxErrorKindLength ? errorKind : errorKind[..MaxErrorKindLength];

    /// <summary>The retry delay after the given failed attempt. An honored upstream
    /// Retry-After hint wins when it is longer; otherwise the bounded exponential
    /// backoff applies. The result is clamped to [1 s, 1 h].</summary>
    public static TimeSpan Backoff(int attempts, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, Math.Max(attempts - 1, 0)), 600));
        var delay = retryAfter is { } hint && hint > exponential ? hint : exponential;
        return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
            : delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
    }

    /// <summary>One image may still be retried later when it has no accepted result yet
    /// and its attempt budget is not exhausted. This includes images whose backoff has
    /// not elapsed yet; it decides whether a job generation closes or requeues.</summary>
    public static bool MayRetryLater(string status, int attempts) =>
        status != Succeeded && attempts < MaxAttemptsPerImage;

    /// <summary>One image is eligible for a new inference attempt right now when it may
    /// retry at all and its retry backoff has elapsed. Completed images never run
    /// again.</summary>
    public static bool IsEligible(string status, int attempts, DateTimeOffset? retryNotBefore, DateTimeOffset now) =>
        MayRetryLater(status, attempts) && (retryNotBefore is null || retryNotBefore <= now);

    /// <summary>True when at least one image still has eligible work now.</summary>
    public static bool HasEligibleWork(IReadOnlyList<(string Status, int Attempts, DateTimeOffset? RetryNotBefore)> images,
        DateTimeOffset now) =>
        images.Any(image => IsEligible(image.Status, image.Attempts, image.RetryNotBefore, now));

    /// <summary>The relevant source rotation of one image: the snapshot's independent
    /// rotation list is index-aligned with the manifest images; when the lengths do not
    /// agree the rotation is not attributed to any image. The values are metadata only
    /// and are never composed into geometry transforms (LayoutGeometry contract).</summary>
    public static int? SourceRotationOf(IReadOnlyList<int> rotations, int manifestIndex) =>
        rotations.Count > 0 && manifestIndex >= 0 && manifestIndex < rotations.Count ? rotations[manifestIndex] : null;
}

/// <summary>The captured, non-secret consumer processing configuration of a layout job:
/// what the job will run with, replayed with the job. Never includes the credential.</summary>
public sealed record LayoutProcessingConfig
{
    public string ContractId { get; init; } = "";
    public string CredentialReference { get; init; } = "";
    public string CoordinateFrame { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public int PerImageBudgetSeconds { get; init; }
    public int MaxConcurrentExchanges { get; init; }
    public int MaxResponseBytes { get; init; }
}
