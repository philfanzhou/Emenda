using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.StudentClient;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEY6 acceptance tests for the Mistake-owned StudentHttpClient removal callback:
/// the errorKind classification from the Student response payload must be
/// passed through unchanged, and absent classifications stay empty.
/// </summary>
public sealed class RemoveProcessedImagesHttpClientTests
{
    [Theory]
    [InlineData("{\"success\":true,\"data\":{\"success\":true,\"recordDeleted\":true,\"remainingImageCount\":0,\"errorKind\":\"none\"}}", "none")]
    [InlineData("{\"success\":true,\"data\":{\"success\":false,\"recordDeleted\":false,\"remainingImageCount\":0,\"errorKind\":\"unknown_record\"}}", "unknown_record")]
    public async Task ErrorKindPassesThroughFromStudentPayload(string body, string expectedErrorKind)
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));

        var result = await client.RemoveProcessedImagesAsync("record-1", new List<string> { "uploads/a" });

        Assert.Equal(expectedErrorKind, result.ErrorKind);
    }

    [Fact]
    public async Task LegacyResponseWithoutErrorKindStaysEmpty()
    {
        var client = CreateClient((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"success":true,"data":{"success":true,"recordDeleted":false,"remainingImageCount":1}}""")));

        var result = await client.RemoveProcessedImagesAsync("record-1", new List<string>());

        Assert.Equal(string.Empty, result.ErrorKind);
        Assert.True(result.Success);
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
