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
/// Product readiness: a deployment must report ready with exactly the
/// components it actually wired (database and object storage), and the
/// caller-credential configuration must fail startup when it has no usable
/// credential. Removed platform settings fail startup instead of being ignored.
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
            .WithMessage("*ConnectionStrings:Emenda is required*");
    }

    [Theory]
    [InlineData("Mistake__ConfigMode", "Local", "*Mistake:ConfigMode (Local/Platform) was removed*")]
    [InlineData("Mistake__ConfigMode", "Platform", "*Mistake:ConfigMode (Local/Platform) was removed*")]
    [InlineData("Emenda__Runtime__Mode", "Platform", "*Emenda:Runtime:Mode only accepts Standalone*")]
    [InlineData("Emenda__Database__SchemaOwnership", "Shared", "*SchemaOwnership=Shared was removed*")]
    [InlineData("Emenda__Database__SchemaOwnership", "Owned", "*must be Own (found 'Owned')*")]
    public void RemovedRuntimeSettings_FailStartup(string key, string value, string message)
    {
        using var factory = new StandaloneStartupFailureWebApplicationFactory(
            extraEnvironment: new Dictionary<string, string?> { [key] = value });

        Action act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    private sealed class StandaloneStartupFailureWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly Action<Dictionary<string, string?>>? _environmentOverrides;
        private readonly IReadOnlyDictionary<string, string?> _extraEnvironment;

        private static readonly string[] OwnedEnvironmentKeys =
        [
            "Emenda__Runtime__Mode",
            "Mistake__ConfigMode",
            "Emenda__Database__SchemaOwnership",
            "Emenda__Security__Callers__0__Name",
            "Emenda__Security__Callers__0__ApiKey",
            "ConnectionStrings__Emenda",
        ];

        public StandaloneStartupFailureWebApplicationFactory(
            IReadOnlyDictionary<string, string?>? extraEnvironment = null,
            Action<Dictionary<string, string?>>? environmentOverrides = null)
        {
            _extraEnvironment = extraEnvironment ?? new Dictionary<string, string?>();
            _environmentOverrides = environmentOverrides;

            var settings = new Dictionary<string, string?>
            {
                ["Emenda__Database__SchemaOwnership"] = null,
                ["Emenda__Security__Callers__0__Name"] = CallerName,
                ["Emenda__Security__Callers__0__ApiKey"] = CallerKey,
                ["ConnectionStrings__Emenda"] =
                    "Host=127.0.0.1;Port=5432;Database=emenda_startup_failure_tests;Username=emenda;Password=startup-failure-tests-only",
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
            "Emenda__Security__Callers__0__Name",
            "Emenda__Security__Callers__0__ApiKey",
            "ConnectionStrings__Emenda",
            "Oss__InternalEndpoint",
            "Oss__InternalSecure",
            "Oss__AccessKey",
            "Oss__SecretKey",
            "Oss__BucketName",
            "Oss__PublicBaseUrl",
        ];

        public StandaloneReadinessWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Emenda",
                "Host=127.0.0.1;Port=5432;Database=emenda_readiness_tests;Username=emenda;Password=readiness-tests-only");
            Environment.SetEnvironmentVariable("Oss__InternalEndpoint", "127.0.0.1:19051");
            Environment.SetEnvironmentVariable("Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Oss__AccessKey", "readiness-test-access");
            Environment.SetEnvironmentVariable("Oss__SecretKey", "readiness-test-secret");
            Environment.SetEnvironmentVariable("Oss__BucketName", "emenda-readiness-tests");
            Environment.SetEnvironmentVariable("Oss__PublicBaseUrl", "http://127.0.0.1/oss");
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