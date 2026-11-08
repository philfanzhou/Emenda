using System.Net;
using System.Text;
using Npgsql;

namespace Emenda.Service.Tests;

/// <summary>
/// IKJ8P0 acceptance tests. Local Student capability disabled must fail
/// closed: no Student probe request, no polling. Current rejected reviews
/// require authentication first, even when the capability is disabled; invalid
/// capability configurations must fail before database or external access
/// with the offending key names; enabled modes must keep existing call paths.
/// </summary>
public sealed class LocalStudentCapabilityPostgresTests
{
    private static string? DedicatedAdminConnection()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(admin) ? null : admin;
    }

    private static async Task<Guid> InsertPendingItemAsync(
        LocalBootstrapPostgresTests.TestDatabase database, string studentId = "student-capability-1")
    {
        var item = Guid.NewGuid();
        await database.ExecuteAsync(
            "INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) " +
            $"VALUES ('{item}','{studentId}',1,7,'upload-capability-marker', " +
            $"'[{{\"sourceImagePath\":\"mistakes/{studentId}/reject-check.jpg\"}}]'::jsonb,1,'root-capability-marker',now())");
        return item;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostReviewAsync(
        HttpClient client, int port, Guid item, int reviewStatus, CancellationToken token)
    {
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                id = item.ToString(),
                reviewStatus,
                reviewerId = "teacher-capability",
                rootCause = "root-cause-marker",
                returnReason = "return-reason-marker"
            }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/mistakes/{item}/review", content, token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public async Task LocalDisabledStudentRejectsAnonymousReviewBeforeSideEffectsAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var student = new LocalBootstrapPostgresTests.RequestProbe();
        await using var s3 = new LocalBootstrapPostgresTests.RequestProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            // Leftover legacy address from the repo appsettings must not be contacted.
            ["StudentService__Url"] = $"http://127.0.0.1:{student.Port}",
            ["Mistake__Local__Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);
        var item = await InsertPendingItemAsync(database);
        var regionsBefore = await database.ScalarAsync($"SELECT source_regions::text FROM mistake_item WHERE id='{item}'");

        string? firstBody = null;
        string? secondBody = null;
        HttpStatusCode firstStatus = HttpStatusCode.OK;
        HttpStatusCode secondStatus = HttpStatusCode.OK;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var run = await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await LocalBootstrapPostgresTests.WaitForRootAsync(client, port, token);
            (firstStatus, firstBody) = await PostReviewAsync(client, port, item, 3, token);
            (secondStatus, secondBody) = await PostReviewAsync(client, port, item, 3, token);
        });

        Assert.Equal(HttpStatusCode.Unauthorized, firstStatus);
        Assert.Equal(HttpStatusCode.Unauthorized, secondStatus);
        Assert.NotNull(firstBody);
        Assert.Equal(firstBody, secondBody);
        Assert.DoesNotContain("Student 集成未启用", firstBody);
        Assert.DoesNotContain("root-capability-marker", firstBody);
        // No domain, OSS or Student side effect was applied.
        Assert.Equal("1", await database.ScalarAsync($"SELECT review_status FROM mistake_item WHERE id='{item}'"));
        Assert.Equal(regionsBefore, await database.ScalarAsync($"SELECT source_regions::text FROM mistake_item WHERE id='{item}'"));
        Assert.Equal("root-capability-marker", await database.ScalarAsync($"SELECT root_cause FROM mistake_item WHERE id='{item}'"));
        Assert.Equal("1", await database.ScalarAsync($"SELECT count(*) FROM mistake_item WHERE id='{item}'"));
        Assert.Equal("0", await database.ScalarAsync("SELECT count(*) FROM process_log"));
        Assert.Equal(0, student.RequestCount);
        Assert.Equal(0, s3.RequestCount);
        Assert.Contains("UploadPollingWorker: Enabled=False", run.Output);
        Assert.Contains("Student capability: Enabled=False, Mode=Local, AddressSource=Disabled", run.Output);
    }

    [Fact]
    public async Task LocalDisabledStudentKeepsConfirmAndLocalReadEndpointsWorkingAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var student = new LocalBootstrapPostgresTests.RequestProbe();
        await using var s3 = new LocalBootstrapPostgresTests.RequestProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["StudentService__Url"] = $"http://127.0.0.1:{student.Port}",
            ["Mistake__Local__Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);
        var item = await InsertPendingItemAsync(database);

        HttpStatusCode confirmStatus = HttpStatusCode.OK;
        string? confirmBody = null;
        string? readBody = null;
        string? listBody = null;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await LocalBootstrapPostgresTests.WaitForRootAsync(client, port, token);
            (confirmStatus, confirmBody) = await PostReviewAsync(client, port, item, 2, token);
            readBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/mistakes/{item}", token);
            listBody = await client.GetStringAsync(
                $"http://127.0.0.1:{port}/api/mistakes?studentId=student-capability-1&subject=0&grade=0&reviewStatus=0&page=1&size=10", token);
        });

        Assert.Equal(HttpStatusCode.OK, confirmStatus);
        Assert.Contains("\"success\":true", confirmBody);
        Assert.Equal("2", await database.ScalarAsync($"SELECT review_status FROM mistake_item WHERE id='{item}'"));
        Assert.Equal("1", await database.ScalarAsync("SELECT count(*) FROM process_log"));
        Assert.Contains("\"success\":true", readBody);
        Assert.Contains("student-capability-1", readBody);
        Assert.Contains("\"success\":true", listBody);
        Assert.Equal(0, student.RequestCount);
        Assert.Equal(0, s3.RequestCount);
    }

    [Theory]
    [InlineData("Local", "false", "true", "Mistake:Student:Enabled=false cannot be combined with Mistake:Workers:UploadPollingWorker:Enabled=true")]
    [InlineData("Local", null, "true", "Mistake:Student:Enabled=false cannot be combined with Mistake:Workers:UploadPollingWorker:Enabled=true")]
    [InlineData("Platform", "false", "true", "Mistake:Student:Enabled=false cannot be combined with Mistake:Workers:UploadPollingWorker:Enabled=true")]
    [InlineData("Local", "maybe", null, "Mistake:Student:Enabled must be true or false.")]
    [InlineData("Local", "1", null, "Mistake:Student:Enabled must be true or false.")]
    public async Task InvalidOrContradictoryStudentSwitchFailsBeforeDatabaseAndExternalAccessAsync(
        string? mode, string? studentValue, string? pollingValue, string diagnostic)
    {
        await using var student = new LocalBootstrapPostgresTests.RequestProbe();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = mode,
            ["ConnectionStrings__MistakeLocal"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=db-secret-marker",
            ["StudentService__Url"] = $"http://127.0.0.1:{student.Port}",
            ["CONSUL_HTTP_ADDR"] = "127.0.0.1:9",
            ["CONSUL_RETRY_COUNT"] = "1",
            ["CONSUL_TIMEOUT_MS"] = "100"
        };
        if (studentValue is not null) settings["Mistake__Student__Enabled"] = studentValue;
        if (pollingValue is not null) settings["Mistake__Workers__UploadPollingWorker__Enabled"] = pollingValue;
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
        Assert.DoesNotContain("db-secret-marker", result.Output);
        Assert.Equal(0, student.RequestCount);
    }

    [Theory]
    [InlineData(null, "Mistake:Local:Student:Url is required")]
    [InlineData(" ", "Mistake:Local:Student:Url is required")]
    [InlineData("not a url", "Mistake:Local:Student:Url must be an absolute HTTP(S) base URL")]
    [InlineData("ftp://127.0.0.1:9", "Mistake:Local:Student:Url must be an absolute HTTP(S) base URL")]
    [InlineData("http://user:url-secret-marker@127.0.0.1:9", "Mistake:Local:Student:Url must be an absolute HTTP(S) base URL")]
    [InlineData("http://127.0.0.1:9/api", "Mistake:Local:Student:Url must be an absolute HTTP(S) base URL")]
    [InlineData("http://127.0.0.1:9?a=b", "Mistake:Local:Student:Url must be an absolute HTTP(S) base URL")]
    public async Task LocalEnabledStudentRequiresExplicitValidUrlBeforeDatabaseAccessAsync(
        string? url, string diagnostic)
    {
        await using var student = new LocalBootstrapPostgresTests.RequestProbe();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Student__Enabled"] = "true",
            ["ConnectionStrings__MistakeLocal"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=db-secret-marker",
            // Only the legacy fallback key is present; it must not satisfy the
            // dedicated Local key.
            ["StudentService__Url"] = $"http://127.0.0.1:{student.Port}"
        };
        if (url is not null) settings["Mistake__Local__Student__Url"] = url;
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
        Assert.DoesNotContain("db-secret-marker", result.Output);
        Assert.DoesNotContain("url-secret-marker", result.Output);
        Assert.Equal(0, student.RequestCount);
    }

    [Fact]
    public async Task LocalEnabledStudentUsesExplicitUrlAndUpgradeDoesNotNeedItAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var student = new LocalBootstrapPostgresTests.RequestProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Student__Enabled"] = "true",
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["StudentService__Url"] = $"http://127.0.0.1:{student.Port}"
        };
        // Explicit upgrade commands do not contact Student, so they do not
        // require the dedicated URL (same boundary as the local OSS keys).
        var upgraded = await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings);
        Assert.Equal(0, upgraded.ExitCode);
        Assert.Equal(0, student.RequestCount);

        settings["Mistake__Local__Student__Url"] = $"http://127.0.0.1:{student.Port}";
        settings["Mistake__Workers__UploadPollingWorker__Enabled"] = "true";
        var run = await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true,
            startupDelay: TimeSpan.FromSeconds(14));
        Assert.Contains("Student capability: Enabled=True, Mode=Local, AddressSource=Explicit", run.Output);
        Assert.Contains($"BaseUrl=http://127.0.0.1:{student.Port}", run.Output);
        Assert.Contains("UploadPollingWorker: Enabled=True", run.Output);
        Assert.True(student.RequestCount > 0);
    }

    [Fact]
    public async Task PlatformModeKeepsEnabledDefaultWithLegacyAddressFallbackAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var consul = new LocalBootstrapPostgresTests.RequestProbe(serverError: true);
        await using var loki = new LocalBootstrapPostgresTests.RequestProbe();
        await using var student = new LocalBootstrapPostgresTests.RequestProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var directory = Path.Combine(Path.GetTempPath(), "mistake-student-platform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(admin);
            await File.WriteAllTextAsync(Path.Combine(directory, "cache.json"),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["PostgreSql:Host"] = connection.Host,
                    ["PostgreSql:Port"] = connection.Port.ToString(),
                    ["PostgreSql:Username"] = connection.Username,
                    ["PostgreSql:Password"] = connection.Password,
                    ["Database:Name"] = database.Name,
                    ["StudentService:Url"] = $"http://127.0.0.1:{student.Port}",
                    ["Mistake:Http:Port"] = port.ToString(),
                    ["Loki:Uri"] = $"http://127.0.0.1:{loki.Port}"
                }));
            var settings = new Dictionary<string, string?>
            {
                ["Mistake__ConfigMode"] = "Platform",
                ["CONSUL_HTTP_ADDR"] = $"127.0.0.1:{consul.Port}",
                ["CONSUL_CACHE_DIR"] = directory,
                ["CONSUL_RETRY_COUNT"] = "1",
                ["CONSUL_TIMEOUT_MS"] = "100"
            };
            Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);
            var run = await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true,
                startupDelay: TimeSpan.FromSeconds(14));
            Assert.Contains("Student capability: Enabled=True, Mode=Platform, AddressSource=Platform", run.Output);
            Assert.Contains($"BaseUrl=http://127.0.0.1:{student.Port}", run.Output);
            Assert.Contains("UploadPollingWorker: Enabled=True", run.Output);
            Assert.True(student.RequestCount > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
