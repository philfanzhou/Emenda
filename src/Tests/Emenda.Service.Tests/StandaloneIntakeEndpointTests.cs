using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Service.Services;
using Xunit;

namespace Emenda.Service.Tests;

/// <summary>
/// End-to-end product intake through the real standalone host: caller
/// credential -> multipart intake -> OSS storage -> VL grouping -> mistake
/// item creation -> image migration to the mistakes/ prefix. OSS is replaced
/// by the local file implementation and the VL analyzer by a deterministic
/// fake; no Student, Identity, Consul or PostgreSQL is involved.
/// </summary>
public sealed class StandaloneIntakeEndpointTests
{
    private const string CallerName = "intake-tests";
    private const string CallerKey = "intake-test-api-key-0123456789";
    private const string IntakePath = "/api/intake/uploads";

    [Fact]
    public async Task StandaloneMode_IntakeCreatesMistakeAndMigratesImagesAsync()
    {
        using var factory = new StandaloneIntakeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", CallerKey);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-42"), "studentId");
        form.Add(new StringContent("source-key-1"), "sourceKey");
        form.Add(PngPart("page-1.png"), "images", "page-1.png");
        form.Add(PngPart("page-2.png"), "images", "page-2.png");

        using var response = await client.PostAsync(IntakePath, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("sourceId").GetString().Should().Be("source-key-1");
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        var itemsInDb = await db.MistakeItems.AsNoTracking().ToListAsync();
        itemsInDb.Should().HaveCount(1);
        itemsInDb[0].StudentId.Should().Be("student-42");
        itemsInDb[0].SourceRegions.Should().NotBeEmpty();
        itemsInDb[0].SourceRegions.Should().OnlyContain(region =>
            region.SourceImagePath.StartsWith("mistakes/"));
    }

    [Fact]
    public async Task StandaloneMode_IntakeIsIdempotentBySourceKeyAsync()
    {
        using var factory = new StandaloneIntakeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", CallerKey);

        using var first = await PostIntakeAsync(client, "student-7", "same-source");
        using var second = await PostIntakeAsync(client, "student-7", "same-source");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        var itemsInDb = await db.MistakeItems.AsNoTracking().ToListAsync();
        itemsInDb.Should().HaveCount(1);
    }

    private static async Task<HttpResponseMessage> PostIntakeAsync(
        HttpClient client, string studentId, string sourceKey)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(studentId), "studentId");
        form.Add(new StringContent(sourceKey), "sourceKey");
        form.Add(PngPart("page.png"), "images", "page.png");
        return await client.PostAsync(IntakePath, form);
    }

    private static ByteArrayContent PngPart(string fileName)
    {
        // Minimal valid PNG signature + payload placeholder; the fake analyzer
        // does not decode pixels and the local OSS stores bytes opaquely.
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"images\"",
            FileName = $"\"{fileName}\""
        };
        return part;
    }

    private sealed class FakeVlAnalyzer : IVlAnalyzer
    {
        public Task<VlAnalysisResult> AnalyzeAsync(
            string studentId, List<string> imagePaths, CancellationToken ct = default)
        {
            var result = new VlAnalysisResult
            {
                Groups =
                {
                    new VlImageGroup
                    {
                        ImageIndices = Enumerable.Range(0, imagePaths.Count).ToList(),
                        Subject = 2,
                        Grade = 8,
                        RootCause = "test grouping"
                    }
                }
            };
            return Task.FromResult(result);
        }

        public Task<VlReanalysisResult> ReAnalyzeAsync(
            string studentId,
            List<string> imagePaths,
            string existingRootCause,
            string? teacherDescription,
            CancellationToken ct = default) =>
            Task.FromResult(new VlReanalysisResult { RootCause = existingRootCause });
    }

    private sealed class StandaloneIntakeWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _ossRoot =
            Path.Combine(Path.GetTempPath(), $"emenda-intake-tests-{Guid.NewGuid():N}");

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

        public StandaloneIntakeWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("Emenda__Runtime__Mode", "Standalone");
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Emenda",
                "Host=127.0.0.1;Port=5432;Database=emenda_intake_tests;Username=emenda;Password=intake-tests-only");
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", null);
            Environment.SetEnvironmentVariable("Mistake__Student__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__UploadPollingWorker__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobHostedService__Enabled", "false");
            Environment.SetEnvironmentVariable("Mistake__Workers__ReanalyzeJobCleanupService__Enabled", "false");
            Environment.SetEnvironmentVariable("Oss__InternalEndpoint", "127.0.0.1:19041");
            Environment.SetEnvironmentVariable("Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Oss__AccessKey", "intake-test-access");
            Environment.SetEnvironmentVariable("Oss__SecretKey", "intake-test-secret");
            Environment.SetEnvironmentVariable("Oss__BucketName", "emenda-intake-tests");
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
            try
            {
                if (Directory.Exists(_ossRoot))
                    Directory.Delete(_ossRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked file must not fail the test run.
            }

            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var databaseName = $"emenda_intake_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));

                services.RemoveAll<IOssService>();
                services.AddSingleton<IOssService>(new LocalFileOssService(_ossRoot));

                services.RemoveAll<IVlAnalyzer>();
                services.AddSingleton<IVlAnalyzer>(new FakeVlAnalyzer());
            });
        }
    }
}