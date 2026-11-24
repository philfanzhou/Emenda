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
        var draft = Draft() with { QuestionNumber = "三、2（1）", Regions = [Region with { SourceId = Other }, Region] };
        var page = Request().Images[0];
        var middle = Enumerable.Range(0, 28).Select(_ => page with { SourceId = Guid.NewGuid(), Regions = [] });
        var request = new QuestionAnalysisRequest(3, [page, .. middle, page with { SourceId = Other, Regions = [] }]);
        var client = Client(async (message, ct) =>
        {
            Assert.Equal("/api/v1/problems/analyze-images", message.RequestUri!.AbsolutePath);
            Assert.Equal(new string('k', 32), message.Headers.GetValues("X-Emenda-Analysis-Key").Single());
            var body = await message.Content!.ReadFromJsonAsync<QuestionAnalysisRequest>(ct);
            Assert.Equal(request.Images.Select(i => i.SourceId), body!.Images.Select(i => i.SourceId));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new QuestionAnalysisResult("vision", [draft])) };
        });
        var result = await client.AnalyzeAsync(request);
        Assert.Equal("unknown", result.Questions[0].Outcome);
        Assert.Null(result.Questions[0].StudentAnswer);
        Assert.Equal("三、2（1）", result.Questions[0].QuestionNumber);
        Assert.Equal(new[] { Other, Source }, result.Questions[0].Regions.Select(r => r.SourceId));
    }

    [Fact]
    public async Task AcceptsImagesWithoutGradeAndKeepsIndependentQuestionGrades()
    {
        var body = $$"""{"model":"vision","questions":[{"subject":2,"grade":6,"statement":"1+1","studentAnswer":"2","referenceAnswer":"2","outcome":"correct","evidence":"Matches 2","regions":[{"sourceId":"{{Source}}","x0":0,"y0":0,"x1":1,"y1":1}]},{"subject":2,"grade":8,"statement":"x+2=3","studentAnswer":null,"referenceAnswer":"1","outcome":"unknown","regions":[{"sourceId":"{{Source}}","x0":0,"y0":0,"x1":1,"y1":1}]}]}""";
        var client = Client(async (message, ct) =>
        {
            var request = await message.Content!.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            Assert.False(request.TryGetProperty("grade", out _));
            return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        });
        var result = await client.AnalyzeAsync(Request() with { Grade = null });
        Assert.Equal(new int?[] { 6, 8 }, result.Questions.Select(q => q.Grade));
        Assert.Equal("", result.Questions[0].Explanation);
        Assert.Equal("Matches 2", result.Questions[0].Evidence);
        Assert.Equal("", result.Questions[1].Evidence);
    }

    [Fact]
    public async Task ReadsAllFortyQuestionsInProviderOrder()
    {
        var labels = Enumerable.Range(1, 40).Select(n => n.ToString()).ToArray();
        var drafts = labels.Select(label => Draft() with { QuestionNumber = label }).ToList();
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new QuestionAnalysisResult("vision", drafts))
        }));

        Assert.Equal(labels, (await client.AnalyzeAsync(Request())).Questions.Select(q => q.QuestionNumber));
    }

    [Fact]
    public async Task RevisionPreservesOnlySuppliedQuestionIdentities()
    {
        var question = Draft() with { Id = Guid.NewGuid() };
        foreach (var id in new[] { question.Id, Guid.NewGuid() })
        {
            var client = Client(async (message, ct) =>
            {
                var sent = await message.Content!.ReadFromJsonAsync<QuestionAnalysisRequest>(ct);
                Assert.Equal(question.Id, sent!.Revision!.Questions.Single().Id);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new QuestionAnalysisResult("vision", [question with { Id = id }])) };
            });
            var request = Request() with { Revision = new("Complete the original crop", "questions", [question]) };
            if (id == question.Id) Assert.Equal(id, (await client.AnalyzeAsync(request)).Questions.Single().Id);
            else Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(request))).Code);
        }
    }

    [Fact]
    public async Task RejectsForeignRegionsAndConfidentAnswersWithoutAttempt()
    {
        foreach (var draft in new[] { Draft() with { Regions = [Region with { SourceId = Other }] }, Draft() with { Outcome = "incorrect", ReferenceAnswer = "2", Evidence = "visible" }, Draft() with { Grade = 13 } })
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

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 12")]
    [InlineData("12 ")]
    [InlineData("12\n（1）")]
    [InlineData("12\u007f1")]
    public async Task RejectsInvalidPrintedQuestionNumbers(string number)
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new QuestionAnalysisResult("vision", [Draft() with { QuestionNumber = number }]))
        }));
        Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => client.AnalyzeAsync(Request()))).Code);
    }

    [Fact]
    public async Task AcceptsMissingOrNullNumbersAndRepeatedLabelsButRejectsOversizedNumbers()
    {
        var number = new string('题', 64);
        var drafts = new[] { Draft(), Draft() with { QuestionNumber = number }, Draft() with { QuestionNumber = number } };
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new QuestionAnalysisResult("vision", drafts.ToList()))
        }));
        var result = await client.AnalyzeAsync(Request());
        Assert.Null(result.Questions[0].QuestionNumber);
        Assert.Equal(new string?[] { null, number, number }, result.Questions.Select(q => q.QuestionNumber));
        var oversized = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new QuestionAnalysisResult("vision", [Draft() with { QuestionNumber = number + "题" }]))
        }));
        Assert.Equal("invalid_analysis_response", (await Assert.ThrowsAsync<ExtractionException>(() => oversized.AnalyzeAsync(Request()))).Code);
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
