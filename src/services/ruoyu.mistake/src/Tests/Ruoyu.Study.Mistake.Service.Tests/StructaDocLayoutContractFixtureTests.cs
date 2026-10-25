using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.Host.Services.Layout;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKQA2N contract-fixture tests. The pinned StructaDoc v0.1.13 layout document is the
/// consumer-side contract: these tests assert the fixture pins the operations and fields the
/// client relies on (parent criterion 3) — authentication failures, the error statuses with
/// their exact Problem Details codes, optional response metadata with no wire schema-version
/// field, and the legitimate empty result — and cross-check every pinned status-plus-code
/// pair against the client's actual classification.
/// </summary>
public sealed class StructaDocLayoutContractFixtureTests
{
    private static readonly Lazy<JsonDocument> Fixture = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "structadoc-layout-v0.1.13-openapi.json");
        using var stream = File.OpenRead(path);
        return JsonDocument.Parse(stream);
    }, isThreadSafe: true);

    private static JsonElement Operation => Fixture.Value.RootElement
        .GetProperty("paths").GetProperty("/api/v1/layout/detect").GetProperty("post");

    [Fact]
    public void FixtureIsPinnedToStructaDocV0113()
    {
        var pin = Fixture.Value.RootElement.GetProperty("x-ruoyu-pin");
        Assert.Equal("StructaDoc", pin.GetProperty("upstream").GetString());
        Assert.Equal("v0.1.13", pin.GetProperty("tag").GetString());
        Assert.Equal("6ec800ca729307fd3ac1ccfa8776e6877aef3140", pin.GetProperty("commit").GetString());
        Assert.NotEmpty(pin.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public void OperationRequiresTheApiKeySchemeOnTheAuthorizationHeader()
    {
        var scheme = Operation.GetProperty("security")[0].EnumerateObject().Single();
        Assert.Equal("ApiKey", scheme.Name);
        var securityScheme = Fixture.Value.RootElement.GetProperty("components")
            .GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", securityScheme.GetProperty("type").GetString());
        Assert.Equal("header", securityScheme.GetProperty("in").GetString());
        Assert.Equal("Authorization", securityScheme.GetProperty("name").GetString());
    }

    [Fact]
    public void RequestBodyIsMultipartWithExactlyOneFileSectionAndNoOtherFields()
    {
        var schema = Operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        Assert.Equal("file", schema.GetProperty("required")[0].GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("binary", schema.GetProperty("properties").GetProperty("file").GetProperty("format").GetString());
    }

    [Fact]
    public void SuccessSchemaRequiresTheCoreFieldsAllowsOptionalMetadataAndHasNoSchemaVersion()
    {
        var schemas = Fixture.Value.RootElement.GetProperty("components").GetProperty("schemas");
        var response = schemas.GetProperty("LayoutDetectResponse");
        var required = response.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "requestId", "width", "height", "coordinateFrame", "regions" }, required);
        var properties = response.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("orientation", properties);
        Assert.Contains("engine", properties);
        Assert.Contains("model", properties);
        Assert.Contains("version", properties);
        Assert.Contains("diagnostics", properties);
        // The pinned contract has no wire schema-version field; consumers keep their own
        // contract identifier separately.
        Assert.DoesNotContain("schemaVersion", properties);
        var region = schemas.GetProperty("LayoutDetectRegion");
        var regionRequired = region.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { "id", "label", "x0", "y0", "x1", "y1" }, regionRequired);
        Assert.DoesNotContain("schemaVersion", region.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        // An explicitly empty regions array is legitimate.
        Assert.Equal("array", response.GetProperty("properties").GetProperty("regions").GetProperty("type").GetString());
    }

    public static TheoryData<int, string?> PinnedFailureAnswers => BuildPinnedAnswers();

    [Theory]
    [MemberData(nameof(PinnedFailureAnswers))]
    public async Task ClientClassifiesEveryPinnedStatusAndCodePair(int status, string? code)
    {
        // Every failure answer the pinned contract declares maps to the classification the
        // client must produce; the client never classifies by status alone (except the
        // platform's 401/403, which carry no layout code).
        var expected = (status, code) switch
        {
            (401, null) => LayoutDetectionFailure.Authentication,
            (403, null) => LayoutDetectionFailure.Authentication,
            (400, "layout-request-invalid") => LayoutDetectionFailure.InvalidInput,
            (413, "layout-input-too-large") => LayoutDetectionFailure.InvalidInput,
            (415, "layout-media-type-unsupported") => LayoutDetectionFailure.InvalidInput,
            (429, "layout-overloaded") => LayoutDetectionFailure.Overloaded,
            (503, "layout-capability-unsupported") => LayoutDetectionFailure.UnsupportedCapability,
            (504, "layout-timeout") => LayoutDetectionFailure.UpstreamTimeout,
            (502, "layout-output-invalid") => LayoutDetectionFailure.InvalidOutput,
            (502, "layout-runtime-unavailable") => LayoutDetectionFailure.RuntimeUnavailable,
            (503, "layout-runtime-unavailable") => LayoutDetectionFailure.RuntimeUnavailable,
            (502, "layout-runtime-rejected") => LayoutDetectionFailure.RuntimeRejected,
            _ => throw new InvalidOperationException($"The fixture declares an answer outside the pinned table: {status}/{code}"),
        };

        var client = new StructaDocLayoutClient(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(
                (HttpStatusCode)status)
            {
                Content = new StringContent(
                    code is null ? "{}" : $$"""{"title":"Layout detection failed","code":"{{code}}"}""",
                    Encoding.UTF8, "application/problem+json"),
            }))) { BaseAddress = new Uri("http://127.0.0.1:9/") },
            new LayoutCapabilityOptions(true),
            new StructaDocLayoutOptions { BaseUrl = "http://127.0.0.1:9/", ApiKey = "fixture-key-1234567890" },
            NullLogger<StructaDocLayoutClient>.Instance);

        var failure = await Assert.ThrowsAsync<LayoutDetectionException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 1, 0, 0, 0, 1], "image/png")));
        Assert.Equal(expected, failure.Failure);
        if (code is not null) Assert.Equal(code, failure.ProblemCode);
    }

    private static TheoryData<int, string?> BuildPinnedAnswers()
    {
        var answers = new TheoryData<int, string?>();
        foreach (var response in Operation.GetProperty("responses").EnumerateObject())
        {
            var status = int.Parse(response.Name);
            if (status == 200)
            {
                // The success answer is JSON, not a problem document; its schema is pinned
                // by SuccessSchemaRequiresTheCoreFieldsAllowsOptionalMetadataAndHasNoSchemaVersion.
                continue;
            }
            if (!response.Value.TryGetProperty("content", out var content))
            {
                // 401/403 are described without a layout problem code; the client
                // classifies them by status.
                Assert.True(status is 401 or 403, $"Unexpected codeless error status {status} in the fixture.");
                answers.Add(status, null);
                continue;
            }
            var codes = content.GetProperty("application/problem+json").GetProperty("x-ruoyu-codes")
                .EnumerateArray().Select(code => code.GetString()!).ToList();
            Assert.NotEmpty(codes);
            foreach (var code in codes) answers.Add(status, code);
        }
        // The fixture must pin the complete documented failure surface.
        Assert.Contains((429, "layout-overloaded"), answers.Select(a => ((int)a[0]!, (string?)a[1])));
        Assert.Contains((503, "layout-capability-unsupported"), answers.Select(a => ((int)a[0]!, (string?)a[1])));
        Assert.Contains((504, "layout-timeout"), answers.Select(a => ((int)a[0]!, (string?)a[1])));
        return answers;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
