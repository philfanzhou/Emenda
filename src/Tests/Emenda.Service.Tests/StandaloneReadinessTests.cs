using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Host.Security;
using Xunit;

namespace Emenda.Service.Tests;

/// <summary>
/// Standalone product readiness: a deployment with no Consul, no Loki, no
/// SignaCore identity and no Student service must report ready with exactly
/// the components it actually wired (database and object storage), and the
/// caller-credential configuration must fail startup when it has no usable
/// credential.
/// </summary>
public sealed class StandaloneReadinessTests
{
    private const string CallerName = "readiness-tests";
    private const string CallerKey = "readiness-test-api-key-0123456789";

    [Fact]
    public async Task StandaloneMode_ReadinessReportsOnlyDatabaseAndObjectStorageAsync()
    {
        using var factory = new StandaloneReadinessWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("ok");
        var checks = body.RootElement.GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("name").GetString())
            .ToList();
        checks.Should().Equal("db", "oss");
        body.RootElement.GetProperty("checks").EnumerateArray()
            .Should().OnlyContain(check => check.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void StandaloneCallerAuthentication_FailsWithoutAnyCredential()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Emenda:Security:Enabled"] = "true",
                ["Emenda:Security:AllowAnonymous"] = "false"
            })
            .Build();
        var services = new ServiceCollection();

        Action act = () => services.AddProductCallerAuthentication(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Callers*ApiKey*");
    }

    [Fact]
    public void StandaloneCallerAuthentication_AllowsAnonymousOnlyForLocalDevelopment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Emenda:Security:Enabled"] = "true",
                ["Emenda:Security:AllowAnonymous"] = "true"
            })
            .Build();
        var services = new ServiceCollection();

        Action act = () => services.AddProductCallerAuthentication(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void StandaloneMode_RequiresExplicitEmendaConnection()
    {
        // Standalone must resolve only ConnectionStrings:Emenda. The inherited
        // ConnectionStrings:Default (the legacy platform example, pointing at
        // the running mistake database) is never used implicitly: connecting to
        // the legacy database is a deliberate coexistence decision declared
        // through the explicit Emenda connection.
        using var factory = new StandaloneStartupFailureWebApplicationFactory(
            environmentOverrides: settings => settings.Remove("ConnectionStrings__Emenda"));

        Action act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires ConnectionStrings:Emenda*");
    }

    [Fact]
    public void SharedSchemaOwnership_IsAStandaloneOnlySetting()
    {
        using var factory = new StandaloneStartupFailureWebApplicationFactory(
            standalone: false,
            extraEnvironment: new Dictionary<string, string?>
            {
                ["Mistake__ConfigMode"] = "Local",
                ["Emenda__Database__SchemaOwnership"] = "Shared"
            });

        Action act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SchemaOwnership=Shared is a standalone-mode setting*");
    }

    private sealed class StandaloneStartupFailureWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly Action<Dictionary<string, string?>>? _environmentOverrides;
        private readonly IReadOnlyDictionary<string, string?> _extraEnvironment;

        private static readonly string[] OwnedEnvironmentKeys =
        [
            "Emenda__Runtime__Mode",
            "Emenda__Database__SchemaOwnership",
            "Emenda__Security__Callers__0__Name",
            "Emenda__Security__Callers__0__ApiKey",
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

        public StandaloneStartupFailureWebApplicationFactory(
            bool standalone = true,
            IReadOnlyDictionary<string, string?>? extraEnvironment = null,
            Action<Dictionary<string, string?>>? environmentOverrides = null)
        {
            _extraEnvironment = extraEnvironment ?? new Dictionary<string, string?>();
            _environmentOverrides = environmentOverrides;

            var settings = new Dictionary<string, string?>
            {
                ["Emenda__Runtime__Mode"] = standalone ? "Standalone" : null,
                ["Emenda__Database__SchemaOwnership"] = null,
                ["Emenda__Security__Callers__0__Name"] = CallerName,
                ["Emenda__Security__Callers__0__ApiKey"] = CallerKey,
                ["ConnectionStrings__Emenda"] =
                    "Host=127.0.0.1;Port=5432;Database=emenda_startup_failure_tests;Username=emenda;Password=startup-failure-tests-only",
                ["Mistake__ConfigMode"] = null,
                ["Mistake__Student__Enabled"] = "false",
                ["Mistake__Workers__UploadPollingWorker__Enabled"] = "false",
                ["Mistake__Workers__ReanalyzeJobHostedService__Enabled"] = "false",
                ["Mistake__Workers__ReanalyzeJobCleanupService__Enabled"] = "false",
                ["VlAnalyzer__ApiKey"] = null,
                ["IdentityService__Authority"] = null,
                ["IdentityService__Issuer"] = null,
                ["IdentityService__Audience"] = null,
                ["IdentityService__AdminAudience"] = null,
                ["IdentityService__MistakeApiStrictAuth"] = null,
                ["IdentityService__MistakeApiResourceAuth"] = null,
            };
            foreach (var pair in _extraEnvironment)
                settings[pair.Key] = pair.Value;
            _environmentOverrides?.Invoke(settings);
            foreach (var pair in settings)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
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
            var databaseName = $"emenda_startup_failure_{Guid.NewGuid():N}";
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        }
    }

    private sealed class StandaloneReadinessWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _ossRoot =
            Path.Combine(Path.GetTempPath(), $"emenda-readiness-tests-{Guid.NewGuid():N}");

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
            "VlAnalyzer__ApiKey",
            "IdentityService__Authority",
            "IdentityService__Issuer",
            "IdentityService__Audience",
            "IdentityService__AdminAudience",
            "IdentityService__MistakeApiStrictAuth",
            "IdentityService__MistakeApiResourceAuth",
        ];

        public StandaloneReadinessWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("Emenda__Runtime__Mode", "Standalone");
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Emenda",
                "Host=127.0.0.1;Port=5432;Database=emenda_readiness_tests;Username=emenda;Password=readiness-tests-only");
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", null);
            Environment.SetEnvironmentVariable("Mistake__Student__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__UploadPollingWorker__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobHostedService__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobCleanupService__Enabled", "false");
            Environment.SetEnvironmentVariable("Oss__InternalEndpoint", "127.0.0.1:19051");
            Environment.SetEnvironmentVariable("Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Oss__AccessKey", "readiness-test-access");
            Environment.SetEnvironmentVariable("Oss__SecretKey", "readiness-test-secret");
            Environment.SetEnvironmentVariable("Oss__BucketName", "emenda-readiness-tests");
            Environment.SetEnvironmentVariable("Oss__PublicBaseUrl", "http://127.0.0.1/oss");
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
                if (Directory.Exists(_ossRoot))
                    Directory.Delete(_ossRoot, recursive: true);
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
            var databaseName = $"emenda_readiness_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));

                services.RemoveAll<IOssService>();
                services.AddSingleton<IOssService>(new LocalFileOssService(_ossRoot));
            });
        }
    }
}