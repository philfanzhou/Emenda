using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Host.Admin;
using Emenda.Host.Authentication;
using Emenda.Host.Security;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Emenda.Service.Tests;

/// <summary>
/// The admin console through the real standalone host: the unconfigured state
/// (page anonymous, protocol endpoints 503, no management API, product API
/// unaffected) and the configured state with a seeded server-side session
/// ticket (CSRF-protected caller management that immediately drives the
/// product caller authentication).
/// </summary>
public sealed class AdminConsoleEndpointTests
{
    private const string CallerName = "admin-console-tests";
    private const string CallerKey = "admin-console-test-api-key-0123456789";

    // ---- unconfigured console ----

    [Fact]
    public async Task UnconfiguredConsole_PageAnonymousAndProtocolStubbed()
    {
        using var factory = new AdminConsoleWebApplicationFactory(configured: false);
        using var client = factory.CreateClient();

        using var page = await client.GetAsync("/admin");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Headers.CacheControl.NoStore.Should().BeTrue();
        (await page.Content.ReadAsStringAsync()).Should().Contain("Emenda 管理控制台");

        using var session = await client.GetAsync("/admin/auth/oidc/session");
        session.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var start = await client.GetAsync("/admin/auth/oidc/start");
        start.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The management API is not mapped when the console is unconfigured.
        using var callers = await client.GetAsync("/admin/api/callers");
        callers.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The product API surface keeps its caller-credential contract.
        using var unauthenticated = await client.GetAsync("/api/mistakes");
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", CallerKey);
        using var authenticated = await client.GetAsync("/api/mistakes");
        authenticated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- configured console with a seeded session ----

    [Fact]
    public async Task ConfiguredConsole_ManagesCallersEndToEnd()
    {
        using var factory = new AdminConsoleWebApplicationFactory(configured: true);
        using var client = factory.CreateClient();

        // Anonymous: the page stays reachable and the API never answers with
        // caller data (an anonymous challenge ends on the sign-in/console page
        // HTML after its redirects, never a JSON payload).
        using var anonymousList = await client.GetAsync("/admin/api/callers");
        if (anonymousList.StatusCode == HttpStatusCode.OK)
        {
            anonymousList.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        }
        else
        {
            anonymousList.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
        }

        // Seed a server-side session ticket the way a completed hosted login does.
        string cookieKey;
        using (var scope = factory.Services.CreateScope())
        {
            var store = (InMemoryTicketStore)scope.ServiceProvider.GetRequiredService<ITicketStore>();
            var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "seed-admin") },
                authenticationType: SignaCoreHostedLoginDefaults.SessionAuthenticationScheme));
            var ticket = new SignaCoreSessionTicket(
                principal, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(20),
                "seed-access-token", "seed-id-token");
            cookieKey = await store.StoreAsync(ticket, CancellationToken.None);
        }
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminConsoleConstants.SessionCookie}={cookieKey}");

        // The seeded session restores through the package's session endpoint.
        using var session = await client.GetAsync("/admin/auth/oidc/session");
        session.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        sessionBody.RootElement.GetProperty("data").GetProperty("authenticated").GetBoolean().Should().BeTrue();

        // The static caller is listed as configuration-sourced.
        using var list = await client.GetAsync("/admin/api/callers");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var names = listBody.RootElement.GetProperty("data").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()).ToList();
        names.Should().Contain(CallerName);

        // Unsafe methods need the antiforgery pair; fetch it through the package endpoint
        // and carry both cookies plus the header from here on.
        using var csrf = await client.GetAsync("/admin/auth/oidc/csrf");
        csrf.StatusCode.Should().Be(HttpStatusCode.OK);
        using var csrfBody = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        var token = csrfBody.RootElement.GetProperty("token").GetString();
        // The antiforgery cookie may arrive in a Set-Cookie header (preferred) or
        // already in the client's default cookie store; collect every source.
        var setCookies = csrf.Headers.NonValidated.SelectMany(header =>
                header.Key == "Set-Cookie" ? header.Value : Enumerable.Empty<string>())
            .ToList();
        Console.WriteLine("SET-COOKIES: " + string.Join(" | ", setCookies));
        var csrfCookie = setCookies
            .Select(value => value.Split(';', '='))
            .Where(parts => parts.Length > 1 && parts[0].Trim() == AdminConsoleConstants.CsrfCookie)
            .Select(parts => parts[1])
            .FirstOrDefault();
        csrfCookie.Should().NotBeNull();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie",
            $"{AdminConsoleConstants.SessionCookie}={cookieKey}; {AdminConsoleConstants.CsrfCookie}={csrfCookie}");

        using var noCsrf = await client.PostAsJsonAsync("/admin/api/callers", new { name = "study-platform" });
        noCsrf.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Create with the CSRF header.
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/api/callers")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "study-platform", note = "test" }), Encoding.UTF8, "application/json")
        };
        create.Headers.Add(AdminConsoleConstants.CsrfHeader, token);
        using var createResponse = await client.SendAsync(create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var createBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var generatedKey = createBody.RootElement.GetProperty("data").GetProperty("apiKey").GetString();
        generatedKey.Should().NotBeNullOrWhiteSpace();

        // The dynamic caller immediately authenticates the product API.
        using var withNewKey = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mistakes")
            .WithHeader("X-Emenda-Api-Key", generatedKey!));
        withNewKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Reveal, rotate and delete round-trip.
        using var reveal = await client.GetAsync("/admin/api/callers/study-platform/secret");
        reveal.StatusCode.Should().Be(HttpStatusCode.OK);
        using var revealBody = JsonDocument.Parse(await reveal.Content.ReadAsStringAsync());
        revealBody.RootElement.GetProperty("data").GetProperty("apiKey").GetString().Should().Be(generatedKey);

        using var rotate = new HttpRequestMessage(HttpMethod.Post, "/admin/api/callers/study-platform/rotate");
        rotate.Headers.Add(AdminConsoleConstants.CsrfHeader, token);
        using var rotateResponse = await client.SendAsync(rotate);
        rotateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rotateBody = JsonDocument.Parse(await rotateResponse.Content.ReadAsStringAsync());
        var rotatedKey = rotateBody.RootElement.GetProperty("data").GetProperty("apiKey").GetString();
        rotatedKey.Should().NotBe(generatedKey);

        // The old key is gone; the rotated key works.
        using var oldRejected = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mistakes")
            .WithHeader("X-Emenda-Api-Key", generatedKey!));
        oldRejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var rotatedAccepted = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mistakes")
            .WithHeader("X-Emenda-Api-Key", rotatedKey!));
        rotatedAccepted.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/admin/api/callers/study-platform");
        remove.Headers.Add(AdminConsoleConstants.CsrfHeader, token);
        using var removeResponse = await client.SendAsync(remove);
        removeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var afterRemove = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mistakes")
            .WithHeader("X-Emenda-Api-Key", rotatedKey!));
        afterRemove.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static readonly string[] OwnedEnvironmentKeys =
    [
        "Emenda__Runtime__Mode",
        "Emenda__Security__Callers__0__Name",
        "Emenda__Security__Callers__0__ApiKey",
        "Emenda__Security__DynamicCallers__Path",
        "Emenda__Admin__Oidc__Authority",
        "Emenda__Admin__Oidc__ClientId",
        "Emenda__Admin__Oidc__ClientSecret",
        "Emenda__Admin__Oidc__RedirectUri",
        "Emenda__Admin__Oidc__PostLogoutRedirectUri",
        "ConnectionStrings__Emenda",
        "Mistake__ConfigMode",
        "Mistake__Student__Enabled",
        "Mistake__Workers__UploadPollingWorker__Enabled",
        "Mistake__Workers__ReanalyzeJobHostedService__Enabled",
        "Mistake__Workers__ReanalyzeJobCleanupService__Enabled",
        "VlAnalyzer__ApiKey",
        "IdentityService__Authority",
        "IdentityService__Issuer",
        "IdentityService__Audience",
        "IdentityService__AdminAudience",
        "IdentityService__MistakeApiStrictAuth",
        "IdentityService__MistakeApiResourceAuth",
    ];

    private sealed class AdminConsoleWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly bool _configured;
        private readonly string _dataRoot =
            Path.Combine(Path.GetTempPath(), $"emenda-admin-tests-{Guid.NewGuid():N}");

        public AdminConsoleWebApplicationFactory(bool configured = false)
        {
            _configured = configured;
            Environment.SetEnvironmentVariable("Emenda__Runtime__Mode", "Standalone");
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
            Environment.SetEnvironmentVariable("Emenda__Security__DynamicCallers__Path",
                configured ? Path.Combine(_dataRoot, "emenda-callers.json") : null);
            Environment.SetEnvironmentVariable("Emenda__Admin__Oidc__Authority",
                configured ? "http://127.0.0.1:5111" : null);
            Environment.SetEnvironmentVariable("Emenda__Admin__Oidc__ClientId",
                configured ? "emenda-admin-console-tests" : null);
            Environment.SetEnvironmentVariable("Emenda__Admin__Oidc__ClientSecret",
                configured ? "test-secret-value" : null);
            Environment.SetEnvironmentVariable("Emenda__Admin__Oidc__RedirectUri",
                configured ? "http://127.0.0.1:5010/admin/auth/oidc/callback" : null);
            Environment.SetEnvironmentVariable("Emenda__Admin__Oidc__PostLogoutRedirectUri",
                configured ? "http://127.0.0.1:5010/admin/auth/oidc/logout/return" : null);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Emenda",
                "Host=127.0.0.1;Port=5432;Database=emenda_admin_tests;Username=emenda;Password=admin-tests-only");
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", null);
            Environment.SetEnvironmentVariable("Mistake__Student__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__UploadPollingWorker__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobHostedService__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobCleanupService__Enabled", "false");
            Environment.SetEnvironmentVariable("VlAnalyzer__ApiKey", null);
            Environment.SetEnvironmentVariable("IdentityService__Authority", null);
            Environment.SetEnvironmentVariable("IdentityService__Issuer", null);
            Environment.SetEnvironmentVariable("IdentityService__Audience", null);
            Environment.SetEnvironmentVariable("IdentityService__AdminAudience", null);
            Environment.SetEnvironmentVariable("IdentityService__MistakeApiStrictAuth", null);
            Environment.SetEnvironmentVariable("IdentityService__MistakeApiResourceAuth", null);
        }

        protected override void Dispose(bool disposing)
        {
            foreach (var key in OwnedEnvironmentKeys)
                Environment.SetEnvironmentVariable(key, null);
            try
            {
                if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var databaseName = $"emenda_admin_{Guid.NewGuid():N}";
            var dataRoot = _dataRoot;
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<IOssService>();
                services.AddSingleton<IOssService>(new LocalFileOssService(
                    Path.Combine(dataRoot, "oss")));
                if (_configured)
                {
                    // Keep the hosted-login backchannel away from any real network: the
                    // seeded ticket path never performs discovery, but a stray call must
                    // not hang the test host.
                    services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                        {
                            ConnectCallback = (_, _) => throw new InvalidOperationException("network disabled in tests")
                        });
                }
            });
        }
    }
}

file static class HttpRequestMessageExtensions
{
    public static HttpRequestMessage WithHeader(this HttpRequestMessage message, string name, string value)
    {
        message.Headers.Add(name, value);
        return message;
    }
}

/// <summary>Adapts an HttpClientHandler (with its cookie container) to a DelegatingHandler.</summary>