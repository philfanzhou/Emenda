using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Ruoyu.Study.Consul.Shared;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class LocalBootstrapPostgresTests
{
    [Fact]
    public async Task LocalModeUsesOnlyExplicitDatabaseAndDoesNotContactConsulOrLokiAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var database = await TestDatabase.CreateAsync(admin);
        await using var consul = new RequestProbe();
        await using var loki = new RequestProbe();
        var connection = new NpgsqlConnectionStringBuilder(admin) { Database = database.Name }.ConnectionString;
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["ConnectionStrings__MistakeLocal"] = connection,
            ["CONSUL_HTTP_ADDR"] = $"127.0.0.1:{consul.Port}",
            ["CONSUL_CACHE_DIR"] = "/dev/null/not-a-cache-directory",
            ["Loki__Uri"] = $"http://127.0.0.1:{loki.Port}"
        };

        var created = await RunHostAsync(["--upgrade-empty-db"], settings);
        Assert.Equal(0, created.ExitCode);
        Assert.Contains("Mode=Local", created.Output);
        Assert.Contains("Source=Environment", created.Output);
        Assert.Contains("empty database upgrade verified", created.Output);
        Assert.DoesNotContain("Consul KV load", created.Output);
        Assert.Equal(0, consul.RequestCount);
        Assert.Equal(0, loki.RequestCount);

        var known = await RunHostAsync(["--upgrade-known-db", "--database", database.Name,
            "--writes-stopped", "--backup-verified"], settings);
        Assert.Equal(0, known.ExitCode);
        Assert.Contains("known database upgrade verified", known.Output);
        Assert.Equal(0, consul.RequestCount);
        Assert.Equal(0, loki.RequestCount);

        var normal = await RunHostAsync([], settings, waitForStartup: true);
        Assert.Contains("Mistake database schema verified for read-only startup", normal.Output);
        Assert.Equal(0, consul.RequestCount);
        Assert.Equal(0, loki.RequestCount);
        Assert.Equal("4", await database.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task LocalModeUsesEnvironmentBeforeFileAndRejectsWrongCredentialsAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var fileDatabase = await TestDatabase.CreateAsync(admin);
        await using var environmentDatabase = await TestDatabase.CreateAsync(admin);
        var directory = Path.Combine(Path.GetTempPath(), "mistake-local-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fileConnection = new NpgsqlConnectionStringBuilder(admin) { Database = fileDatabase.Name }.ConnectionString;
            await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Mistake = new { ConfigMode = "Local" },
                    ConnectionStrings = new { MistakeLocal = fileConnection }
                }));
            var settings = new Dictionary<string, string?>
            {
                ["ASPNETCORE_CONTENTROOT"] = directory,
                ["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                    { Database = environmentDatabase.Name }.ConnectionString,
                ["CONSUL_HTTP_ADDR"] = "127.0.0.1:9",
                ["CONSUL_CACHE_DIR"] = "/dev/null/not-a-cache-directory"
            };
            var result = await RunHostAsync(["--upgrade-empty-db"], settings);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Source=Environment", result.Output);
            Assert.Equal("0", await fileDatabase.ScalarAsync("SELECT count(*) FROM pg_class WHERE relnamespace='public'::regnamespace AND relname='mistake_item'"));
            Assert.Equal("4", await environmentDatabase.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\""));

            settings.Remove("ConnectionStrings__MistakeLocal");
            var fromFile = await RunHostAsync(["--upgrade-empty-db"], settings);
            Assert.Equal(0, fromFile.ExitCode);
            Assert.Contains("Source=File", fromFile.Output);
            Assert.Equal("4", await fileDatabase.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\""));

            settings["ConnectionStrings__MistakeLocal"] = new NpgsqlConnectionStringBuilder(admin)
                { Database = environmentDatabase.Name, Password = "wrong-password-marker" }.ConnectionString;
            var rejected = await RunHostAsync(["--upgrade-empty-db"], settings);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.DoesNotContain("wrong-password-marker", rejected.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PlatformModeKeepsConsulCacheFallbackAndEnvironmentPrecedenceAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL 15 admin connection string.");
        await using var cachedDatabase = await TestDatabase.CreateAsync(admin);
        await using var environmentDatabase = await TestDatabase.CreateAsync(admin);
        await using var consul = new RequestProbe(serverError: true);
        await using var loki = new RequestProbe();
        var directory = Path.Combine(Path.GetTempPath(), "mistake-platform-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(admin);
            await File.WriteAllTextAsync(Path.Combine(directory, "cache.json"),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["PostgreSql:Host"] = settings.Host,
                    ["PostgreSql:Port"] = settings.Port.ToString(),
                    ["PostgreSql:Username"] = settings.Username,
                    ["PostgreSql:Password"] = settings.Password,
                    ["Database:Name"] = cachedDatabase.Name,
                    ["Loki:Uri"] = $"http://127.0.0.1:{loki.Port}"
                }));
            var environment = new Dictionary<string, string?>
            {
                ["CONSUL_HTTP_ADDR"] = $"127.0.0.1:{consul.Port}",
                ["CONSUL_CACHE_DIR"] = directory,
                ["CONSUL_RETRY_COUNT"] = "1",
                ["CONSUL_TIMEOUT_MS"] = "1000"
            };
            var fallback = await RunHostAsync(["--upgrade-empty-db"], environment);
            Assert.Equal(0, fallback.ExitCode);
            Assert.Contains("Mode=Platform", fallback.Output);
            Assert.Contains("Source=Cache", fallback.Output);
            Assert.True(consul.RequestCount > 0);
            Assert.Equal("4", await cachedDatabase.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\""));

            environment["Mistake__ConfigMode"] = "Platform";
            environment["Database__Name"] = environmentDatabase.Name;
            var overrideResult = await RunHostAsync(["--upgrade-empty-db"], environment);
            Assert.Equal(0, overrideResult.ExitCode);
            Assert.Contains("Mode=Platform", overrideResult.Output);
            Assert.Equal("4", await environmentDatabase.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\""));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlatformConfigurationStillOverridesLokiUriAndBuildsSharedDatabaseConnection()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Loki:Uri"] = "http://loki.example:3100",
            ["Serilog:WriteTo:1:Args:uri"] = "http://fallback.example:3100",
            ["PostgreSql:Host"] = "postgres.example",
            ["PostgreSql:Port"] = "5544",
            ["PostgreSql:Username"] = "mistake_user",
            ["PostgreSql:Password"] = "test-password",
            ["Database:Name"] = "mistake_platform"
        });
        configuration.AddRuoyuLokiSink();
        Assert.Equal("http://loki.example:3100", configuration["Serilog:WriteTo:1:Args:uri"]);
        var connection = new NpgsqlConnectionStringBuilder(
            SharedPostgreSqlConnectionStringFactory.BuildOrFallback(configuration));
        Assert.Equal("postgres.example", connection.Host);
        Assert.Equal(5544, connection.Port);
        Assert.Equal("mistake_platform", connection.Database);
    }

    [Theory]
    [InlineData(null, null, "Local mode requires ConnectionStrings:MistakeLocal")]
    [InlineData("Host=localhost;Port=5432;Database=ruoyu_study_mistake;Username=phil", null, "distinct from the platform example")]
    [InlineData(null, "Locl", "Mistake:ConfigMode must be Local or Platform")]
    public async Task InvalidLocalConfigurationFailsBeforeExternalAccessAsync(string? connection,
        string? mode, string diagnostic)
    {
        await using var consul = new RequestProbe();
        var settings = new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = mode ?? "Local",
            ["ConnectionStrings__MistakeLocal"] = connection,
            ["CONSUL_HTTP_ADDR"] = $"127.0.0.1:{consul.Port}",
            ["CONSUL_CACHE_DIR"] = "/dev/null/not-a-cache-directory"
        };
        var result = await RunHostAsync(["--upgrade-empty-db"], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Output);
        Assert.Equal(0, consul.RequestCount);
    }

    private static async Task<RunResult> RunHostAsync(string[] arguments,
        IReadOnlyDictionary<string, string?> settings, bool waitForStartup = false)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hostDirectory = Path.Combine(sourceRoot, "Host", "bin", configuration, "net8.0");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = hostDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(hostDirectory, "Ruoyu.Study.Mistake.Host.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        foreach (var (key, value) in settings)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Mistake Host.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (waitForStartup)
            {
                await Task.WhenAny(process.WaitForExitAsync(timeout.Token), Task.Delay(2000, timeout.Token));
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            else await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        await process.WaitForExitAsync();
        return new RunResult(process.ExitCode, await output + await errors);
    }

    private sealed record RunResult(int ExitCode, string Output);

    private sealed class RequestProbe : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _serverError;
        private readonly CancellationTokenSource _stopped = new();
        private readonly Task _server;
        private int _requests;

        public RequestProbe(bool serverError = false)
        {
            _serverError = serverError;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = ServeAsync();
        }

        public int Port { get; }
        public int RequestCount => Volatile.Read(ref _requests);

        private async Task ServeAsync()
        {
            while (!_stopped.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                    Interlocked.Increment(ref _requests);
                    var response = Encoding.ASCII.GetBytes(_serverError
                        ? "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
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
            await _server;
            _stopped.Dispose();
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _admin;

        private TestDatabase(string admin, string name) { _admin = admin; Name = name; }
        public string Name { get; }

        public static async Task<TestDatabase> CreateAsync(string admin)
        {
            var name = "mistake_local_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            return new TestDatabase(admin, name);
        }

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin)
                { Database = Name }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{Name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
