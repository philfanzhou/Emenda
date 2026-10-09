using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Mistake.StudentClient;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Xunit;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEY4 acceptance tests for the review reject flow's Student callback:
/// the request must carry the deterministic operationId derived from the
/// item, and callback failures must not be reported as a successful review
/// (transport-unknown points at the read-back query, payload-conflict is
/// explicit). Runs the real host with strict+resource auth enabled, EF Core
/// InMemory, a recording gateway or the real Student client + Host adapter,
/// and an OSS fake that supports the
/// reject-flow copy/delete pair.
/// </summary>
public sealed class ReviewRejectCallbackTests
{
    private const string Issuer = "http://localhost:5002";
    private const string SharedAudience = "QuantumZhou.microservices";
    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("ikjey4-review-reject-signing-key"));
    private const string StaffSub = "staff-reject-1";
    private const string StudentId = "student-reject-1";

    private static Guid ItemId { get; } = Guid.Parse("40000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task RejectCallbackSucceeded_ReportsSuccessAndSendsDerivedOperationId()
    {
        using var factory = NewFactory(out var gateway);
        gateway.NextReturnedRecordResult = new CreateReturnedUploadRecordResult
        {
            Status = CreateReturnedUploadRecordStatus.Succeeded,
            Replayed = false
        };
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            Post($"/api/mistakes/{ItemId}/review", $$"""{"id":"{{ItemId}}","reviewStatus":3,"reviewerId":"{{StaffSub}}","returnReason":"图片模糊"}""", Token(StaffSub, "teacher")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":true");
        body.Should().NotContain("operationId").And.NotContain("returnState").And.NotContain("confirmedRecordId").And.NotContain("errorKind");

        var call = Assert.Single(gateway.ReturnedRecordCalls);
        Assert.Equal(StudentId, call.StudentId);
        Assert.Equal($"mistake-return:{ItemId}", call.OperationId);
        Assert.Equal("Rejected", call.Comments);
        Assert.Equal("图片模糊", call.ReturnReason);
        var entry = Assert.Single(call.Entries);
        Assert.Equal("uploads/reject-1/a.png", entry.Path);
        Assert.Equal("mistake", entry.Type);

        // The reject flow still performed its own side effects.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            db.MistakeItems.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task RejectCallbackUnknown_ReportsFailureWithReadBackHint()
    {
        using var factory = NewFactory(out var gateway);
        gateway.NextReturnedRecordResult = new CreateReturnedUploadRecordResult
        {
            Status = CreateReturnedUploadRecordStatus.Unknown,
            Message = "connection refused"
        };
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            Post($"/api/mistakes/{ItemId}/review", $$"""{"id":"{{ItemId}}","reviewStatus":3,"reviewerId":"{{StaffSub}}"}""", Token(StaffSub, "teacher")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":false");
        body.Should().Contain($"mistake-return:{ItemId}");
        body.Should().Contain("结果未知");
        body.Should().Contain($"/api/uploads/returned/mistake-return:{ItemId}");
        Assert.Single(gateway.ReturnedRecordCalls);
    }

    [Fact]
    public async Task RejectCallbackConflict_ReportsFailureWithConflictMessage()
    {
        using var factory = NewFactory(out var gateway);
        gateway.NextReturnedRecordResult = new CreateReturnedUploadRecordResult
        {
            Status = CreateReturnedUploadRecordStatus.PayloadConflict,
            Message = "different payload"
        };
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            Post($"/api/mistakes/{ItemId}/review", $$"""{"id":"{{ItemId}}","reviewStatus":3,"reviewerId":"{{StaffSub}}"}""", Token(StaffSub, "teacher")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":false");
        body.Should().Contain("载荷冲突");
        body.Should().Contain($"mistake-return:{ItemId}");
        Assert.Single(gateway.ReturnedRecordCalls);
    }

    [Fact]
    public async Task RejectCallbackSucceededReplay_LogsReplayWithoutFailingTheReview()
    {
        using var factory = NewFactory(out var gateway);
        gateway.NextReturnedRecordResult = new CreateReturnedUploadRecordResult
        {
            Status = CreateReturnedUploadRecordStatus.Succeeded,
            Replayed = true
        };
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            Post($"/api/mistakes/{ItemId}/review", $$"""{"id":"{{ItemId}}","reviewStatus":3,"reviewerId":"{{StaffSub}}"}""", Token(StaffSub, "teacher")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task RejectRoleGateAndReviewerBindingStillHold()
    {
        using var factory = NewFactory(out var gateway);
        using var client = factory.CreateClient();
        string Body(string reviewer) =>
            $$"""{"id":"{{ItemId}}","reviewStatus":3,"reviewerId":"{{reviewer}}"}""";

        (await client.SendAsync(Post($"/api/mistakes/{ItemId}/review", Body(StaffSub), Token("learner-1", "student"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post($"/api/mistakes/{ItemId}/review", Body("someone-else"), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        gateway.ReturnedRecordCalls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(CreateReturnedRecordHttpClientTests.UnreadableResponses), MemberType = typeof(CreateReturnedRecordHttpClientTests))]
    public async Task RejectUnreadableRealClientResponse_ReportsFailureAfterExistingSideEffects(HttpStatusCode status, string upstreamBody)
    {
        var handler = new ReturnedResponseHandler(status, upstreamBody);
        using var factory = new RejectCallbackWebApplicationFactory(null, handler);
        factory.SeedData();
        handler.BeforeSend = () =>
        {
            Assert.Equal(("mistakes/reject-1/a.png", "uploads/reject-1/a.png"), Assert.Single(factory.Oss.Copies));
            Assert.Equal("mistakes/reject-1/a.png", Assert.Single(factory.Oss.Deletes));
            using var scope = factory.Services.CreateScope();
            Assert.Empty(scope.ServiceProvider.GetRequiredService<MistakeDbContext>().MistakeItems);
        };
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post($"/api/mistakes/{ItemId}/review",
            $$"""{"reviewStatus":3,"reviewerId":"{{StaffSub}}","returnReason":"图片模糊"}""", Token(StaffSub, "teacher")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("success").GetBoolean());
        var error = data.GetProperty("errorMessage").GetString();
        Assert.Contains($"mistake-return:{ItemId}", error);
        if (status == HttpStatusCode.Conflict)
            Assert.Contains("载荷冲突", error);
        else
        {
            Assert.Contains("结果未知", error);
            Assert.Contains($"GET /api/uploads/returned/mistake-return:{ItemId}", error);
        }
        Assert.DoesNotContain("UPSTREAM_PRIVATE_CANARY", body);
        Assert.DoesNotContain(factory.ClientLogger.Invocations, invocation =>
            invocation.Arguments.Any(argument => argument?.ToString()?.Contains("UPSTREAM_PRIVATE_CANARY") == true));
        Assert.Equal(1, handler.Sends);
        Assert.Equal($"/api/students/{StudentId}/uploads/returned", handler.Path);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal($"mistake-return:{ItemId}", request.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("Rejected", request.RootElement.GetProperty("comments").GetString());
        Assert.Equal("图片模糊", request.RootElement.GetProperty("returnReason").GetString());
        Assert.Equal("uploads/reject-1/a.png", request.RootElement.GetProperty("imageEntries")[0].GetProperty("path").GetString());
        using var finalScope = factory.Services.CreateScope();
        Assert.Empty(finalScope.ServiceProvider.GetRequiredService<MistakeDbContext>().MistakeItems);
        Assert.Single(factory.Oss.Copies);
        Assert.Single(factory.Oss.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectRealClientSuccess_PreservesCreationAndReplay(bool replayed)
    {
        var handler = new ReturnedResponseHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { success = true, data = new { success = true, replayed } }));
        using var factory = new RejectCallbackWebApplicationFactory(null, handler);
        factory.SeedData();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Post($"/api/mistakes/{ItemId}/review",
            $$"""{"reviewStatus":3,"reviewerId":"{{StaffSub}}"}""", Token(StaffSub, "teacher")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("success").GetBoolean());
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectRealClient_DeniedOrDisabledHasNoSideEffects(bool enabled)
    {
        var handler = new ReturnedResponseHandler(HttpStatusCode.OK, "UPSTREAM_PRIVATE_CANARY{");
        using var factory = new RejectCallbackWebApplicationFactory(null, handler, enabled);
        factory.SeedData();
        using var client = factory.CreateClient();
        string Body(string reviewer) => $$"""{"reviewStatus":3,"reviewerId":"{{reviewer}}"}""";
        if (enabled)
        {
            using var roleResponse = await client.SendAsync(Post($"/api/mistakes/{ItemId}/review", Body(StaffSub), Token("learner-1", "student")));
            Assert.Equal(HttpStatusCode.Forbidden, roleResponse.StatusCode);
            using var reviewerResponse = await client.SendAsync(Post($"/api/mistakes/{ItemId}/review", Body("someone-else"), Token(StaffSub, "teacher")));
            Assert.Equal(HttpStatusCode.Forbidden, reviewerResponse.StatusCode);
        }
        else
        {
            using var response = await client.SendAsync(Post($"/api/mistakes/{ItemId}/review", Body(StaffSub), Token(StaffSub, "teacher")));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        Assert.Equal(0, handler.Sends);
        Assert.Empty(factory.Oss.Copies);
        Assert.Empty(factory.Oss.Deletes);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, Assert.Single(scope.ServiceProvider.GetRequiredService<MistakeDbContext>().MistakeItems).ReviewStatus);
    }

    private sealed class ReturnedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public Action? BeforeSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeforeSend?.Invoke();
            Sends++;
            Path = request.RequestUri?.PathAndQuery;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // ===== Helpers =====

    private static RejectCallbackWebApplicationFactory NewFactory(out RecordingGateway gateway)
    {
        gateway = new RecordingGateway();
        var factory = new RejectCallbackWebApplicationFactory(gateway);
        factory.SeedData();
        return factory;
    }

    private static string Token(string sub, string role)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, sub),
            new("unique_name", sub),
            new("role", role)
        };
        var token = new JwtSecurityToken(
            Issuer, SharedAudience, claims,
            notBefore: now.AddMinutes(-5),
            expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage Post(string path, string json, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal sealed class RecordingGateway : IUploadSourceGateway
    {
        public List<(string StudentId, List<UploadImageEntry> Entries, string? Comments, string? ReturnReason, string? OperationId)> ReturnedRecordCalls { get; } = new();

        public CreateReturnedUploadRecordResult NextReturnedRecordResult { get; set; } = new()
        {
            Status = CreateReturnedUploadRecordStatus.Succeeded
        };

        public Task<CreateReturnedUploadRecordResult> CreateReturnedRecordAsync(
            string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken cancellationToken = default)
        {
            ReturnedRecordCalls.Add((studentId, imageEntries.ToList(), comments, returnReason, operationId));
            return Task.FromResult(NextReturnedRecordResult);
        }

        public Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(string identityAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AllowedStudentsResolution.Unavailable);
    }

    internal sealed class RejectFlowOssService : IOssService
    {
        public List<(string Source, string Dest)> Copies { get; } = new();
        public List<string> Deletes { get; } = new();

        public Task<bool> ObjectExistsAsync(string objectPath) => Task.FromResult(true);
        public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600) =>
            Task.FromResult($"https://oss.example/{objectPath}?test-signature");
        public Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();
        public Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();
        public Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectPath) => throw new NotImplementedException();
        public Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths) => throw new NotImplementedException();

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

        public Task<int> DeleteManyAsync(IEnumerable<string> objectPaths) => throw new NotImplementedException();
        public Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600) =>
            throw new NotImplementedException();
        public Task<bool> CheckConnectivityAsync() => throw new NotImplementedException();
        public Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null) => throw new NotImplementedException();
        public Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null) =>
            throw new NotImplementedException();
    }

    internal sealed class RejectCallbackWebApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string[] OwnedEnvironmentKeys =
        [
            "Mistake__ConfigMode",
            "ConnectionStrings__MistakeLocal",
            "Mistake__Local__Oss__InternalEndpoint",
            "Mistake__Local__Oss__InternalSecure",
            "Mistake__Local__Oss__AccessKey",
            "Mistake__Local__Oss__SecretKey",
            "Mistake__Local__Oss__BucketName",
            "Mistake__Local__Oss__PublicBaseUrl",
            "IdentityService__Authority",
            "IdentityService__Issuer",
            "IdentityService__Audience",
            "IdentityService__RequireHttpsMetadata",
            "IdentityService__AdminAudience",
            "IdentityService__MistakeApiStrictAuth",
            "IdentityService__MistakeApiResourceAuth",
            "Mistake__Student__Enabled",
            "Mistake__Local__Student__Url",
            "IDENTITY_APP_ID_ADMIN",
        ];

        private readonly RecordingGateway? _gateway;
        private readonly HttpMessageHandler? _handler;
        public RejectFlowOssService Oss { get; } = new();
        public Mock<ILogger<StudentHttpClient>> ClientLogger { get; } = new();

        public RejectCallbackWebApplicationFactory(RecordingGateway? gateway, HttpMessageHandler? handler = null, bool studentEnabled = true)
        {
            _gateway = gateway;
            _handler = handler;
            Environment.SetEnvironmentVariable("IDENTITY_APP_ID_ADMIN", null);
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", "Local");
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__MistakeLocal",
                "Host=127.0.0.1;Port=5432;Database=mistake_reject_callback_tests;Username=mistake_tester;Password=reject-callback-tests-only");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__InternalEndpoint", "127.0.0.1:19041");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__AccessKey", "reject-callback-access");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__SecretKey", "reject-callback-secret");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__BucketName", "mistake-reject-callback-tests");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__PublicBaseUrl", "http://127.0.0.1/oss");
            Environment.SetEnvironmentVariable("IdentityService__Authority", Issuer);
            Environment.SetEnvironmentVariable("IdentityService__Issuer", Issuer);
            Environment.SetEnvironmentVariable("IdentityService__Audience", SharedAudience);
            Environment.SetEnvironmentVariable("IdentityService__RequireHttpsMetadata", "false");
            Environment.SetEnvironmentVariable("IdentityService__AdminAudience", null);
            Environment.SetEnvironmentVariable("IdentityService__MistakeApiStrictAuth", "true");
            Environment.SetEnvironmentVariable("IdentityService__MistakeApiResourceAuth", "true");
            // The reject flow fail-closes with 503 when the Student capability
            // is disabled (IKJ8P0); enable it so the callback path is reached.
            // The URL is never contacted — the gateway or HTTP transport is faked.
            Environment.SetEnvironmentVariable("Mistake__Student__Enabled", studentEnabled ? "true" : "false");
            Environment.SetEnvironmentVariable("Mistake__Local__Student__Url", "http://127.0.0.1:19042");
        }

        protected override void Dispose(bool disposing)
        {
            foreach (var key in OwnedEnvironmentKeys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var databaseName = $"mistake_reject_callback_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));

                if (_gateway is not null)
                {
                    services.RemoveAll<IUploadSourceGateway>();
                    services.AddScoped<IUploadSourceGateway>(_ => _gateway);
                }
                else if (_handler is not null)
                {
                    // Preserve the production Host adapter registration; replace only transport.
                    services.RemoveAll<IStudentHttpClient>();
                    services.AddSingleton<IStudentHttpClient>(new StudentHttpClient(
                        new HttpClient(_handler) { BaseAddress = new Uri("http://127.0.0.1:19042") }, ClientLogger.Object));
                }
                services.RemoveAll<IOssService>();
                services.AddSingleton<IOssService>(Oss);

                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, UseStaticSigningKeys);
                services.PostConfigure<JwtBearerOptions>(Ruoyu.Study.Common.Authentication.RuoyuAdminApiAuthorization.SchemeName, UseStaticSigningKeys);
            });
        }

        public void SeedData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            db.MistakeItems.Add(new MistakeItemEntity
            {
                Id = ItemId,
                StudentId = StudentId,
                Subject = 2,
                Grade = 3,
                SourceUploadId = "upload-reject-1",
                SourceRegions = [new SourceRegionEntry { SourceImagePath = "mistakes/reject-1/a.png" }],
                ReviewStatus = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            db.SaveChanges();
        }

        private static void UseStaticSigningKeys(JwtBearerOptions options)
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(SigningKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.Authority = Issuer;
            options.RequireHttpsMetadata = false;
        }
    }
}
