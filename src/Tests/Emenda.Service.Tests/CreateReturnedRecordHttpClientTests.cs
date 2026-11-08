using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Emenda.StudentClient;

namespace Emenda.Service.Tests;

/// <summary>
/// IKJEY4 acceptance tests for the Mistake-owned StudentHttpClient returned-record
/// callback classification: success (fresh or replayed) must surface,
/// HTTP 409 must classify as a payload conflict, and transport failures /
/// timeouts / unreadable or business-failed responses must surface as an
/// explicit Unknown outcome instead of being swallowed.
/// </summary>
public sealed class CreateReturnedRecordHttpClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task SuccessResponse_ClassifiesAsSucceededAndPreservesReplayFlag(HttpStatusCode status)
    {
        string? requestedPath = null;
        string? requestedBody = null;
        var client = CreateClient(async (request, ct) =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            requestedBody = await request.Content!.ReadAsStringAsync(ct);
            return Json(status,
                """{"success":true,"data":{"success":true,"replayed":true,"record":{"id":"r1"}}}""");
        });

        var outcome = await client.CreateReturnedRecordAsync(
            "student-1", [new ImageEntryDto { Path = "uploads/a", Type = "mistake" }], "Rejected", "reason", "mistake-return:1");

        Assert.Equal("/api/students/student-1/uploads/returned", requestedPath);
        Assert.Contains("\"operationId\":\"mistake-return:1\"", requestedBody);
        Assert.Equal(CreateReturnedRecordStatus.Succeeded, outcome.Status);
        Assert.True(outcome.Replayed);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task SuccessResponse_FreshCreationAlsoClassifiesAsSucceeded(HttpStatusCode status)
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(status,
            """{"success":true,"data":{"success":true,"replayed":false}}""")));

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(CreateReturnedRecordStatus.Succeeded, outcome.Status);
        Assert.False(outcome.Replayed);
    }

    [Fact]
    public async Task ConflictResponse_ClassifiesAsPayloadConflictWithMessage()
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(HttpStatusCode.Conflict,
            """{"success":false,"message":"operationId 'op' was already used with a different payload"}""")));

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(CreateReturnedRecordStatus.PayloadConflict, outcome.Status);
        Assert.Contains("different payload", outcome.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NonSuccessHttpResponses_ClassifyAsUnknown(HttpStatusCode status)
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(status, "{\"success\":false,\"message\":\"boom\"}")));

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(CreateReturnedRecordStatus.Unknown, outcome.Status);
        Assert.NotEmpty(outcome.Message);
    }

    [Fact]
    public async Task BusinessLevelFailure_ClassifiesAsUnknown()
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"success":true,"data":{"success":false,"errorMessage":"internal failure"}}""")));

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(CreateReturnedRecordStatus.Unknown, outcome.Status);
    }

    [Fact]
    public async Task TransportFailure_SurfacesAsUnknownWithOriginalMessage()
    {
        var client = CreateClient((_, _) => throw new HttpRequestException("connection refused"));

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(CreateReturnedRecordStatus.Unknown, outcome.Status);
        Assert.Contains("connection refused", outcome.Message);
    }

    [Fact]
    public async Task Timeout_ClassifiesAsUnknown()
    {
        var client = CreateClient((_, _) => throw new TaskCanceledException("timed out"));

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(CreateReturnedRecordStatus.Unknown, outcome.Status);
    }

    public static IEnumerable<object[]> UnreadableResponses()
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Conflict,
                     HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable })
        foreach (var body in new[] { "", "UPSTREAM_PRIVATE_CANARY{", "null",
                     "{\"success\":\"UPSTREAM_PRIVATE_CANARY\"}",
                     "{\"success\":true,\"data\":{\"success\":\"UPSTREAM_PRIVATE_CANARY\"}}",
                     "{\"message\":{\"private\":\"UPSTREAM_PRIVATE_CANARY\"}}" })
            yield return [status, body];
    }

    [Theory]
    [MemberData(nameof(UnreadableResponses))]
    public async Task UnreadableResponse_PreservesStatusWithoutRetryOrLeakingBody(HttpStatusCode status, string body)
    {
        var sends = 0;
        var logger = new Mock<ILogger<StudentHttpClient>>();
        var client = new StudentHttpClient(new HttpClient(new StubHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Json(status, body));
        })) { BaseAddress = new Uri("http://localhost:5005") }, logger.Object);

        var outcome = await client.CreateReturnedRecordAsync("student-1", [], null, null, "op");

        Assert.Equal(status == HttpStatusCode.Conflict
            ? CreateReturnedRecordStatus.PayloadConflict : CreateReturnedRecordStatus.Unknown, outcome.Status);
        Assert.False(outcome.Replayed);
        Assert.Equal(1, sends);
        Assert.NotEmpty(outcome.Message);
        Assert.DoesNotContain("UPSTREAM_PRIVATE_CANARY", outcome.Message);
        if (status == HttpStatusCode.Conflict)
            Assert.Equal("CreateReturnedRecord payload conflict", outcome.Message);
        Assert.DoesNotContain(logger.Invocations, invocation =>
            invocation.Arguments.Any(argument => argument?.ToString()?.Contains("UPSTREAM_PRIVATE_CANARY") == true));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = CreateClient((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateReturnedRecordAsync("student-1", [], null, null, "op", cancellation.Token));
    }

    private static StudentHttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new StubHandler(send)) { BaseAddress = new Uri("http://localhost:5005") },
            NullLogger<StudentHttpClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
