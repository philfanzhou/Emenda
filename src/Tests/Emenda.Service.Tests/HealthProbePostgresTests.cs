using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Emenda.Service.Tests;

/// <summary>
/// IKJFIX acceptance tests for the anonymous health endpoints, run against the
/// real Mistake Host subprocess with a temporary PostgreSQL database:
/// live never touches external IO; ready reports the startup dependency matrix
/// (db/oss always, student only when enabled, vl only when configured); a
/// failing component turns ready into 503 while live stays 200; the endpoints
/// stay anonymous under strict authentication; responses and logs carry no
/// secrets.
/// </summary>
public sealed class HealthProbePostgresTests
{
    private const string SecretMarker = "health-probe-pg-secret-marker";

    private static string? DedicatedAdminConnection()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(admin) ? null : admin;
    }

    /// <summary>
    /// Minimal S3-shaped responder: answers HTTP 200 to every request, so the
    /// MinIO bucket-exists HEAD reports the bucket as present.
    /// </summary>
    private sealed class AlwaysOkProbe : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopped = new();

        public AlwaysOkProbe()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = ServeAsync();
        }

        public int Port { get; }

        private async Task ServeAsync()
        {
            while (!_stopped.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await client.GetStream().WriteAsync(response, _stopped.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (IOException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopped.Cancel();
            _listener.Stop();
            _stopped.Dispose();
            await Task.CompletedTask;
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(
        HttpClient client, int port, string path, CancellationToken token)
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}{path}", token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public async Task ReadyIsGreenWhenDbAndOssAreReachableAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var s3 = new AlwaysOkProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Http__Port"] = port.ToString(),
            // The secret marker rides along as a valid harmless connection field
            // so the no-leak assertions below check the real connection string.
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
            {
                Database = database.Name,
                ApplicationName = SecretMarker,
            }.ConnectionString,
            ["Mistake__Local__Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        string liveBody = "";
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var run = await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await LocalBootstrapPostgresTests.WaitForRootAsync(client, port, token);
            (liveStatus, liveBody) = await GetAsync(client, port, "/health/live", token);
            (readyStatus, readyBody) = await GetAsync(client, port, "/health/ready", token);
        });

        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.Contains("\"status\":\"ok\"", liveBody);
        Assert.Equal(HttpStatusCode.OK, readyStatus);
        Assert.Contains("\"status\":\"ok\"", readyBody);
        // Matrix: db and oss always, student disabled and vl unconfigured absent.
        Assert.Contains("\"name\":\"db\",\"ok\":true", readyBody);
        Assert.Contains("\"name\":\"oss\",\"ok\":true", readyBody);
        Assert.DoesNotContain("student", readyBody);
        Assert.DoesNotContain("\"name\":\"vl\"", readyBody);
        // No secrets in the response or the host log.
        Assert.DoesNotContain(SecretMarker, readyBody);
        Assert.DoesNotContain(SecretMarker, run.Output);
    }

    [Fact]
    public async Task ReadyReportsOssFailureWhileLiveStaysOkAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        // The request probe answers 404 to everything, so the MinIO bucket-exists
        // HEAD reports the bucket as missing.
        await using var s3 = new LocalBootstrapPostgresTests.RequestProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["Mistake__Local__Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await LocalBootstrapPostgresTests.WaitForRootAsync(client, port, token);
            (liveStatus, _) = await GetAsync(client, port, "/health/live", token);
            (readyStatus, readyBody) = await GetAsync(client, port, "/health/ready", token);
        });

        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyStatus);
        Assert.Contains("\"status\":\"unavailable\"", readyBody);
        Assert.Contains("\"name\":\"db\",\"ok\":true", readyBody);
        Assert.Contains("\"name\":\"oss\",\"ok\":false", readyBody);
    }

    [Fact]
    public async Task ReadyReportsStudentFailureWhenEnabledAndUnreachableAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var s3 = new AlwaysOkProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        // A released port has nothing listening, so the Student HTTP probe hits
        // connection refusal.
        var unreachableStudentPort = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["Mistake__Local__Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}",
            ["Mistake__Student__Enabled"] = "true",
            ["Mistake__Local__Student__Url"] = $"http://127.0.0.1:{unreachableStudentPort}",
            // Probe behavior must not depend on the worker switches.
            ["Mistake__Workers__UploadPollingWorker__Enabled"] = "true",
            ["Mistake__Workers__ReanalyzeJobHostedService__Enabled"] = "true",
            ["Mistake__Workers__ReanalyzeJobCleanupService__Enabled"] = "true"
        };
        Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await LocalBootstrapPostgresTests.WaitForRootAsync(client, port, token);
            (liveStatus, _) = await GetAsync(client, port, "/health/live", token);
            (readyStatus, readyBody) = await GetAsync(client, port, "/health/ready", token);
        });

        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyStatus);
        Assert.Contains("\"name\":\"db\",\"ok\":true", readyBody);
        Assert.Contains("\"name\":\"oss\",\"ok\":true", readyBody);
        Assert.Contains("\"name\":\"student\",\"ok\":false", readyBody);
        Assert.DoesNotContain("\"name\":\"vl\"", readyBody);
    }

    [Fact]
    public async Task HealthEndpointsStayAnonymousUnderStrictAuthAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(admin);
        await using var s3 = new LocalBootstrapPostgresTests.RequestProbe();
        var port = LocalBootstrapPostgresTests.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["Mistake__Local__Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}",
            ["IdentityService__MistakeApiStrictAuth"] = "true",
            ["IdentityService__Authority"] = "http://127.0.0.1:5002",
            ["IdentityService__Issuer"] = "http://127.0.0.1:5002",
            ["IdentityService__Audience"] = "ruoyu-mistake",
            ["IdentityService__RequireHttpsMetadata"] = "false"
        };
        Assert.Equal(0, (await LocalBootstrapPostgresTests.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await LocalBootstrapPostgresTests.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await LocalBootstrapPostgresTests.WaitForRootAsync(client, port, token);
            // No Authorization header: anonymous requests under strict mode must
            // not receive 401 from the health endpoints.
            (liveStatus, _) = await GetAsync(client, port, "/health/live", token);
            (readyStatus, readyBody) = await GetAsync(client, port, "/health/ready", token);
        });

        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.NotEqual(HttpStatusCode.Unauthorized, readyStatus);
        Assert.Contains("\"checks\"", readyBody);
    }
}
