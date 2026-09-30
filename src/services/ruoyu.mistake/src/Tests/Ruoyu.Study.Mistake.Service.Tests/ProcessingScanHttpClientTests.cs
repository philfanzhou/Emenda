using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.StudentClient;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class ProcessingScanHttpClientTests
{
    [Fact]
    public async Task SuccessfulPagePreservesCursorAndImageFields()
    {
        string? requested = null;
        var client = CreateClient((request, _) =>
        {
            requested = request.RequestUri?.PathAndQuery;
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"success":true,"data":{"items":[{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","studentId":"student-1","imageEntries":[{"path":"uploads/a","type":"mistake"}]}],"hasMore":true,"nextCursor":"abc"}}"""));
        });

        var page = await client.ScanProcessingUploadRecordsAsync("old", 50);
        Assert.Equal("/api/uploads/processing/scan?pageSize=50&cursor=old", requested);
        Assert.True(page.HasMore);
        Assert.Equal("abc", page.NextCursor);
        Assert.Equal("uploads/a", Assert.Single(Assert.Single(page.Items!).ImageEntries).Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailuresAreNotEmptyPages(HttpStatusCode status)
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(status, "{\"success\":false}")));
        var error = await Assert.ThrowsAsync<ProcessingUploadScanException>(() => client.ScanProcessingUploadRecordsAsync(null, 50));
        Assert.Equal(ProcessingUploadScanFailure.HttpStatus, error.Failure);
    }

    [Theory]
    [InlineData("{\"success\":false,\"message\":\"failed\"}", ProcessingUploadScanFailure.Business)]
    [InlineData("not json", ProcessingUploadScanFailure.InvalidResponse)]
    [InlineData("{\"success\":true,\"data\":{\"hasMore\":false}}", ProcessingUploadScanFailure.InvalidResponse)]
    [InlineData("{\"success\":true,\"data\":{\"items\":[],\"hasMore\":true}}", ProcessingUploadScanFailure.InvalidResponse)]
    public async Task BadPayloadsAreClassified(string body, ProcessingUploadScanFailure failure)
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));
        var error = await Assert.ThrowsAsync<ProcessingUploadScanException>(() => client.ScanProcessingUploadRecordsAsync(null, 50));
        Assert.Equal(failure, error.Failure);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationStayDistinct()
    {
        var timeoutClient = CreateClient((_, _) => throw new TaskCanceledException());
        var timeout = await Assert.ThrowsAsync<ProcessingUploadScanException>(() => timeoutClient.ScanProcessingUploadRecordsAsync(null, 50));
        Assert.Equal(ProcessingUploadScanFailure.Timeout, timeout.Failure);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledClient = CreateClient((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            canceledClient.ScanProcessingUploadRecordsAsync(null, 50, cancellation.Token));
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
