using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
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
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Xunit;

namespace Emenda.Service.Tests;

/// <summary>
/// Emenda has no Student service. A review rejection persists the Rejected
/// status and its ProcessLog, keeps the source images where they are, and
/// returns the normal success response.
/// </summary>
public sealed class StandaloneReviewRejectTests
{
    private const string CallerName = "standalone-reject-tests";
    private const string CallerKey = "standalone-reject-api-key-0123456789";
    private const string ApiKeyHeaderName = "X-Emenda-Api-Key";
    private const string StudentId = "standalone-student-1";
    private const string SourceImagePath = "mistakes/standalone-1/a.png";

    [Fact]
    public async Task StandaloneMode_RejectsMistakeLocallyWithoutStudent()
    {
        using var factory = new StandaloneRejectWebApplicationFactory();
        using var client = factory.CreateClient();
        var (itemId, sourceImagePath) = await factory.SeedPendingReviewItemAsync();

        using var response = await client.SendAsync(ReviewRequest(
            itemId,
            CallerKey,
            """{"reviewStatus":3,"reviewerId":"reviewer-1","rootCause":"图片模糊"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":true");
        body.Should().Contain($"\"studentId\":\"{StudentId}\"");
        body.Should().Contain("\"sourceUploadId\":\"upload-standalone-1\"");

        var item = await factory.ReadItemAsync(itemId);
        item.ReviewStatus.Should().Be((int)ReviewStatus.Rejected);
        item.ReviewerId.Should().Be("reviewer-1");
        item.RootCause.Should().Be("图片模糊");
        item.ReviewedAt.Should().NotBeNull();

        // The images stay in place: same source region, no OSS copy/delete.
        item.SourceRegions.Should().ContainSingle();
        item.SourceRegions[0].SourceImagePath.Should().Be(sourceImagePath);
        factory.Oss.Copies.Should().BeEmpty();
        factory.Oss.Deletes.Should().BeEmpty();

        // Normal process log history: the pre-existing Created entry is retained
        // and the rejection appends ReviewRejected.
        var logActions = await factory.ReadProcessLogActionsAsync(itemId);
        logActions.Should().Contain((int)ProcessAction.Created);
        logActions.Should().Contain((int)ProcessAction.ReviewRejected);
    }

    [Fact]
    public async Task StandaloneMode_RejectStillRequiresCallerKeyAsync()
    {
        using var factory = new StandaloneRejectWebApplicationFactory();
        using var client = factory.CreateClient();
        var (itemId, _) = await factory.SeedPendingReviewItemAsync();

        using var response = await client.SendAsync(ReviewRequest(
            itemId,
            apiKey: null,
            """{"reviewStatus":3,"reviewerId":"reviewer-1"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.ReadItemAsync(itemId)).ReviewStatus.Should().Be((int)ReviewStatus.PendingReview);
    }

    [Fact]
    public async Task StandaloneMode_RejectOfAlreadyReviewedItemReturnsBadRequestAsync()
    {
        using var factory = new StandaloneRejectWebApplicationFactory();
        using var client = factory.CreateClient();
        var (itemId, _) = await factory.SeedPendingReviewItemAsync();
        var body = """{"reviewStatus":3,"reviewerId":"reviewer-1"}""";

        using var first = await client.SendAsync(ReviewRequest(itemId, CallerKey, body));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await client.SendAsync(ReviewRequest(itemId, CallerKey, body));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await factory.ReadItemAsync(itemId)).ReviewStatus.Should().Be((int)ReviewStatus.Rejected);
        var logActions = await factory.ReadProcessLogActionsAsync(itemId);
        logActions.Count(action => action == (int)ProcessAction.ReviewRejected).Should().Be(1);
    }

    [Fact]
    public async Task StandaloneMode_ConfirmSemanticsStayUnchangedAsync()
    {
        using var factory = new StandaloneRejectWebApplicationFactory();
        using var client = factory.CreateClient();
        var (itemId, _) = await factory.SeedPendingReviewItemAsync();

        using var response = await client.SendAsync(ReviewRequest(
            itemId,
            CallerKey,
            """{"reviewStatus":2,"reviewerId":"reviewer-1","rootCause":"题干识别正确"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":true");

        var item = await factory.ReadItemAsync(itemId);
        item.ReviewStatus.Should().Be((int)ReviewStatus.Confirmed);
        var logActions = await factory.ReadProcessLogActionsAsync(itemId);
        logActions.Should().Contain((int)ProcessAction.ReviewConfirmed);
        factory.Oss.Copies.Should().BeEmpty();
        factory.Oss.Deletes.Should().BeEmpty();
    }

    private static HttpRequestMessage ReviewRequest(Guid itemId, string? apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/mistakes/{itemId}/review")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (apiKey is not null)
            request.Headers.Add(ApiKeyHeaderName, apiKey);
        return request;
    }

    /// <summary>
    /// Deployment shaped like the production one: product caller credentials
    /// only and an in-memory database. OSS is replaced with a recorder so an
    /// accidental OSS copy/delete fails the assertion instead of passing silently.
    /// </summary>
    private sealed class StandaloneRejectWebApplicationFactory : WebApplicationFactory<Program>
    {
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

        public RecordingOssService Oss { get; } = new();

        public StandaloneRejectWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
            Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Emenda",
                "Host=127.0.0.1;Port=5432;Database=emenda_standalone_reject_tests;Username=emenda;Password=standalone-reject-tests-only");
            Environment.SetEnvironmentVariable("Oss__InternalEndpoint", "127.0.0.1:19033");
            Environment.SetEnvironmentVariable("Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Oss__AccessKey", "standalone-reject-access");
            Environment.SetEnvironmentVariable("Oss__SecretKey", "standalone-reject-secret");
            Environment.SetEnvironmentVariable("Oss__BucketName", "emenda-standalone-reject-tests");
            Environment.SetEnvironmentVariable("Oss__PublicBaseUrl", "http://127.0.0.1/oss");
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
            var databaseName = $"emenda_standalone_reject_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));

                services.RemoveAll<IOssService>();
                services.AddSingleton<IOssService>(Oss);
            });
        }

        public async Task<(Guid ItemId, string SourceImagePath)> SeedPendingReviewItemAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            var id = Guid.NewGuid();
            db.MistakeItems.Add(new MistakeItemEntity
            {
                Id = id,
                StudentId = StudentId,
                Subject = 2,
                Grade = 3,
                SourceUploadId = "upload-standalone-1",
                SourceRegions = [new SourceRegionEntry { SourceImagePath = SourceImagePath }],
                RootCause = "初始原因",
                ReviewStatus = (int)ReviewStatus.PendingReview,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.ProcessLogs.Add(new ProcessLogEntity
            {
                Id = Guid.NewGuid(), MistakeItemId = id, StudentId = StudentId,
                Action = (int)ProcessAction.Created, Details = "seeded", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return (id, SourceImagePath);
        }

        public async Task<MistakeItemEntity> ReadItemAsync(Guid itemId)
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MistakeDbContext>()
                .MistakeItems.AsNoTracking().SingleAsync(item => item.Id == itemId);
        }

        public async Task<List<int>> ReadProcessLogActionsAsync(Guid itemId)
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MistakeDbContext>()
                .ProcessLogs.AsNoTracking()
                .Where(log => log.MistakeItemId == itemId)
                .OrderBy(log => log.CreatedAt)
                .Select(log => log.Action)
                .ToListAsync();
        }
    }

    internal sealed class RecordingOssService : IOssService
    {
        public List<(string Source, string Dest)> Copies { get; } = new();
        public List<string> Deletes { get; } = new();

        public Task<bool> ObjectExistsAsync(string objectPath) => Task.FromResult(true);

        public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600) =>
            Task.FromResult($"https://oss.example/{objectPath}?test-signature");

        public Task<string> UploadAsync(Stream stream, string fileName, string contentType,
            OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();

        public Task<string> UploadAsync(byte[] data, string fileName, string contentType,
            OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();

        public Task<List<string>> UploadManyAsync(
            IEnumerable<(Stream stream, string fileName, string contentType)> files,
            OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();

        public Task<Stream> DownloadAsync(string objectPath) => throw new NotImplementedException();

        public Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths) =>
            throw new NotImplementedException();

        public Task CopyObjectAsync(string sourcePath, string destPath)
        {
            Copies.Add((sourcePath, destPath));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string objectPath)
        {
            Deletes.Add(objectPath);
            return Task.FromResult(true);
        }

        public Task<int> DeleteManyAsync(IEnumerable<string> objectPaths) =>
            throw new NotImplementedException();

        public Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600) =>
            throw new NotImplementedException();

        public Task<bool> CheckConnectivityAsync() => throw new NotImplementedException();

        public Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null) =>
            throw new NotImplementedException();

        public Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null) =>
            throw new NotImplementedException();
    }
}