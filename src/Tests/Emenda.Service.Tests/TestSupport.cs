using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Emenda.Common.Oss;
using Emenda.Database;

namespace Emenda.Service.Tests;

/// <summary>Isolated PostgreSQL database initialized to the product schema, plus a file-backed OSS mock.</summary>
internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emenda-product-" + Guid.NewGuid().ToString("N"));
    public string Connection { get; }
    public LocalFileOssService Files { get; }
    public Mock<IOssService> Oss { get; } = new(MockBehavior.Strict);
    public Func<int, Task> BeforeUpload { get; set; } = _ => Task.CompletedTask;
    public bool FailAfterPut { get; set; }
    private int _uploadAttempts;

    private PostgresTestDatabase(string admin, string name)
    {
        _admin = admin; _name = name;
        Connection = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        Files = new LocalFileOssService(_root);
        Oss.Setup(o => o.ObjectExistsAsync(It.IsAny<string>())).Returns((string path) => Files.ObjectExistsAsync(path));
        Oss.Setup(o => o.DownloadAsync(It.IsAny<string>())).Returns((string path) => Files.DownloadAsync(path));
        Oss.Setup(o => o.DeleteIfExistsAsync(It.IsAny<string>())).Returns((string path) => ((IOssService)Files).DeleteIfExistsAsync(path));
        Oss.Setup(o => o.DeleteDerivedAsync(It.IsAny<string>())).Returns((string path) => Oss.Object.DeleteIfExistsAsync(path));
        Oss.Setup(o => o.UploadAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OssBucket>(), It.IsAny<string?>()))
            .Returns(async (byte[] bytes, string name, string type, OssBucket bucket, string? folder) =>
            {
                await BeforeUpload(Interlocked.Increment(ref _uploadAttempts));
                var path = await Files.UploadAsync(bytes, name, type, bucket, folder);
                if (FailAfterPut) throw new IOException("injected lost PUT response");
                return path;
            });
    }

    internal static async Task<PostgresTestDatabase> CreateAsync(bool initialize = true)
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_MIGRATION_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_MIGRATION_TEST_CONNECTION to a dedicated PostgreSQL test admin connection.");
        var name = "emenda_product_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        var database = new PostgresTestDatabase(admin, name);
        try
        {
            if (initialize)
            {
                await using var db = database.CreateContext();
                await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
            }
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    public MistakeDbContext CreateContext() => new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(Connection).Options);
    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        Directory.Delete(_root, recursive: true);
    }
}

/// <summary>Runs the built Host as a subprocess with a product caller and test OSS settings.</summary>
internal static class HostProcess
{
    internal const string HostCallerName = "host-tests";
    internal const string HostCallerKey = "host-test-api-key-0123456789";

    internal static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static async Task<string> WaitForRootAsync(HttpClient client, int port, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await client.GetStringAsync($"http://127.0.0.1:{port}/", token); }
            catch (HttpRequestException) { await Task.Delay(100, token); }
        }
    }

    internal static async Task<RunResult> RunHostAsync(string[] arguments,
        IReadOnlyDictionary<string, string?> settings, bool waitForStartup = false,
        TimeSpan? startupDelay = null, Func<CancellationToken, Task>? duringStartup = null,
        bool includeOss = true)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hostDirectory = Path.Combine(sourceRoot, "Host", "bin", configuration, "net10.0");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = hostDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(hostDirectory, "Emenda.Host.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Emenda__Security__Callers__0__Name"] = HostCallerName;
        start.Environment["Emenda__Security__Callers__0__ApiKey"] = HostCallerKey;
        if (includeOss)
        {
            start.Environment["Oss__InternalEndpoint"] = "127.0.0.1:19001";
            start.Environment["Oss__InternalSecure"] = "false";
            start.Environment["Oss__AccessKey"] = "test-access";
            start.Environment["Oss__SecretKey"] = "test-secret";
            start.Environment["Oss__BucketName"] = "emenda-host-tests";
            start.Environment["Oss__PublicBaseUrl"] = "http://127.0.0.1:19002/oss";
        }
        foreach (var (key, value) in settings)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Mistake Host.");
        var schemaVerified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = ReadOutputAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            if (waitForStartup)
            {
                try
                {
                    if (duringStartup is not null) await duringStartup(timeout.Token);
                    else
                    {
                        if (startupDelay is null)
                            await Task.WhenAny(process.WaitForExitAsync(timeout.Token),
                                schemaVerified.Task).WaitAsync(timeout.Token);
                        await Task.WhenAny(process.WaitForExitAsync(timeout.Token),
                            Task.Delay(startupDelay ?? TimeSpan.FromSeconds(2), timeout.Token));
                    }
                }
                finally
                {
                    if (!process.HasExited && !OperatingSystem.IsWindows())
                    {
                        using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
                        {
                            ArgumentList = { "-TERM", process.Id.ToString() },
                            UseShellExecute = false
                        });
                        if (signal is not null) await signal.WaitForExitAsync();
                        await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
                    }
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
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

        async Task<string> ReadOutputAsync()
        {
            var lines = new StringBuilder();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                lines.AppendLine(line);
                if (line.Contains("Mistake database schema verified at target version", StringComparison.Ordinal))
                    schemaVerified.TrySetResult();
            }
            return lines.ToString();
        }
    }

    internal sealed record RunResult(int ExitCode, string Output);

    internal sealed class RequestProbe : IAsyncDisposable
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

    internal sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _admin;

        private ScratchDatabase(string admin, string name) { _admin = admin; Name = name; }
        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync(string admin)
        {
            var name = "mistake_local_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            return new ScratchDatabase(admin, name);
        }

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin)
                { Database = Name }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin)
                { Database = Name }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
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

/// <summary>In-process standalone Host with an isolated database and OSS store.</summary>
internal sealed class StandaloneWebApplicationFactory : WebApplicationFactory<Program>
{
    internal const string CallerName = "intake-tests";
    internal const string CallerKey = "intake-test-api-key-0123456789";
    private readonly string? _connection;
    private readonly string _ossRoot =
        Path.Combine(Path.GetTempPath(), $"emenda-intake-tests-{Guid.NewGuid():N}");

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

    /// <param name="connection">PostgreSQL connection, or null for an in-memory database.</param>
    public StandaloneWebApplicationFactory(string? connection)
    {
        _connection = connection;
        Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__Name", CallerName);
        Environment.SetEnvironmentVariable("Emenda__Security__Callers__0__ApiKey", CallerKey);
        // The in-memory variant still needs a syntactically valid product connection.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Emenda",
            connection ?? "Host=127.0.0.1;Port=1;Database=emenda_inmemory_tests;Username=unused");
        Environment.SetEnvironmentVariable("Oss__InternalEndpoint", "127.0.0.1:19041");
        Environment.SetEnvironmentVariable("Oss__InternalSecure", "false");
        Environment.SetEnvironmentVariable("Oss__AccessKey", "intake-test-access");
        Environment.SetEnvironmentVariable("Oss__SecretKey", "intake-test-secret");
        Environment.SetEnvironmentVariable("Oss__BucketName", "emenda-intake-tests");
        Environment.SetEnvironmentVariable("Oss__PublicBaseUrl", "http://127.0.0.1/oss");
    }

    /// <summary>Optional OSS replacement; defaults to a temporary file store.</summary>
    public IOssService? Oss { get; init; }

    /// <summary>Creates a client that presents the given product caller key.</summary>
    public HttpClient Client(string apiKey = CallerKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", apiKey);
        return client;
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

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<MistakeDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
            var memory = $"emenda_tests_{Guid.NewGuid():N}";
            services.AddDbContext<MistakeDbContext>(options =>
            {
                if (_connection is null) options.UseInMemoryDatabase(memory);
                else options.UseNpgsql(_connection);
            });

            services.RemoveAll<IOssService>();
            services.AddSingleton<IOssService>(Oss ?? new LocalFileOssService(_ossRoot));

        });
    }
}

internal sealed class FakeOssService : IOssService
{
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
    public Task CopyObjectAsync(string sourcePath, string destPath) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(string objectPath) => throw new NotImplementedException();
    public Task<int> DeleteManyAsync(IEnumerable<string> objectPaths) => throw new NotImplementedException();
    public Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600) =>
        throw new NotImplementedException();
    public Task<bool> CheckConnectivityAsync() => throw new NotImplementedException();
    public Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null) => throw new NotImplementedException();
    public Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null) =>
        throw new NotImplementedException();
}
