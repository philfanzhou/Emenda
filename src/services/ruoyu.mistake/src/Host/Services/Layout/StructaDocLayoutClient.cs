using System.Net.Http.Headers;
using System.Text.Json;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;

namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>
/// The Mistake-owned typed client for StructaDoc's dedicated non-archiving layout endpoint
/// (IKQA2N), pinned to the v0.1.13 <c>POST /api/v1/layout/detect</c> contract.
///
/// One call is one bounded synchronous exchange: multipart with exactly one <c>file</c>
/// section carrying the unchanged original bytes, <c>Authorization: ApiKey</c> with the
/// scoped deployment credential (no end-user tokens are forwarded; no caller-supplied URL or
/// credential exists), a configurable per-image budget enforced through a linked cancellation
/// token — the HttpClient itself is registered with an infinite timeout so the framework's
/// 100-second default can never cut a cloud exchange whose own deadline is 120 seconds — and
/// a process-wide concurrency gate. Responses are bounded and validated against the pinned
/// schema; invalid or incompatible output is a stage error, never an empty successful layout.
/// The client never falls back to StructaDoc's Document/Parse Run endpoints or any commercial
/// API, and logs only classifications, statuses, codes and counts — never the credential,
/// image bytes or raw provider responses.
/// </summary>
public sealed class StructaDocLayoutClient : ILayoutDetectionClient
{
    public const string HttpClientName = "structadoc-layout";

    /// <summary>The never-resolving placeholder a disabled capability points its client at,
    /// matching the existing disabled-capability precedent.</summary>
    public const string DisabledPlaceholderBaseUrl = "http://structadoc-layout-disabled.invalid";

    private const string DetectRelativeUrl = "api/v1/layout/detect";

    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly LayoutCapabilityOptions _capability;
    private readonly StructaDocLayoutOptions _options;
    private readonly SemaphoreSlim _exchangeGate;
    private readonly ILogger<StructaDocLayoutClient> _logger;

    public StructaDocLayoutClient(
        HttpClient httpClient,
        LayoutCapabilityOptions capability,
        StructaDocLayoutOptions options,
        ILogger<StructaDocLayoutClient> logger)
    {
        _httpClient = httpClient;
        _capability = capability;
        _options = options;
        _exchangeGate = new SemaphoreSlim(
            Math.Clamp(options.MaxConcurrentExchanges, 1, 16), Math.Clamp(options.MaxConcurrentExchanges, 1, 16));
        _logger = logger;
    }

    public async Task<LayoutDetectionResult> DetectLayoutAsync(
        LayoutDetectionImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!_capability.Enabled)
            throw new LayoutDetectionException(
                LayoutDetectionFailure.CapabilityDisabled,
                "The Mistake layout capability is disabled; no layout request was sent.");
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new LayoutDetectionException(
                LayoutDetectionFailure.Configuration,
                "The layout capability is enabled but its base URL or scoped credential is not configured.");

        cancellationToken.ThrowIfCancellationRequested();
        var inspection = InspectImage(image);

        // The concurrency gate bounds the exchanges this process holds; the deployed cloud
        // adapter allows four. Waiting for a slot honors caller cancellation.
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.PerImageBudget);
            try
            {
                using var response = await SendAsync(inspection, image.Bytes, budget.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var result = await ReadSuccessAsync(response, budget.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "StructaDoc layout detection succeeded: RequestId={RequestId}, Width={Width}, Height={Height}, Orientation={Orientation}, Regions={RegionCount}",
                        result.RequestId, result.Width, result.Height, result.Orientation?.ToString() ?? "none", result.Regions.Count);
                    return result;
                }
                throw await ClassifyFailureAsync(response, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the budget token or the HttpClient can cancel here while the caller is
                // still live: the per-image budget expired. The HttpClient itself is infinite,
                // so this is the budget, and it is an upstream-timeout classification.
                throw Failure(
                    LayoutDetectionFailure.UpstreamTimeout,
                    $"The per-image layout budget of {_options.PerImageBudgetSeconds} seconds expired before StructaDoc answered.");
            }
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    private (string MediaType, int Width, int Height) InspectImage(LayoutDetectionImage image)
    {
        if (!LayoutImageInspection.TryInspect(image.Bytes, out var mediaType, out var width, out var height, out var rejection))
            throw Failure(LayoutDetectionFailure.InvalidInput, rejection!);
        if (image.DeclaredMediaType is { } declared &&
            !string.Equals(declared.Trim(), mediaType, StringComparison.OrdinalIgnoreCase))
            throw Failure(
                LayoutDetectionFailure.InvalidInput,
                $"The declared media type '{declared}' does not match the submitted {mediaType} image.");
        return (mediaType, width, height);
    }

    private async Task<HttpResponseMessage> SendAsync(
        (string MediaType, int Width, int Height) inspection, byte[] bytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DetectRelativeUrl);
        // The scoped deployment credential only. End-user tokens are never forwarded, and the
        // credential never leaves this header.
        request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", _options.ApiKey);
        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(inspection.MediaType);
        // Exactly one file section named "file", matching the pinned contract; no form fields.
        content.Add(fileContent, "file", "image");
        request.Content = content;
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                LayoutDetectionFailure.TransientTransport,
                "StructaDoc layout detection failed at the transport level before any answer.");
        }
    }

    private async Task<LayoutDetectionResult> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        WireLayoutResponse wire;
        try
        {
            wire = JsonSerializer.Deserialize<WireLayoutResponse>(body, WireJsonOptions)
                ?? throw Failure(LayoutDetectionFailure.MalformedResponse, "The layout answer is not a JSON object.");
        }
        catch (JsonException)
        {
            throw Failure(LayoutDetectionFailure.MalformedResponse, "The layout answer is not valid JSON.");
        }
        if (!string.Equals(wire.CoordinateFrame, LayoutGeometry.CoordinateFrame, StringComparison.Ordinal))
            throw Failure(
                LayoutDetectionFailure.MalformedResponse,
                $"The layout answer uses the unsupported coordinate frame '{wire.CoordinateFrame}'.");
        if (wire.Regions is null)
            throw Failure(
                LayoutDetectionFailure.MalformedResponse,
                "The layout answer carries no regions array; an accepted empty answer states the array explicitly.");
        if (wire.Width > LayoutImageInspection.MaximumDimensionPixels ||
            wire.Height > LayoutImageInspection.MaximumDimensionPixels ||
            (long)wire.Width * wire.Height > LayoutImageInspection.MaximumTotalPixels)
            throw Failure(LayoutDetectionFailure.MalformedResponse, "The layout answer reports dimensions beyond the contract bounds.");

        var result = new LayoutDetectionResult
        {
            RequestId = wire.RequestId ?? string.Empty,
            Width = wire.Width,
            Height = wire.Height,
            Orientation = wire.Orientation,
            Engine = wire.Engine,
            Model = wire.Model,
            Version = wire.Version,
            Diagnostics = wire.Diagnostics ?? [],
            Regions = (wire.Regions ?? []).Select(r => new LayoutRegion(
                r.Id, r.Label ?? string.Empty, new NormalizedLayoutBox(r.X0, r.Y0, r.X1, r.Y1))).ToArray(),
        };
        var errors = LayoutGeometry.ValidateLayoutResult(result);
        if (errors.Count > 0)
            throw Failure(
                LayoutDetectionFailure.MalformedResponse,
                "The layout answer violates the geometry contract: " + string.Join(" ", errors.Take(3)));
        return result;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > _options.MaxResponseBytes)
            throw Failure(
                LayoutDetectionFailure.MalformedResponse,
                $"The layout answer exceeds the configured {_options.MaxResponseBytes}-byte response bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > _options.MaxResponseBytes)
                throw Failure(
                    LayoutDetectionFailure.MalformedResponse,
                    $"The layout answer exceeds the configured {_options.MaxResponseBytes}-byte response bound.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private async Task<LayoutDetectionException> ClassifyFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        // Consumer authentication failures are decided by status alone: the platform's 401/403
        // answers do not carry the layout problem code.
        if (status is 401 or 403)
            return Failure(
                LayoutDetectionFailure.Authentication,
                $"StructaDoc rejected the layout credential with HTTP {status}.",
                status: status);

        string? code = null;
        try
        {
            var body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.String)
                code = codeElement.GetString();
        }
        catch (Exception exception) when (exception is JsonException or LayoutDetectionException or OperationCanceledException)
        {
            // An unparseable failure body keeps the null code; the status still classifies
            // below through the strict table.
        }
        var retryAfter = status == 429 ? ParseRetryAfter(response) : null;
        var (failure, description) = (status, code) switch
        {
            (400, "layout-request-invalid") => (LayoutDetectionFailure.InvalidInput, "the request was rejected as invalid"),
            (413, "layout-input-too-large") => (LayoutDetectionFailure.InvalidInput, "the image exceeds the upstream input bounds"),
            (415, "layout-media-type-unsupported") => (LayoutDetectionFailure.InvalidInput, "the image media type is unsupported upstream"),
            (429, "layout-overloaded") => (LayoutDetectionFailure.Overloaded, "the upstream is overloaded"),
            (503, "layout-capability-unsupported") => (LayoutDetectionFailure.UnsupportedCapability, "the upstream backend does not support the layout capability"),
            (504, "layout-timeout") => (LayoutDetectionFailure.UpstreamTimeout, "the upstream deadline expired"),
            (502, "layout-output-invalid") => (LayoutDetectionFailure.InvalidOutput, "the upstream runtime produced invalid output"),
            (502, "layout-runtime-unavailable") => (LayoutDetectionFailure.RuntimeUnavailable, "the upstream runtime is unavailable"),
            (503, "layout-runtime-unavailable") => (LayoutDetectionFailure.RuntimeUnavailable, "the upstream runtime is unavailable"),
            (502, "layout-runtime-rejected") => (LayoutDetectionFailure.RuntimeRejected, "the upstream runtime rejected the exchange"),
            _ => ((LayoutDetectionFailure?)null, (string?)null),
        };
        if (failure is null)
            return Failure(
                LayoutDetectionFailure.UnexpectedResponse,
                $"StructaDoc answered HTTP {status} with code '{code ?? "none"}', outside the pinned layout contract.",
                status: status,
                code: code);
        _logger.LogWarning(
            "StructaDoc layout detection failed: Status={Status}, Code={Code}, Failure={Failure}",
            status, code ?? "none", failure.Value);
        return Failure(
            failure.Value,
            $"StructaDoc layout detection failed: {description} (HTTP {status}, code {code}).",
            retryAfter,
            status,
            code);
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
        return null;
    }

    private static LayoutDetectionException Failure(
        LayoutDetectionFailure failure,
        string message,
        TimeSpan? retryAfter = null,
        int? status = null,
        string? code = null) =>
        new(failure, message, retryAfter) { HttpStatusCode = status, ProblemCode = code };

    private sealed record WireLayoutRegion(int Id, string? Label, double X0, double Y0, double X1, double Y1);

    private sealed record WireLayoutResponse
    {
        public string? RequestId { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public string? CoordinateFrame { get; init; }

        public IReadOnlyList<WireLayoutRegion>? Regions { get; init; }

        public int? Orientation { get; init; }

        public string? Engine { get; init; }

        public string? Model { get; init; }

        public string? Version { get; init; }

        public IReadOnlyList<string>? Diagnostics { get; init; }
    }
}
