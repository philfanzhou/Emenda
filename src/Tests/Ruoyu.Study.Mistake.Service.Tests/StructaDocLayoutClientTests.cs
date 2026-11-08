using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.Host.Services.Layout;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKQA2N client unit tests with a stub HTTP handler: request-shape guarantees, the pinned
/// status-plus-code failure classification, local pre-send validation with zero requests,
/// response contract validation, Retry-After handling, the disabled capability's zero-request
/// guarantee and cancellation propagation.
/// </summary>
public sealed class StructaDocLayoutClientTests
{
    private const string CloudExchangeDeadlineNote = "The deployed cloud exchange deadline is 120 seconds";

    private static readonly byte[] Png1x1 = BuildPng(1, 1);

    private static byte[] BuildPng(int width, int height)
    {
        var bytes = new byte[32];
        // Signature, IHDR length (13), "IHDR", big-endian dimensions.
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] BuildJpeg(int width, int height)
    {
        // SOI, APP0 (16 bytes), SOF0 with big-endian dimensions, EOI.
        var bytes = new byte[24];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 16);
        bytes[20] = 0xFF; bytes[21] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22, 2), 17);
        var frame = new byte[bytes.Length + 8];
        bytes.CopyTo(frame, 0);
        frame[bytes.Length] = 8; // Sample precision.
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(bytes.Length + 1, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(bytes.Length + 3, 2), (ushort)width);
        return frame;
    }

    private static string SuccessBody(string regions = """
        [{"id":0,"label":"text","x0":0.1,"y0":0.2,"x1":0.3,"y1":0.4},{"id":1,"label":"table","x0":0.4,"y0":0.1,"x1":0.9,"y1":0.6}]
        """)
        => $$"""
        {"requestId":"req-abc","width":1754,"height":1240,"coordinateFrame":"top-left-origin-normalized","regions":{{regions.Trim()}},"orientation":6,"engine":"mineru-cloud","model":"mineru2","version":"1.0","diagnostics":["note"]}
        """;

    private static (StructaDocLayoutClient Client, StubHandler Handler) CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        StructaDocLayoutOptions? options = null, bool enabled = true)
    {
        var handler = new StubHandler(send);
        var client = new StructaDocLayoutClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9/") },
            new LayoutCapabilityOptions(enabled),
            options ?? new StructaDocLayoutOptions { BaseUrl = "http://127.0.0.1:9/", ApiKey = "test-layout-secret-key-1" },
            NullLogger<StructaDocLayoutClient>.Instance);
        return (client, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        Json(status, $$"""{"title":"Layout detection failed","status":{{(int)status}},"detail":"x","code":"{{code}}"}""",
            "application/problem+json");

    private static async Task<LayoutDetectionException> FailureOf(StructaDocLayoutClient client, byte[]? image = null)
    {
        return await Assert.ThrowsAsync<LayoutDetectionException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage(image ?? Png1x1, "image/png")));
    }

    [Fact]
    public void DefaultBudgetAccommodatesTheCloudDeadlineAndBoundsMatchTheContract()
    {
        var options = new StructaDocLayoutOptions();
        // Parent criterion 3: the consumer HTTP budget must accommodate the cloud exchange
        // deadline (120 seconds after the execution slot); the default is 180 seconds.
        Assert.True(options.PerImageBudget > TimeSpan.FromSeconds(120), CloudExchangeDeadlineNote);
        Assert.Equal(180, options.PerImageBudgetSeconds);
        Assert.Equal(4, options.MaxConcurrentExchanges);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public async Task SuccessfulAnswerParsesOptionalMetadataAndPreservesRegionOrder()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())));
        var result = await client.DetectLayoutAsync(new LayoutDetectionImage(Png1x1, "image/png"));
        Assert.Equal("req-abc", result.RequestId);
        Assert.Equal(1754, result.Width);
        Assert.Equal(1240, result.Height);
        Assert.Equal(6, result.Orientation);
        Assert.Equal("mineru-cloud", result.Engine);
        Assert.Equal("mineru2", result.Model);
        Assert.Equal("1.0", result.Version);
        Assert.Equal(["note"], result.Diagnostics);
        Assert.Equal(2, result.Regions.Count);
        Assert.Equal(0, result.Regions[0].Id);
        Assert.Equal("text", result.Regions[0].Label);
        // Detection sequence preserved: region 1 (table) stays second.
        Assert.Equal(1, result.Regions[1].Id);
        Assert.Equal("table", result.Regions[1].Label);
        Assert.Equal(0.1, result.Regions[0].Box.X0, 12);
    }

    [Fact]
    public async Task ExplicitlyEmptyRegionsIsALegitimateAnswer()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, SuccessBody("[]"))));
        var result = await client.DetectLayoutAsync(new LayoutDetectionImage(Png1x1, "image/png"));
        Assert.Empty(result.Regions);
    }

    [Fact]
    public async Task MultipartCarriesExactlyOneFileSectionWithApiKeyAuthorizationAndMatchedType()
    {
        // Captured inside the handler: the client disposes its request after the exchange,
        // so assertions must run on values recorded while the request was alive.
        CapturedMultipart? captured = null;
        byte[] png = BuildPng(640, 480);
        (StructaDocLayoutClient client, _) = CreateClient(async (request, _) =>
        {
            captured = await CapturedMultipart.ReadAsync(request);
            return Json(HttpStatusCode.OK, SuccessBody("[]"));
        });
        await client.DetectLayoutAsync(new LayoutDetectionImage(png, "image/png"));

        Assert.NotNull(captured);
        Assert.Equal("ApiKey test-layout-secret-key-1", captured!.Authorization);
        Assert.Equal("POST", captured.Method);
        Assert.EndsWith("api/v1/layout/detect", captured.Uri);
        Assert.Equal("file", captured.FileSectionName);
        Assert.NotNull(captured.FileFileName);
        Assert.Equal("image/png", captured.FileContentType);
        // Original bytes transmitted unchanged: no auto-orient, resize or compression.
        Assert.Equal(png, captured.FileBytes);
        Assert.Equal(1, captured.SectionCount);
    }

    private sealed record CapturedMultipart(
        string Method,
        string Uri,
        string? Authorization,
        string? FileSectionName,
        string? FileFileName,
        string? FileContentType,
        byte[] FileBytes,
        int SectionCount)
    {
        public static async Task<CapturedMultipart> ReadAsync(HttpRequestMessage request)
        {
            string? fileSectionName = null;
            string? fileFileName = null;
            string? fileContentType = null;
            byte[] fileBytes = [];
            var sectionCount = 0;
            var content = (MultipartFormDataContent)request.Content!;
            foreach (var section in content)
            {
                sectionCount++;
                var disposition = section.Headers.ContentDisposition;
                if (disposition?.FileName is not null)
                {
                    fileSectionName = disposition.Name;
                    fileFileName = disposition.FileName;
                    fileContentType = section.Headers.ContentType?.MediaType;
                    fileBytes = await section.ReadAsByteArrayAsync();
                }
            }
            return new CapturedMultipart(
                request.Method.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                fileSectionName, fileFileName, fileContentType, fileBytes, sectionCount);
        }
    }

    [Theory]
    [InlineData(400, "layout-request-invalid", LayoutDetectionFailure.InvalidInput)]
    [InlineData(413, "layout-input-too-large", LayoutDetectionFailure.InvalidInput)]
    [InlineData(415, "layout-media-type-unsupported", LayoutDetectionFailure.InvalidInput)]
    [InlineData(429, "layout-overloaded", LayoutDetectionFailure.Overloaded)]
    [InlineData(503, "layout-capability-unsupported", LayoutDetectionFailure.UnsupportedCapability)]
    [InlineData(504, "layout-timeout", LayoutDetectionFailure.UpstreamTimeout)]
    [InlineData(502, "layout-output-invalid", LayoutDetectionFailure.InvalidOutput)]
    [InlineData(502, "layout-runtime-unavailable", LayoutDetectionFailure.RuntimeUnavailable)]
    [InlineData(503, "layout-runtime-unavailable", LayoutDetectionFailure.RuntimeUnavailable)]
    [InlineData(502, "layout-runtime-rejected", LayoutDetectionFailure.RuntimeRejected)]
    public async Task StatusAndProblemCodeAreClassifiedTogether(
        int status, string code, LayoutDetectionFailure expected)
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            Task.FromResult(Problem((HttpStatusCode)status, code)));
        var failure = await FailureOf(client);
        Assert.Equal(expected, failure.Failure);
        Assert.Equal(status, failure.HttpStatusCode);
        Assert.Equal(code, failure.ProblemCode);
        // Never retry every 5xx: only overload, upstream timeout and runtime unavailability
        // are transient among the classified server answers.
        var expectedTransience = expected is LayoutDetectionFailure.Overloaded
            or LayoutDetectionFailure.UpstreamTimeout
            or LayoutDetectionFailure.RuntimeUnavailable;
        Assert.Equal(expectedTransience, failure.IsTransient);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthenticationFailuresAreClassifiedByStatusAlone(int status)
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            Task.FromResult(Json((HttpStatusCode)status, "{}")));
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.Authentication, failure.Failure);
        Assert.False(failure.IsTransient);
    }

    [Theory]
    [InlineData(500, "layout-timeout")]        // Valid code on the wrong status.
    [InlineData(502, "layout-mystery")]        // Unknown code.
    [InlineData(429, null)]                    // Overload without the pinned code.
    [InlineData(503, null)]                    // No code at all on an error status.
    public async Task AnswersOutsideThePinnedTableAreUnexpectedAndNotRetried(int status, string? code)
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) => Task.FromResult(code is null
            ? Json((HttpStatusCode)status, "not json")
            : Problem((HttpStatusCode)status, code)));
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.UnexpectedResponse, failure.Failure);
        Assert.False(failure.IsTransient);
    }

    [Fact]
    public async Task OverloadRetryAfterDelaySecondsIsHonored()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
        {
            var response = Problem(HttpStatusCode.TooManyRequests, "layout-overloaded");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.Overloaded, failure.Failure);
        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
    }

    [Fact]
    public async Task OverloadRetryAfterHttpDateIsHonored()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
        {
            var response = Problem(HttpStatusCode.TooManyRequests, "layout-overloaded");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                DateTimeOffset.UtcNow.AddSeconds(30));
            return Task.FromResult(response);
        });
        var failure = await FailureOf(client);
        Assert.NotNull(failure.RetryAfter);
        Assert.InRange(failure.RetryAfter!.Value.TotalSeconds, 25, 30);
    }

    [Fact]
    public async Task InvalidJsonAnswerIsMalformedNeverAnEmptySuccess()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "not json")));
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.MalformedResponse, failure.Failure);
        Assert.False(failure.IsTransient);
    }

    [Theory]
    [InlineData("""{"requestId":"r","width":1754,"height":1240,"coordinateFrame":"bottom-left-origin","regions":[]}""", "coordinate frame")]
    [InlineData("""{"requestId":"r","width":1754,"height":1240,"coordinateFrame":"top-left-origin-normalized"}""", "regions array")]
    [InlineData("""{"requestId":"r","width":40000,"height":1,"coordinateFrame":"top-left-origin-normalized","regions":[]}""", "dimensions")]
    [InlineData("""{"requestId":"r","width":10,"height":10,"coordinateFrame":"top-left-origin-normalized","regions":[{"id":0,"label":"t","x0":0,"y0":0,"x1":2,"y1":1}]}""", "geometry contract")]
    [InlineData("""{"requestId":"r","width":10,"height":10,"coordinateFrame":"top-left-origin-normalized","regions":[{"id":0,"label":"t","x0":0,"y0":0,"x1":0.5,"y1":0.5},{"id":0,"label":"t","x0":0,"y0":0,"x1":0.2,"y1":0.2}]}""", "geometry contract")]
    [InlineData("""{"requestId":"r","width":10,"height":10,"coordinateFrame":"top-left-origin-normalized","regions":[{"id":0,"label":"t","x0":0.9,"y0":0,"x1":0.1,"y1":0.5}]}""", "geometry contract")]
    [InlineData("""{"requestId":"","width":10,"height":10,"coordinateFrame":"top-left-origin-normalized","regions":[]}""", "geometry contract")]
    public async Task ContractViolationsInSuccessAnswersAreStageErrors(string body, string expectedFragment)
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, body)));
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.MalformedResponse, failure.Failure);
        Assert.Contains(expectedFragment, failure.Message);
    }

    [Fact]
    public async Task OversizedDeclaredAnswerIsRejectedWithoutFullRead()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
        {
            var response = Json(HttpStatusCode.OK, "{}");
            response.Content.Headers.ContentLength = 8 * 1024 * 1024;
            return Task.FromResult(response);
        });
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.MalformedResponse, failure.Failure);
        Assert.Contains("response bound", failure.Message);
    }

    [Fact]
    public async Task TransportFailuresAreTransient()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, _) =>
            throw new HttpRequestException("connection refused"));
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.TransientTransport, failure.Failure);
        Assert.True(failure.IsTransient);
    }

    [Fact]
    public async Task DisabledCapabilityFailsClosedWithZeroRequests()
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())), enabled: false);
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.CapabilityDisabled, failure.Failure);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task EnabledWithoutCredentialIsAConfigurationFailureWithZeroRequests()
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())),
            options: new StructaDocLayoutOptions { BaseUrl = "http://127.0.0.1:9/" });
        var failure = await FailureOf(client);
        Assert.Equal(LayoutDetectionFailure.Configuration, failure.Failure);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("image/jpeg", "image/png")]   // Declared JPEG, actual PNG bytes.
    [InlineData("image/webp", "image/png")]   // Unsupported declared type.
    public async Task DeclaredMediaTypeMustMatchTheSignature(string declared, string _)
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())));
        var failure = await Assert.ThrowsAsync<LayoutDetectionException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage(Png1x1, declared)));
        Assert.Equal(LayoutDetectionFailure.InvalidInput, failure.Failure);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task UnknownSignatureIsRejectedLocallyWithZeroRequests()
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())));
        var failure = await FailureOf(client, "not an image at all, just text bytes"u8.ToArray());
        Assert.Equal(LayoutDetectionFailure.InvalidInput, failure.Failure);
        Assert.Contains("JPEG or a PNG", failure.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task EmptyImageIsRejectedLocally()
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())));
        var failure = await FailureOf(client, []);
        Assert.Equal(LayoutDetectionFailure.InvalidInput, failure.Failure);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ByteBoundIsEnforcedLocallyWithZeroRequests()
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())));
        var oversized = new byte[LayoutImageInspection.MaximumImageBytes + 1];
        Png1x1.AsSpan(0, 16).CopyTo(oversized);
        var failure = await FailureOf(client, oversized);
        Assert.Equal(LayoutDetectionFailure.InvalidInput, failure.Failure);
        Assert.Contains("layout detection limit", failure.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PixelBoundsAreEnforcedLocallyWithZeroRequests()
    {
        (StructaDocLayoutClient client, StubHandler handler) = CreateClient(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SuccessBody())));
        var failure = await FailureOf(client, BuildPng(40_000, 100));
        Assert.Equal(LayoutDetectionFailure.InvalidInput, failure.Failure);
        Assert.Contains("pixel bounds", failure.Message);
        Assert.Equal(0, handler.RequestCount);

        var totalPixels = await Assert.ThrowsAsync<LayoutDetectionException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage(BuildPng(20_000, 9_999), "image/png")));
        Assert.Equal(LayoutDetectionFailure.InvalidInput, totalPixels.Failure);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task HeaderInspectionReadsJpegStoredDimensions()
    {
        string? sectionMediaType = null;
        (StructaDocLayoutClient client, _) = CreateClient(async (request, _) =>
        {
            var section = Assert.IsType<MultipartFormDataContent>(request.Content).Single();
            sectionMediaType = section.Headers.ContentType?.MediaType;
            await section.ReadAsByteArrayAsync();
            return Json(HttpStatusCode.OK, SuccessBody("[]"));
        });
        var jpeg = BuildJpeg(1754, 1240);
        var result = await client.DetectLayoutAsync(new LayoutDetectionImage(jpeg, "image/jpeg"));
        Assert.Empty(result.Regions);
        Assert.Equal("image/jpeg", sectionMediaType);
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfClassifying()
    {
        (StructaDocLayoutClient client, _) = CreateClient((_, ct) =>
            Task.FromCanceled<HttpResponseMessage>(ct));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage(Png1x1, "image/png"), cancellation.Token));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return send(request, cancellationToken);
        }
    }
}
