using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.TestFixtures;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Authentication;
using Emenda.Host.Security;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Emenda.Service.Tests;

public sealed class ExtractionHttpPostgresTests
{
    [Fact]
    public async Task RealHost_AuthenticatesCallersAndCsrf_AndCorrectsWithoutLeakingServiceCredentials()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await caller.GetAsync("/api/intake/extractions")).StatusCode);
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-http"), "studentId");
        form.Add(new StringContent("4"), "grade");
        form.Add(new StringContent("worksheet-http"), "sourceKey");
        var originalBytes = TestPng();
        var image = new ByteArrayContent(originalBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "images", "page.png");
        using var uploaded = await caller.PostAsync("/api/intake/extractions", form);
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        var job = await Data<ExtractionJobView>(uploaded);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().RunQueuedAsync(default);
        var result = await Data<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"));
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("12", result.Questions.Single().Question.QuestionNumber);
        Assert.NotNull(result.Questions.Single().ItemId);
        var question = result.Questions.Single().Question;
        var crop = new ExtractionRevisionRequest(Guid.NewGuid(), result.Version, "Narrow the crop",
            [question with { Regions = [question.Regions.Single() with { X1 = .5 }] }]);
        var route = $"/api/intake/extractions/{job.Id}/questions";
        using var anonymousAdmin = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousAdmin.PutAsJsonAsync(route, crop)).StatusCode);
        var cropped = await Data<ExtractionJobView>(await caller.PutAsJsonAsync(route, crop));
        Assert.Equal(2, cropped.Version);
        Assert.Equal(1, cropped.Questions.Single().Images.Single().Width);
        Assert.Equal(result.Questions.Single().ItemId, cropped.Questions.Single().ItemId);
        await using (var check = database.CreateContext())
            Assert.Equal((int)ReviewStatus.PendingReview, (await check.MistakeItems.SingleAsync()).ReviewStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await caller.PutAsJsonAsync(route, crop with { RequestKey = Guid.NewGuid() })).StatusCode);
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), cropped.Version, "Correct hand-written answer",
            [cropped.Questions.Single().Question with { QuestionNumber = "三、2（1）", Outcome = "correct", StudentAnswer = "2", Evidence = "The actual answer is 2" }]);
        var anonymousResponse = await anonymousAdmin.GetAsync("/admin/api/extractions");
        Assert.NotEqual(HttpStatusCode.OK, anonymousResponse.StatusCode);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        var sessionKey = await factory.SeedSessionAsync(admin);
        using var session = await admin.GetAsync("/admin/auth/oidc/session");
        Assert.True(session.IsSuccessStatusCode, $"{session.StatusCode}: {await session.Content.ReadAsStringAsync()}");
        using var adminList = await admin.GetAsync("/admin/api/extractions");
        Assert.True(adminList.StatusCode == HttpStatusCode.OK, $"{adminList.StatusCode}: {await adminList.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PutAsJsonAsync($"/admin/api/extractions/{job.Id}/questions", request)).StatusCode);
        await factory.AddCsrfAsync(admin, sessionKey);
        using var removedUpload = await admin.PostAsync("/admin/api/extractions", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, removedUpload.StatusCode);
        var revised = await Data<ExtractionJobView>(await admin.PutAsJsonAsync($"/admin/api/extractions/{job.Id}/questions", request));
        Assert.Equal(3, revised.Version);
        Assert.Equal("correct", revised.Questions.Single().Question.Outcome);
        Assert.Equal("三、2（1）", revised.Questions.Single().Question.QuestionNumber);
        Assert.Equal(result.Questions.Single().ItemId, revised.Questions.Single().ItemId);
        var history = await admin.GetAsync($"/admin/api/extractions/{job.Id}/versions/1");
        Assert.Equal(HttpStatusCode.Gone, history.StatusCode);
        Assert.Contains("version_retired", await history.Content.ReadAsStringAsync());
        var original = await admin.GetAsync($"/admin/api/extractions/{job.Id}/sources/{job.Sources[0].Id}");
        Assert.Equal(originalBytes, await original.Content.ReadAsByteArrayAsync());
        using var page = await admin.GetAsync("/admin/extractions");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("错题入库", html);
        Assert.DoesNotContain("type=\"file\"", html);
        Assert.DoesNotContain("uploadForm", html);
        Assert.DoesNotContain(ExtractionHostFixture.CallerKey, html);
        await using var db = database.CreateContext();
        Assert.Equal(3, (await db.MistakeItems.SingleAsync()).ReviewStatus);
    }

    [Fact]
    public async Task RealHost_RejectsMalformedEnvelopesAndImageMismatchBeforeCreatingTask()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await client.PostAsJsonAsync("/api/intake/extractions", new { studentId = "student" })).StatusCode);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student"), "studentId");
        form.Add(new StringContent("3"), "grade");
        form.Add(new StringContent("wrong-image"), "sourceKey");
        var image = new ByteArrayContent([1, 2, 3]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "images", "page.png");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/intake/extractions", form)).StatusCode);
        var list = await Data<List<ExtractionJobView>>(await client.GetAsync("/api/intake/extractions"));
        Assert.Empty(list);
    }

    [Fact]
    public async Task ImagesOnly_UsesSignedStudentAndDeduplicatesOnlyWithinThatStudent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var signer = RSA.Create(2048);
        using var root = new ExtractionHostFixture(database.Connection);
        const string trust = "Emenda:Security:PortalStudentAssertions:0:";
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [trust + "CallerName"] = "http-tests", [trust + "Issuer"] = "https://portal.example",
                [trust + "Audience"] = "emenda-images", [trust + "PublicKeyPem"] = signer.ExportSubjectPublicKeyInfoPem()
            })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        async Task<HttpResponseMessage> Submit(string? student, bool extraField = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PortalStudentAssertion.UploadPath);
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(TestPng());
            image.Headers.ContentType = new("image/png");
            form.Add(image, "images", "page.png");
            if (extraField) form.Add(new StringContent("different-student"), "studentId");
            request.Content = form;
            if (student is not null)
            {
                var claims = PortalStudentAssertionTests.Claims();
                claims["sub"] = student; claims["caller"] = "http-tests";
                request.Headers.Add(PortalStudentAssertion.HeaderName, PortalStudentAssertionTests.Sign(signer, claims));
            }
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Submit(null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit("student-a", extraField: true)).StatusCode);
        var first = (await Data<JsonElement>(await Submit("student-a"))).GetProperty("id").GetGuid();
        var replay = (await Data<JsonElement>(await Submit("student-a"))).GetProperty("id").GetGuid();
        var other = (await Data<JsonElement>(await Submit("student-b"))).GetProperty("id").GetGuid();
        Assert.Equal(first, replay);
        Assert.NotEqual(first, other);
        await using var db = database.CreateContext();
        var stored = await db.ExtractionJobs.FindAsync(first);
        Assert.Equal("student-a", stored!.StudentId);
        Assert.Null(stored.Grade);
    }

    [Fact]
    public async Task Operations_ListsAcrossCallers_RequiresGrantAndCsrf_AndRemovesDeletedTask()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var agent = factory.CreateClient();
        const string ops = "/api/ops/extractions";
        const string key = "fixture-only-operations-credential-for-local-testing";
        using var scope = factory.Services.CreateScope();
        var callers = scope.ServiceProvider.GetRequiredService<DynamicCallerStore>();
        var service = scope.ServiceProvider.GetRequiredService<ExtractionService>();
        await callers.UpsertAsync("agent-fixture", key, "fixture", "");
        var job = await service.SubmitAsync(new("http-tests", "http-tests"), new("student-a", 4, "source-a",
            [new() { ContentType = "image/png", Content = TestPng(), FileName = "page.png" }]));
        var other = await service.SubmitAsync(new("agent-fixture", "agent-fixture"), new("student-b", 4, "source-b",
            [new() { ContentType = "image/png", Content = TestPng(), FileName = "page.png" }]));
        await service.RunQueuedAsync(default);
        await service.RunQueuedAsync(default);
        var ownerAccess = new ExtractionAccess("http-tests", "http-tests");
        var initial = await service.GetAsync(ownerAccess, job.Id);
        var first = initial.Questions.Single().Question;
        var mixed = await service.ReviseAsync(ownerAccess, job.Id, new(Guid.NewGuid(), initial.Version, "Mixed worksheet",
            [first, first with { Id = Guid.NewGuid(), Statement = "Second question", Regions = [first.Regions[0] with { X1 = .5 }] }]));
        var retained = mixed.Questions[0];
        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var confirmed = await owner.PostAsJsonAsync($"/api/intake/extractions/{job.Id}/questions/{first.Id}/review",
            new ExtractionReviewRequest(mixed.Version, retained.ItemId!.Value, (int)ReviewStatus.Confirmed, "test-teacher", null, null));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var imageBefore = await owner.GetByteArrayAsync($"/api/intake/extractions/{job.Id}/questions/{first.Id}/images/{mixed.Version}/0");
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync(ops)).StatusCode);
        agent.DefaultRequestHeaders.Add("X-Emenda-Api-Key", key);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(ops)).StatusCode);
        await callers.UpdateAsync("agent-fixture", null, null, canOperateExtractions: true);
        var summaries = await Data<ExtractionSummaryPage>(await agent.GetAsync(ops));
        Assert.Equal(new[] { other.Id, job.Id }, summaries.Items.Select(x => x.Id));
        Assert.Equal(1, summaries.Items[0].MistakeCount);
        Assert.Equal(2, summaries.Items[1].MistakeCount);
        Assert.Equal(1, summaries.Items[1].ConfirmedCount);
        var session = await factory.SeedSessionAsync(admin);
        var route = $"/admin/api/extractions/{job.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync(route)).StatusCode);
        await factory.AddCsrfAsync(admin, session);
        var deletion = await Data<JsonElement>(await admin.DeleteAsync(route));
        Assert.Equal(job.Id, deletion.GetProperty("jobId").GetGuid());
        Assert.Equal(1, deletion.GetProperty("deletedMistakes").GetInt32());
        Assert.Equal(1, deletion.GetProperty("retainedConfirmed").GetInt32());
        var retainedPath = $"/api/intake/extractions/retained/{retained.ItemId}/images/0";
        Assert.Equal(imageBefore, await owner.GetByteArrayAsync(retainedPath));
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync(retainedPath)).StatusCode);
        var detail = await Data<JsonElement>(await owner.GetAsync($"/api/mistakes/{retained.ItemId}"));
        Assert.Equal(first.Statement, detail.GetProperty("analysisSnapshot").GetProperty("statement").GetString());
        Assert.Equal(other.Id, Assert.Single((await Data<ExtractionSummaryPage>(await agent.GetAsync(ops))).Items).Id);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"{ops}/{job.Id}/deletion")).StatusCode);
        await callers.UpdateAsync("agent-fixture", null, null, canOperateExtractions: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync($"{ops}/{other.Id}")).StatusCode);
    }

    [Fact]
    public async Task RealHost_ServesEveryMistakeImageBySize()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = factory.CreateClient(new() { AllowAutoRedirect = false });
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-sizes"), "studentId");
        form.Add(new StringContent("4"), "grade");
        form.Add(new StringContent("worksheet-sizes"), "sourceKey");
        using var page = RasterFixture.Create(1600, 1000);
        var image = new ByteArrayContent(page.ToByteArray(ImageMagick.MagickFormat.Png));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "images", "page.png");
        var job = await Data<ExtractionJobView>(await caller.PostAsync("/api/intake/extractions", form));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().RunQueuedAsync(default);
        job = await Data<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"));
        var itemId = job.Questions.Single().ItemId!.Value;

        using var source = await caller.GetAsync($"/api/intake/extractions/{job.Id}/sources/{job.Sources[0].Id}?size=thumbnail");
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        Assert.Equal("image/jpeg", source.Content.Headers.ContentType!.MediaType);
        Assert.True(source.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(1), source.Headers.CacheControl.MaxAge);
        using (var decoded = new ImageMagick.MagickImage(await source.Content.ReadAsByteArrayAsync())) Assert.Equal(200u, decoded.Width);
        using var bogus = await caller.GetAsync($"/api/intake/extractions/{job.Id}/sources/{job.Sources[0].Id}?size=huge");
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.Contains("invalid_size", await bogus.Content.ReadAsStringAsync());

        using var card = await caller.GetAsync($"/api/mistakes/{itemId}/images/0?size=small");
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Equal("image/jpeg", card.Content.Headers.ContentType!.MediaType);
        using (var decoded = new ImageMagick.MagickImage(await card.Content.ReadAsByteArrayAsync())) Assert.Equal(400u, decoded.Width);
        using var fullCrop = await caller.GetAsync($"/api/mistakes/{itemId}/images/0");
        Assert.Equal("image/png", fullCrop.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await caller.GetAsync($"/api/mistakes/{itemId}/images/1?size=small")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.GetAsync($"/api/mistakes/{itemId}/images/0?size=xl")).StatusCode);

        // A legacy item reads through the same route; variants appear beside its original.
        var oss = factory.Services.GetRequiredService<IOssService>();
        var legacyPath = await oss.UploadAsync(page.ToByteArray(ImageMagick.MagickFormat.Jpeg), "page.jpg", "image/jpeg", OssBucket.Mistakes, "2025/06/14/legacy");
        var legacyId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.MistakeItems.Add(new() { Id = legacyId, StudentId = "student-sizes", Subject = 2, Grade = 4, SourceUploadId = "legacy-upload",
                QuestionId = "legacy", SourceRegions = [new() { SourceImagePath = legacyPath }], ReviewStatus = 2 });
            await db.SaveChangesAsync();
        }
        using var legacy = await caller.GetAsync($"/api/mistakes/{legacyId}/images/0?size=medium");
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.True(await oss.ObjectExistsAsync(ImageVariants.PathFor(legacyPath, "medium")));
    }

    internal static byte[] TestPng()
    {
        // A real raster fixture, encoded from the decoder's permitted PNM input.
        using var image = new ImageMagick.MagickImage(System.Text.Encoding.ASCII.GetBytes("P6\n2 1\n255\n\u00ff\0\0\0\0\u00ff"));
        return image.ToByteArray(ImageMagick.MagickFormat.Png);
    }
    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("data").Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}

/// <summary>Real product host and auth middleware; only external AI/storage adapters are controlled.</summary>
public sealed class ExtractionHostFixture : WebApplicationFactory<Program>
{
    public const string CallerKey = "test-extraction-caller-credential-not-for-production";
    private readonly string _connection;
    private readonly bool _runWorker;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emenda-extraction-http-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previous = [];

    public ExtractionHostFixture(string connection, bool runWorker = false, string callerName = "http-tests")
    {
        _connection = connection;
        _runWorker = runWorker;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "callers.json"), "{\"version\":1,\"callers\":[]}");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings__Emenda"] = connection, ["Mistake__Layout__Enabled"] = "false",
            ["Emenda__Security__Callers__0__Name"] = callerName, ["Emenda__Security__Callers__0__ApiKey"] = CallerKey,
            ["Emenda__Security__DynamicCallers__Path"] = Path.Combine(_root, "callers.json"),
            ["Emenda__Admin__Oidc__Authority"] = "http://127.0.0.1:5111",
            ["Emenda__Admin__Oidc__ClientId"] = "emenda-tests", ["Emenda__Admin__Oidc__ClientSecret"] = "test-identity-secret",
            ["Emenda__Admin__Oidc__RedirectUri"] = "http://127.0.0.1:5010/admin/auth/oidc/callback",
            ["Emenda__Admin__Oidc__PostLogoutRedirectUri"] = "http://127.0.0.1:5010/admin/auth/oidc/logout/return",
            ["Emenda__Extraction__Enabled"] = "false"
        };
        foreach (var pair in settings)
        {
            _previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<MistakeDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MistakeDbContext>>();
            services.AddDbContext<MistakeDbContext>(options => options.UseNpgsql(_connection));
            services.RemoveAll<IOssService>();
            services.AddSingleton<IOssService>(new LocalFileOssService(Path.Combine(_root, "oss")));
            services.RemoveAll<IQuestionAnalysisClient>();
            services.AddSingleton<IQuestionAnalysisClient, ControlledAnalysis>();
            services.RemoveAll<ILayoutDetectionClient>();
            services.AddSingleton<ILayoutDetectionClient, ControlledLayout>();
            services.RemoveAll<IOptions<ExtractionOptions>>();
            services.AddSingleton(Options.Create(new ExtractionOptions { Enabled = true }));
            foreach (var registration in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && (d.ImplementationType == typeof(ExtractionHostedService)
                    || d.ImplementationType == typeof(Emenda.Host.Services.Images.ImageVariantWorker))).ToList()) services.Remove(registration);
            if (_runWorker) services.AddHostedService<ExtractionHostedService>();
        });
    }

    public async Task<string> SeedSessionAsync(HttpClient client)
    {
        var store = Services.GetRequiredService<ITicketStore>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-admin")],
            SignaCoreHostedLoginDefaults.SessionAuthenticationScheme));
        var key = await store.StoreAsync(new SignaCoreSessionTicket(principal, DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(20), "test-access", "test-id"), default)
            ?? throw new InvalidOperationException("Test session was not retained.");
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminConsoleConstants.SessionCookie}={key}");
        return key;
    }
    public async Task AddCsrfAsync(HttpClient client, string sessionKey)
    {
        using var response = await client.GetAsync("/admin/auth/oidc/csrf");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cookie = response.Headers.GetValues("Set-Cookie").Select(h => h.Split(';')[0])
            .Single(h => h.StartsWith(AdminConsoleConstants.CsrfCookie + "=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminConsoleConstants.SessionCookie}={sessionKey}; {cookie}");
        client.DefaultRequestHeaders.Add(AdminConsoleConstants.CsrfHeader, json.RootElement.GetProperty("token").GetString());
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class ControlledLayout : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default)
        {
            using var decoded = new ImageMagick.MagickImage(image.Bytes);
            return Task.FromResult(new LayoutDetectionResult { RequestId = "controlled-layout", Width = (int)decoded.Width,
                Height = (int)decoded.Height, Regions = [new(0, "question", new(0, 0, 1, 1))] });
        }
    }
    private sealed class ControlledAnalysis : IQuestionAnalysisClient
    {
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default) =>
            Task.FromResult(new QuestionAnalysisResult("controlled-test-model", [new ExtractionQuestionDraft
            {
                QuestionNumber = "12", Subject = 2, Statement = "1 + 1 = ?", StudentAnswer = "3", ReferenceAnswer = "2",
                Explanation = "Add one and one", Evidence = "Actual attempt is 3 rather than 2", Outcome = "incorrect",
                Regions = request.Images.Select(i => new ExtractionRegion(i.SourceId, 0, 0, 1, 1)).ToList()
            }]));
    }
}
