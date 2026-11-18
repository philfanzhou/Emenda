using System.Net;
using System.Net.Http.Json;
using System.Text;
using Emenda.Host.Services.Conversation;
using Moq;

namespace Emenda.Service.Tests;

public sealed class ConversationAuthorizationClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FixedCallbackUsesTrustedAssertionsAndOnlyExplicitBooleanAuthorization(bool authorized)
    {
        var client = Client(async (request, ct) =>
        {
            Assert.Equal("https://portal.example/api/ai-tutor/collection/authorize", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new string('k', 64), request.Headers.GetValues("X-Question-Collection-Key").Single());
            Assert.False(request.Headers.Contains("Authorization")); Assert.False(request.Headers.Contains("Origin"));
            var body = await request.Content!.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            Assert.Equal("actor", body!["actorId"]); Assert.Equal("opaque-student", body["studentId"]); Assert.Equal("owned-context", body["ownerId"]);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { authorized }) };
        });
        Assert.Equal(authorized, await client.AllowedAsync("actor", "opaque-student", "owned-context", CancellationToken.None));
    }

    [Theory]
    [InlineData(502, "application/json", "{\"authorized\":false}")]
    [InlineData(302, "application/json", "{\"authorized\":false}")]
    [InlineData(200, "text/plain", "{\"authorized\":false}")]
    [InlineData(200, "application/json", "{\"authorized\":null}")]
    [InlineData(200, "application/json", "{\"authorized\":false,\"authorized\":true}")]
    [InlineData(200, "application/json", "{")]
    public async Task OutageRedirectMalformedAndWrongContentNeverBecomePermanentRevocation(int status, string media, string body)
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.AllowedAsync("actor", "student", "owner", CancellationToken.None));
    }

    [Fact]
    public async Task OversizedCallbackAndCancellationRemainFailures()
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 5000) + "{\"authorized\":false}", Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.AllowedAsync("actor", "student", "owner", CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AllowedAsync("actor", "student", "owner", canceled.Token));
    }

    [Theory]
    [InlineData("https://tutor.example/path", "https://portal.example")]
    [InlineData("https://tutor.example", "https://portal.example?redirect=x")]
    [InlineData("https://user:password@tutor.example", "https://portal.example")]
    public void ConfigurationRejectsNonOriginDestinations(string tutor, string portal)
        => Assert.Throws<InvalidOperationException>(() => new ConversationCollectionOptions { Enabled = true, TutorUrl = tutor, AuthorizationBaseUrl = portal, CollectionKey = new string('k', 64) }.Validate());

    private static ConversationAuthorizationClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("ConversationCollection")).Returns(() => new HttpClient(new Handler(callback)));
        return new(factory.Object, new() { AuthorizationBaseUrl = "https://portal.example", CollectionKey = new string('k', 64) });
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request, ct);
    }
}
