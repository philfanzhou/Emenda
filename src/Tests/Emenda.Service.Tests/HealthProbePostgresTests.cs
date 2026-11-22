using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Emenda.Service.Tests;

/// <summary>
/// IKJFIX acceptance tests for the anonymous health endpoints, run against the
/// real Mistake Host subprocess with a temporary PostgreSQL database:
/// live never touches external IO; ready reports the startup dependency matrix
/// (db/oss always, structadoc only when layout detection is enabled); a
/// failing component turns ready into 503 while live stays 200; the endpoints
/// stay anonymous while /api requires a caller key; responses and logs carry no
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
        await using var database = await HostProcess.ScratchDatabase.CreateAsync(admin);
        await using var s3 = new AlwaysOkProbe();
        var port = HostProcess.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__Http__Port"] = port.ToString(),
            // The secret marker rides along as a valid harmless connection field
            // so the no-leak assertions below check the real connection string.
            ["ConnectionStrings__Emenda"] = new NpgsqlConnectionStringBuilder(admin)
            {
                Database = database.Name,
                ApplicationName = SecretMarker,
            }.ConnectionString,
            ["Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await HostProcess.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        string liveBody = "";
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var run = await HostProcess.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await HostProcess.WaitForRootAsync(client, port, token);
            (liveStatus, liveBody) = await GetAsync(client, port, "/health/live", token);
            (readyStatus, readyBody) = await GetAsync(client, port, "/health/ready", token);
        });

        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.Contains("\"status\":\"ok\"", liveBody);
        Assert.Equal(HttpStatusCode.OK, readyStatus);
        Assert.Contains("\"status\":\"ok\"", readyBody);
        // Matrix: db and oss always; structadoc only when layout detection is enabled.
        Assert.Contains("\"name\":\"db\",\"ok\":true", readyBody);
        Assert.Contains("\"name\":\"oss\",\"ok\":true", readyBody);
        Assert.DoesNotContain("structadoc", readyBody);
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
        await using var database = await HostProcess.ScratchDatabase.CreateAsync(admin);
        // The request probe answers 404 to everything, so the MinIO bucket-exists
        // HEAD reports the bucket as missing.
        await using var s3 = new HostProcess.RequestProbe();
        var port = HostProcess.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__Emenda"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await HostProcess.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await HostProcess.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await HostProcess.WaitForRootAsync(client, port, token);
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
    public async Task HealthEndpointsStayAnonymousWhileApiRequiresCallerKeyAsync()
    {
        var admin = DedicatedAdminConnection();
        if (admin is null)
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await HostProcess.ScratchDatabase.CreateAsync(admin);
        await using var s3 = new HostProcess.RequestProbe();
        var port = HostProcess.GetAvailablePort();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__Http__Port"] = port.ToString(),
            ["ConnectionStrings__Emenda"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = database.Name }.ConnectionString,
            ["Oss__InternalEndpoint"] = $"127.0.0.1:{s3.Port}"
        };
        Assert.Equal(0, (await HostProcess.RunHostAsync(["--upgrade-empty-db"], settings)).ExitCode);

        HttpStatusCode liveStatus = HttpStatusCode.OK;
        HttpStatusCode readyStatus = HttpStatusCode.OK;
        HttpStatusCode apiStatus = HttpStatusCode.OK;
        string readyBody = "";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await HostProcess.RunHostAsync([], settings, waitForStartup: true, duringStartup: async token =>
        {
            await HostProcess.WaitForRootAsync(client, port, token);
            // No caller key: health endpoints stay anonymous, the business API does not.
            (liveStatus, _) = await GetAsync(client, port, "/health/live", token);
            (readyStatus, readyBody) = await GetAsync(client, port, "/health/ready", token);
            (apiStatus, _) = await GetAsync(client, port, "/api/mistakes?page=1&size=1&subject=0&grade=0&reviewStatus=0", token);
        });

        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.NotEqual(HttpStatusCode.Unauthorized, readyStatus);
        Assert.Contains("\"checks\"", readyBody);
        Assert.Equal(HttpStatusCode.Unauthorized, apiStatus);
    }
}
