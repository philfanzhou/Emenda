namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>
/// The classified failure of one layout-detection exchange (IKQA2N). The classification is
/// derived from the HTTP status together with the Problem Details <c>code</c> exactly as the
/// pinned StructaDoc contract defines them; a 5xx status alone never decides the category, so
/// consumers must not retry every 5xx. Local pre-send validation and capability state use the
/// local categories.
/// </summary>
public enum LayoutDetectionFailure
{
    /// <summary>The Mistake:Layout:Enabled startup snapshot is false; the capability makes
    /// zero requests and reports a layout-stage failure instead.</summary>
    CapabilityDisabled,

    /// <summary>Enabled configuration is incomplete or invalid (missing base URL, credential
    /// or bounded settings).</summary>
    Configuration,

    /// <summary>The submitted image fails local or upstream request validation: unsupported
    /// signature, declared media type that does not match the signature, byte bound exceeded,
    /// pixel bounds exceeded, or upstream 400 layout-request-invalid / 413
    /// layout-input-too-large / 415 layout-media-type-unsupported.</summary>
    InvalidInput,

    /// <summary>Consumer authentication failed: HTTP 401 or 403.</summary>
    Authentication,

    /// <summary>Upstream overload: HTTP 429 layout-overloaded. <see cref="LayoutDetectionException.RetryAfter"/>
    /// carries the honored Retry-After hint when the upstream supplied one.</summary>
    Overloaded,

    /// <summary>The per-image budget expired or the upstream reported its own deadline:
    /// HTTP 504 layout-timeout.</summary>
    UpstreamTimeout,

    /// <summary>The selected upstream backend does not support the layout capability:
    /// HTTP 503 layout-capability-unsupported.</summary>
    UnsupportedCapability,

    /// <summary>The upstream runtime or its configuration is unavailable: HTTP 502/503
    /// layout-runtime-unavailable.</summary>
    RuntimeUnavailable,

    /// <summary>The upstream runtime refused the exchange itself: HTTP 502
    /// layout-runtime-rejected.</summary>
    RuntimeRejected,

    /// <summary>The upstream runtime produced malformed or oversized output: HTTP 502
    /// layout-output-invalid.</summary>
    InvalidOutput,

    /// <summary>A 200 answer that violates the pinned response contract: unparseable JSON,
    /// oversized body, wrong coordinate frame or invalid geometry. Never an empty successful
    /// layout.</summary>
    MalformedResponse,

    /// <summary>A transport-level failure before any answer: DNS, connection refused, reset.
    /// No status was received.</summary>
    TransientTransport,

    /// <summary>Any status and code pair outside the pinned contract, or an unclassifiable
    /// answer. Not a retry candidate.</summary>
    UnexpectedResponse,
}

/// <summary>
/// The typed layout-stage failure. Carries the classification, the HTTP status and Problem
/// Details code when an answer arrived, and the honored Retry-After hint for overload.
/// Messages contain only the status, the code and fixed wording — never image bytes, the
/// credential or raw provider response text.
/// </summary>
public sealed class LayoutDetectionException : Exception
{
    public LayoutDetectionException(LayoutDetectionFailure failure, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    public LayoutDetectionFailure Failure { get; }

    /// <summary>The honored Retry-After hint, present only for overload answers that supplied
    /// one (delay-seconds or HTTP-date form).</summary>
    public TimeSpan? RetryAfter { get; }

    public int? HttpStatusCode { get; init; }

    public string? ProblemCode { get; init; }

    /// <summary>True for the categories a consumer may legitimately retry after a bounded
    /// backoff: transport failures, overload (honoring <see cref="RetryAfter"/>), upstream
    /// timeouts and runtime unavailability. Authorization, unsupported capability, invalid
    /// input and malformed output are never transient, and unexpected answers are not retry
    /// candidates either.</summary>
    public bool IsTransient => Failure is LayoutDetectionFailure.Overloaded
        or LayoutDetectionFailure.UpstreamTimeout
        or LayoutDetectionFailure.RuntimeUnavailable
        or LayoutDetectionFailure.TransientTransport;
}
