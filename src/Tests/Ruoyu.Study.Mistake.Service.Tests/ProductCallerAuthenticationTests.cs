using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ruoyu.Study.Mistake.Database;
using Xunit;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Product standalone mode (Emenda:Runtime:Mode=Standalone): callers must
/// present a per-caller credential on the business /api surface, while health,
/// contract and root endpoints stay anonymous. No platform identity provider
/// or Consul/Loki path is involved.
/// </summary>
public sealed class ProductCallerAuthenticationTests
{
    private const string CallerName = "standalone-tests";
    private const string CallerKey = "standalone-test-api-key-0123456789";
    private const string ListPath = "/api/mistakes?page=1&size=10&subject=0&grade=0&reviewStatus=0";

    [Fact]
    public async Task StandaloneMode_RejectsAnonymousBusinessRequestAsync()
    {
        using var factory = new StandaloneWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ListPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("caller authentication required");
    }

    [Fact]
    public async Task StandaloneMode_RejectsWrongCallerKeyAsync()
    {
        using var factory = new StandaloneWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ListPath);
        request.Headers.Add(ProductCallerAuthenticationOptions.ApiKeyHeaderName, "not-the-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StandaloneMode_AcceptsConfiguredCallerKeyAsync()
    {
        using var factory = new StandaloneWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ListPath);
        request.Headers.Add(ProductCallerAuthenticationOptions.ApiKeyHeaderName, CallerKey);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task StandaloneMode_KeepsHealthAndContractEndpointsAnonymousAsync()
    {
        using var factory = new StandaloneWebApplicationFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var contract = await client.GetAsync("/contracts/mistake-http-v3.json");

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        contract.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StandaloneMode_ProtectsProductIntakeEndpointAsync()
    {
        using var factory = new StandaloneWebApplicationFactory();
        using var client = factory.CreateClient();

        using var anonymous = await client.PostAsync(
            "/api/intake/uploads",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/intake/uploads");
        request.Headers.Add(ProductCallerAuthenticationOptions.ApiKeyHeaderName, CallerKey);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        // Authenticated but not multipart: the intake API rejects the request
        // shape, proving the endpoint is wired behind the caller credential.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static class ProductCallerAuthenticationOptions
    {
        public const string ApiKeyHeaderName = "X-Emenda-Api-Key";
    }

    private sealed class StandaloneWebApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string[] OwnedEnvironmentKeys =
        [
            "Emenda__Runtime__Mode",
            "Emenda__Security__Callers__0__Name",
            "Emenda__Security__Callers__0__ApiKey",
            "ConnectionStrings__Emenda",
            "Mistake__ConfigMode",
            "Mistake__Student__Enabled",
            "Mistake__Workers__UploadPollingWorker__Enabled",
            "Mistake__Workers__ReanalyzeJobHostedService__Enabled",
            "Mistake__Workers__ReanalyzeJobCleanupService__Enabled",
            "Oss__InternalEndpoint",
            "Oss__InternalSecure",
            "Oss__AccessKey",
            "Oss__SecretKey",
            "Oss__BucketName",
            "Oss__PublicBaseUrl",
            "IdentityService__Authority",
            "IdentityService__Issuer",
            "IdentityService__Audience",
            "IdentityService__AdminAudience",
            "IdentityService__MistakeApiStrictAuth",
            "IdentityService__MistakeApiResourceAuth",
        ];

        public StandaloneWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("Emenda__Runtime__Mode", "Standalone");
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Emenda",
                "Host=127.0.0.1;Port=5432;Database=emenda_standalone_tests;Username=emenda;Password=standalone-tests-only");
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", null);
            Environment.SetEnvironmentVariable("Mistake__Student__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__UploadPollingWorker__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobHostedService__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobCleanupService__Enabled", "false");
            Environment.SetEnvironmentVariable("Oss__InternalEndpoint", "127.0.0.1:19031");
            Environment.SetEnvironmentVariable("Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Oss__AccessKey", "standalone-test-access");
            Environment.SetEnvironmentVariable("Oss__SecretKey", "standalone-test-secret");
            Environment.SetEnvironmentVariable("Oss__BucketName", "emenda-standalone-tests");
            Environment.SetEnvironmentVariable("Oss__PublicBaseUrl", "http://127.0.0.1/oss");
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
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var databaseName = $"emenda_standalone_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        }
    }
}