using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.StudentClient;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEY4 acceptance tests for the Mistake-owned StudentHttpClient returned-record
/// callback classification: success (fresh or replayed) must surface,
/// HTTP 409 must classify as a payload conflict, and transport failures /
/// timeouts / unreadable or business-failed responses must surface as an
/// explicit Unknown outcome instead of being swallowed.
/// </summary>
public sealed class CreateReturnedRecordHttpClientTests
{
    [Fact]
    public async Task SuccessResponse_ClassifiesAsSucceededAndPreservesReplayFlag()
    {
        string? requestedPath = null;
        string? requestedBody = null;
        var client = CreateClient((request, _) =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            requestedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"success":true,"data":{"success":true,"replayed":true,"record":{"id":"r1"}}}"""));
        });

        var outcome = await client.CreateReturnedRecordAsync(
            "student-1", [new ImageEntryDto { Path = "uploads/a", Type = "mistake" }], "Rejected", "reason", "mistake-return:1");

        Assert.Equal("/api/students/student-1/uploads/returned", requestedPath);
        Assert.Contains("\"operationId\":\"mistake-return:1\"", requestedBody);
        Assert.Equal(CreateReturnedRecordStatus.Succeeded, outcome.Status);
        Assert.True(outcome.Replayed);
    }

    [Fact]
    public async Task SuccessResponse_FreshCreationAlsoClassifiesAsSucceeded()
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
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
