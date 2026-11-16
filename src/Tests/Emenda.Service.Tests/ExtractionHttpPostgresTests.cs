using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Authentication;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
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
        Assert.NotNull(result.Questions.Single().ItemId);
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), result.Version, "Correct hand-written answer",
            [result.Questions.Single().Question with { Outcome = "correct", StudentAnswer = "2", Evidence = "The actual answer is 2" }]);
        Assert.Equal(HttpStatusCode.NotFound,
            (await caller.PutAsJsonAsync($"/api/intake/extractions/{job.Id}/questions", request)).StatusCode);
        using var anonymousAdmin = factory.CreateClient(new() { AllowAutoRedirect = false });
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
        var revised = await Data<ExtractionJobView>(await admin.PutAsJsonAsync($"/admin/api/extractions/{job.Id}/questions", request));
        Assert.Equal(2, revised.Version);
        Assert.Equal("correct", revised.Questions.Single().Question.Outcome);
        Assert.Equal(result.Questions.Single().ItemId, revised.Questions.Single().ItemId);
        var history = await admin.GetAsync($"/admin/api/extractions/{job.Id}/versions/1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var original = await admin.GetAsync($"/admin/api/extractions/{job.Id}/sources/{job.Sources[0].Id}");
        Assert.Equal(originalBytes, await original.Content.ReadAsByteArrayAsync());
        using var page = await admin.GetAsync("/admin/extractions");
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ExtractionHostFixture.CallerKey, html);
        await using var db = database.CreateContext();
        Assert.Equal(3, (await db.MistakeItems.SingleAsync()).ReviewStatus);
    }

    [Fact]
    public async Task RealHost_RejectsMalformedEnvelopesAndImageMismatchBeforeCreatingTask()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
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

    public ExtractionHostFixture(string connection, bool runWorker = false)
    {
        _connection = connection;
        _runWorker = runWorker;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "callers.json"), "{\"version\":1,\"callers\":[]}");
        var settings = new Dictionary<string, string?>
        {
            ["Emenda__Runtime__Mode"] = "Standalone", ["ConnectionStrings__Emenda"] = connection,
            ["Emenda__Security__Callers__0__Name"] = "http-tests", ["Emenda__Security__Callers__0__ApiKey"] = CallerKey,
            ["Emenda__Security__DynamicCallers__Path"] = Path.Combine(_root, "callers.json"),
            ["Emenda__Admin__Oidc__Authority"] = "http://127.0.0.1:5111",
            ["Emenda__Admin__Oidc__ClientId"] = "emenda-tests", ["Emenda__Admin__Oidc__ClientSecret"] = "test-identity-secret",
            ["Emenda__Admin__Oidc__RedirectUri"] = "http://127.0.0.1:5010/admin/auth/oidc/callback",
            ["Emenda__Admin__Oidc__PostLogoutRedirectUri"] = "http://127.0.0.1:5010/admin/auth/oidc/logout/return",
            ["Emenda__Extraction__Enabled"] = "false", ["Emenda__Database__SchemaOwnership"] = "Own",
            ["Mistake__Student__Enabled"] = "false", ["Mistake__Layout__Enabled"] = "false",
            ["Mistake__SourceIntake__Enabled"] = "false", ["Mistake__SourceTargets__Enabled"] = "false",
            ["Mistake__SourceReturns__Enabled"] = "false", ["Mistake__SourceHandoffs__Enabled"] = "false",
            ["Mistake__Workers__UploadPollingWorker__Enabled"] = "false",
            ["Mistake__Workers__ReanalyzeJobHostedService__Enabled"] = "false",
            ["Mistake__Workers__ReanalyzeJobCleanupService__Enabled"] = "false",
            ["IdentityService__MistakeApiStrictAuth"] = "false", ["IdentityService__MistakeApiResourceAuth"] = "false"
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
                Subject = 2, Statement = "1 + 1 = ?", StudentAnswer = "3", ReferenceAnswer = "2",
                Explanation = "Add one and one", Evidence = "Actual attempt is 3 rather than 2", Outcome = "incorrect",
                Regions = request.Images.Select(i => new ExtractionRegion(i.SourceId, 0, 0, 1, 1)).ToList()
            }]));
    }
}
