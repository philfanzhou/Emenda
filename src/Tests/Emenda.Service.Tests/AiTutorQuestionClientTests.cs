using System.Net;
using System.Net.Http.Json;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.Extensions.Options;

namespace Emenda.Service.Tests;

public sealed class AiTutorQuestionClientTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly ExtractionRegion Region = new(Source, 0, 0, 1, 1);
    private static QuestionAnalysisRequest Request() => new(3, [new(Source, "data:image/png;base64,iVBORw0KGgo=", 100, 100, [Region])]);
    private static ExtractionQuestionDraft Draft() => new() { Subject = 2, Statement = "1+1", Outcome = "unknown", Regions = [Region] };
    private static ExtractionOptions Settings() => new() { Enabled = true, TutorBaseUrl = "http://localhost:8787", TutorApiKey = new string('k', 32) };

    [Fact]
    public async Task SendsExactContractAndPreservesCrossImageUnknown()
    {
        var draft = Draft() with { Regions = [Region, Region with { SourceId = Other }] };
        var request = Request() with { Images = [.. Request().Images, new(Other, "data:image/png;base64,iVBORw0KGgo=", 100, 100, [])] };
        var client = Client(async (message, ct) =>
        {
            Assert.Equal("/api/v1/problems/analyze-images", message.RequestUri!.AbsolutePath);
            Assert.Equal(new string('k', 32), message.Headers.GetValues("X-Emenda-Analysis-Key").Single());
            var body = await message.Content!.ReadFromJsonAsync<QuestionAnalysisRequest>(ct);
            Assert.Equal(2, body!.Images.Count);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new QuestionAnalysisResult("vision", [draft])) };
        });
        var result = await client.AnalyzeAsync(request);
        Assert.Equal("unknown", result.Questions[0].Outcome);
        Assert.Null(result.Questions[0].StudentAnswer);
        Assert.Equal(2, result.Questions[0].Regions.Count);
    }

    [Fact]
    public async Task RejectsForeignRegionsAndConfidentAnswersWithoutAttempt()
    {
        foreach (var draft in new[] { Draft() with { Regions = [Region with { SourceId = Other }] }, Draft() with { Outcome = "incorrect", ReferenceAnswer = "2", Evidence = "visible" } })
        {
            var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new QuestionAnalysisResult("vision", [draft])) }));
            Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(Request()))).Code);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "analysis_credentials_rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "analysis_rate_limited")]
    [InlineData(HttpStatusCode.TemporaryRedirect, "analysis_provider_unavailable")]
    public async Task DoesNotRetryFailures(HttpStatusCode status, string code)
    {
        var calls = 0;
        var client = Client((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(status)); });
        Assert.Equal(code, (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(Request()))).Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectsMalformedAndOversizedResponses()
    {
        foreach (var body in new[] { "not json", new string('x', 2 * 1024 * 1024 + 1) })
        {
            var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }));
            Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(Request()))).Code);
        }
    }

    [Fact]
    public async Task RejectsOversizedRequestBeforeNetwork()
    {
        var client = Client((_, _) => throw new InvalidOperationException("must not send"));
        var request = Request() with { Images = [new(Source, "data:image/png;base64," + new string('A', 8 * 1024 * 1024), 100, 100, [])] };
        Assert.Equal("analysis_request_too_large", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(request))).Code);
    }

    [Fact]
    public async Task PreservesCallerCancellation()
    {
        var client = Client(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AnalyzeAsync(Request(), cancel.Token));
    }

    [Theory]
    [InlineData("https://user:secret@example.test")]
    [InlineData("file:///tmp/provider")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test?key=value")]
    public void RejectsUnsafeBaseUris(string value)
    {
        var settings = Settings();
        settings.TutorBaseUrl = value;
        Assert.False(settings.IsConfigured);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("text/plain", false)]
    [InlineData("text/html; charset=utf-8", false)]
    [InlineData("text/vnd.analysis+json", false)]
    [InlineData("application/json; charset=utf-16", false)]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/vnd.emenda.analysis+json; charset=\"UTF-8\"", true)]
    public async Task RequiresUtf8JsonContentType(string? contentType, bool accepted)
    {
        var client = Client((_, _) =>
        {
            var content = JsonContent.Create(new QuestionAnalysisResult("vision", [Draft()]));
            content.Headers.ContentType = contentType is null ? null : System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        if (accepted)
            Assert.Equal("vision", (await client.AnalyzeAsync(Request())).Model);
        else
            Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(Request()))).Code);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("identity")]
    public async Task RejectsContentEncodingEvenWhenBodyIsValidJson(string encoding)
    {
        var client = Client((_, _) =>
        {
            var content = JsonContent.Create(new QuestionAnalysisResult("vision", [Draft()]));
            content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(Request()))).Code);
    }

    [Fact]
    public async Task PreservesBase64PlusWithoutHtmlEscapingOnWire()
    {
        const string dataUrl = "data:image/png;base64,++++";
        var client = Client(async (message, ct) =>
        {
            var json = await message.Content!.ReadAsStringAsync(ct);
            Assert.Contains("++++", json);
            Assert.DoesNotContain("\\u002B", json, StringComparison.OrdinalIgnoreCase);
            var parsed = await message.Content.ReadFromJsonAsync<QuestionAnalysisRequest>(ct);
            Assert.Equal(dataUrl, parsed!.Images[0].DataUrl);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new QuestionAnalysisResult("vision", [Draft()])) };
        });
        var request = Request() with { Images = [new(Source, dataUrl, 100, 100, [Region])] };
        Assert.Equal("vision", (await client.AnalyzeAsync(request)).Model);
    }

    private static AiTutorQuestionClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)), Options.Create(Settings()));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
