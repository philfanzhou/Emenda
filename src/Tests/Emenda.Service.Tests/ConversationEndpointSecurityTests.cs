using System.Net;
using System.Net.Http.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Host.Security;
using Emenda.Host.Services.Conversation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Emenda.Service.Tests;

public sealed class ConversationEndpointSecurityTests
{
    [Theory]
    [InlineData("/api/mistakes/conversation-collections")]
    [InlineData("/api/mistakes/conversation-collections/stop")]
    [InlineData("/api/mistakes/22222222-2222-4222-8222-222222222222/conversation-analysis")]
    public async Task CollectionSurfaceRejectsUntrustedCallerKeysAndBrowserHeadersBeforeDependencies(string path)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        // The appsettings copy in the test output no longer carries a Logging section.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var authorization = new Mock<IConversationAuthorization>(MockBehavior.Strict);
        var database = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=never;Username=fixture").Options;
        builder.Services.AddSingleton(new ConversationCollectionOptions { Enabled = true, CollectionKey = new string('c', 64) });
        builder.Services.AddSingleton(new ConversationCollectionStore(database, authorization.Object, Mock.Of<IOssService>(), Mock.Of<IConversationExportReader>()));
        builder.Services.AddSingleton(new ConversationAnalysisStore(database, authorization.Object));
        await using var app = builder.Build();
        app.Use(async (http, next) => { if (http.Request.Headers.TryGetValue("Fixture-Caller", out var caller)) http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey] = caller.ToString(); await next(http); });
        app.MapConversationCollectionEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        object input = path.EndsWith("/stop") ? new { studentId = "learner", actorId = "actor" }
            : path.EndsWith("conversation-analysis") ? new ConversationAnalysisWrite("learner", "actor", new string('a', 64), 1, "model", "Explanation")
            : new ConversationCollectionEndpoints.CollectionAdmission(Guid.NewGuid(), "learner", 3, "actor", "owner");
        foreach (var invalid in new[] { "missing-caller", "foreign-caller", "wrong-key", "Origin", "Sec-Fetch-Site" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
            if (invalid != "missing-caller") request.Headers.Add("Fixture-Caller", invalid == "foreign-caller" ? "foreign" : "platform");
            request.Headers.Add("X-Question-Collection-Key", new string(invalid == "wrong-key" ? 'w' : 'c', 64));
            if (invalid is "Origin" or "Sec-Fetch-Site") request.Headers.Add(invalid, "browser");
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(authorization.Invocations);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task AdmissionDistinguishesExplicitDenialFromAuthorizationOutage(bool outage, HttpStatusCode status)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        // The appsettings copy in the test output no longer carries a Logging section.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var authorization = new Mock<IConversationAuthorization>();
        var setup = authorization.Setup(a => a.AllowedAsync("actor", "learner", "owner", It.IsAny<CancellationToken>()));
        if (outage) setup.ThrowsAsync(new HttpRequestException("unavailable")); else setup.ReturnsAsync(false);
        var database = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=never;Username=fixture").Options;
        builder.Services.AddSingleton(new ConversationCollectionOptions { Enabled = true, CollectionKey = new string('c', 64) });
        builder.Services.AddSingleton(new ConversationCollectionStore(database, authorization.Object, Mock.Of<IOssService>(), Mock.Of<IConversationExportReader>()));
        builder.Services.AddSingleton(new ConversationAnalysisStore(database, authorization.Object));
        await using var app = builder.Build(); app.Use(async (http, next) => { http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey] = "platform"; await next(http); });
        app.MapConversationCollectionEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Question-Collection-Key", new string('c', 64));
        using var response = await client.PostAsJsonAsync("/api/mistakes/conversation-collections", new ConversationCollectionEndpoints.CollectionAdmission(Guid.NewGuid(), "learner", 3, "actor", "owner"));
        Assert.Equal(status, response.StatusCode); Assert.Single(authorization.Invocations);
    }
}
