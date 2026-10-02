using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class BackupRestorePostgresTests
{
    private const string Initial = "20260514051544_InitialCreate";
    private const string ProcessLog = "20260612030015_AddStudentIdToProcessLog";
    private const string Reanalyze = "20260626000000_AddReanalyzeJob";
    private static readonly string[] BusinessTables = ["mistake_item", "mistake_practice", "process_log", "reanalyze_job"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisstampedTextPathsRestoreIsVerifiedBeforeUpgradeAndSourceStaysUnchangedAsync(bool lowercaseIndex)
    {
        await using var drill = await Drill.CreateAsync();
        await using var source = drill.Context(drill.Source);
        await source.GetService<IMigrator>().MigrateAsync(Initial);
        await drill.ExecuteAsync(drill.Source, "ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        await drill.ExecuteAsync(drill.Source, "ALTER TABLE process_log ADD COLUMN student_id character varying(36)");
        await drill.ExecuteAsync(drill.Source, "CREATE INDEX \"IX_process_log_student_id\" ON process_log (student_id)");
        await drill.ExecuteAsync(drill.Source, $"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{ProcessLog}','8.0.11')");
        await source.GetService<IMigrator>().MigrateAsync(Reanalyze);
        if (lowercaseIndex)
            await drill.ExecuteAsync(drill.Source, "ALTER INDEX \"IX_process_log_student_id\" RENAME TO ix_process_log_student_id");
        await SeedAsync(drill, true);
        var before = await drill.ManifestAsync(drill.Source, true);
        Assert.Equal(3, before.HistoryCount);
        var file = await drill.DumpAsync();
        Assert.False(drill.IsBackupVerified);
        var restored = await drill.NewDatabaseAsync();
        await drill.RestoreAsync(file, restored);
        Assert.True(drill.IsBackupVerified);
        Assert.Equal(before, await drill.ManifestAsync(restored, true));
        await using var restoredContext = drill.Context(restored);
        await DatabaseInitializer.UpgradeKnownAsync(restoredContext, restored, true, drill.IsBackupVerified);
        await DatabaseInitializer.InitializeAsync(restoredContext, NullLoggerFactory.Instance);
        Assert.Equal(5, (await drill.ManifestAsync(restored, true)).HistoryCount);
        using var paths = JsonDocument.Parse(await drill.ScalarAsync(restored,
            "SELECT image_paths::text FROM mistake_practice WHERE image_paths <> '[]'::jsonb"));
        Assert.Equal(new[] { "Pictures/中文/A.JPG", "pictures/a.jpg" },
            paths.RootElement.EnumerateArray().Select(path => path.GetString()));
        Assert.Equal(before, await drill.ManifestAsync(drill.Source, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DumpRestoresIntoFreshDatabaseAndPreservesRowsBeforeAndAfterUpgradeAsync(bool targetVersion)
    {
        await using var drill = await Drill.CreateAsync();
        await using var source = drill.Context(drill.Source);
        await source.GetService<IMigrator>().MigrateAsync(targetVersion ? null : Initial);
        await SeedAsync(drill, targetVersion);
        var before = await drill.ManifestAsync(drill.Source, targetVersion);
        var file = await drill.DumpAsync();
        Assert.False(drill.IsBackupVerified);
        var restored = await drill.NewDatabaseAsync();
        await drill.RestoreAsync(file, restored);
        Assert.True(drill.IsBackupVerified);
        Assert.Equal(before, await drill.ManifestAsync(restored, targetVersion));

        await using var restoredContext = drill.Context(restored);
        if (!targetVersion)
        {
            await DatabaseInitializer.UpgradeKnownAsync(restoredContext, restored, true, true);
            await DatabaseInitializer.InitializeAsync(restoredContext, NullLoggerFactory.Instance);
            var upgraded = await drill.ManifestAsync(restored, true);
            Assert.Equal(5, upgraded.HistoryCount);
            Assert.StartsWith(before.RowCounts, upgraded.RowCounts);
            Assert.StartsWith(before.Ids, upgraded.Ids);
            using var paths = JsonDocument.Parse(await drill.ScalarAsync(restored,
                "SELECT image_paths::text FROM mistake_practice WHERE image_paths <> '[]'::jsonb"));
            Assert.Equal(new[] { "Pictures/中文/A.JPG", "pictures/a.jpg" },
                paths.RootElement.EnumerateArray().Select(path => path.GetString()));
            Assert.Equal(before, await drill.ManifestAsync(drill.Source, false));
        }
        else
        {
            await DatabaseInitializer.InitializeAsync(restoredContext, NullLoggerFactory.Instance);
            await DatabaseInitializer.UpgradeKnownAsync(restoredContext, restored, true, true);
            Assert.Equal(before, await drill.ManifestAsync(restored, true));
        }

        // A second independent restore proves that the dump remains usable after the first run.
        var again = await drill.NewDatabaseAsync();
        await drill.RestoreAsync(file, again);
        Assert.Equal(before, await drill.ManifestAsync(again, targetVersion));
        Assert.Equal(before, await drill.ManifestAsync(drill.Source, targetVersion));
    }

    [Fact]
    public async Task FailedRestoreCannotVerifyBackupAndRetryUsesAnotherEmptyDatabaseAsync()
    {
        await using var drill = await Drill.CreateAsync();
        await using var source = drill.Context(drill.Source);
        await source.GetService<IMigrator>().MigrateAsync(Initial);
        await SeedAsync(drill, false);
        var original = await drill.ManifestAsync(drill.Source, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => drill.DumpAsync(denyAccess: true));
        Assert.False(drill.IsBackupVerified);
        Assert.Equal(original, await drill.ManifestAsync(drill.Source, false));
        var file = await drill.DumpAsync();
        Assert.False(drill.IsBackupVerified);
        var truncated = Path.Combine(drill.Directory, "truncated.dump");
        var bytes = await File.ReadAllBytesAsync(file);
        await File.WriteAllBytesAsync(truncated, bytes[..Math.Min(100, bytes.Length)]);
        var failedTarget = await drill.NewDatabaseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => drill.RestoreAsync(truncated, failedTarget));
        Assert.False(drill.IsVerified(failedTarget));
        Assert.False(drill.IsBackupVerified);
        Assert.Equal(original, await drill.ManifestAsync(drill.Source, false));

        var canceledTarget = await drill.NewDatabaseAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drill.RestoreAsync(file, canceledTarget, canceled.Token));
        Assert.False(drill.IsVerified(canceledTarget));
        Assert.False(drill.IsBackupVerified);
        var interruptedTarget = await drill.NewDatabaseAsync();
        using var interrupt = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            drill.RestoreAsync(file, interruptedTarget, interrupt.Token, onStarted: interrupt.Cancel));
        Assert.False(drill.IsVerified(interruptedTarget));
        Assert.False(drill.IsBackupVerified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => drill.RestoreAsync(file, drill.Source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => drill.RestoreAsync(file, "postgres"));
        Assert.Equal(original, await drill.ManifestAsync(drill.Source, false));

        var readonlyTarget = await drill.NewDatabaseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => drill.RestoreAsync(file, readonlyTarget, readOnly: true));
        Assert.False(drill.IsVerified(readonlyTarget));
        Assert.False(drill.IsBackupVerified);
        Assert.Equal(original, await drill.ManifestAsync(drill.Source, false));

        var retry = await drill.NewDatabaseAsync();
        await drill.RestoreAsync(file, retry);
        Assert.True(drill.IsVerified(retry));
        Assert.True(drill.IsBackupVerified);
        Assert.Equal(original, await drill.ManifestAsync(retry, false));
        Assert.Equal(original, await drill.ManifestAsync(drill.Source, false));
    }

    [Fact]
    public async Task ConcurrentDrillsUseSeparateSourceAndRestoreDatabasesAsync()
    {
        var runs = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var drill = await Drill.CreateAsync();
            await using var context = drill.Context(drill.Source);
            await context.GetService<IMigrator>().MigrateAsync(Initial);
            await SeedAsync(drill, false);
            var expected = await drill.ManifestAsync(drill.Source, false);
            var dump = await drill.DumpAsync();
            var target = await drill.NewDatabaseAsync();
            await drill.RestoreAsync(dump, target);
            Assert.Equal(expected, await drill.ManifestAsync(target, false));
            return (drill.Source, target);
        }));
        Assert.Equal(4, runs.SelectMany(run => new[] { run.Source, run.target }).Distinct().Count());
    }

    private static async Task SeedAsync(Drill drill, bool targetVersion)
    {
        var item = Guid.NewGuid();
        var practice = Guid.NewGuid();
        var log = Guid.NewGuid();
        var cause = targetVersion ? "root_cause" : "review_comment";
        await drill.ExecuteAsync(drill.Source, $"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,{cause},created_at) VALUES ('{item}','student-A',1,7,'Upload/大小写','[{{\"path\":\"Pictures/中文/A.JPG\"}}]'::jsonb,1,'合成原因',now())");
        await drill.ExecuteAsync(drill.Source, $"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-A',now(),'[\"Pictures/中文/A.JPG\",\"pictures/a.jpg\"]',false)");
        await drill.ExecuteAsync(drill.Source, $"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-A',now(),'[]',true)");
        if (targetVersion)
            await drill.ExecuteAsync(drill.Source, $"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{log}','{item}','student-A',1,now())");
        else
            await drill.ExecuteAsync(drill.Source, $"INSERT INTO process_log (id,mistake_item_id,action,created_at) VALUES ('{log}','{item}',1,now())");
        if (targetVersion)
            await drill.ExecuteAsync(drill.Source, $"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,created_at) VALUES ('{Guid.NewGuid()}','{item}','teacher-A','student-A',now())");
    }

    private sealed record Manifest(string SchemaHash, string DataHash, string RowCounts, string Ids, int HistoryCount);

    private sealed class Drill : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _container;
        private readonly NpgsqlConnectionStringBuilder _settings;
        private readonly HashSet<string> _owned = [];
        private readonly HashSet<string> _verified = [];
        private readonly HashSet<string> _attempted = [];
        private readonly string _source;
        private Manifest? _backupManifest;
        private bool _backupHasJob;
        private bool _backupVerified;

        private Drill(string admin, string container, string directory)
        {
            _admin = admin;
            _container = container;
            _settings = new NpgsqlConnectionStringBuilder(admin);
            Directory = directory;
            _source = "mistake_drill_" + Guid.NewGuid().ToString("N");
        }

        public string Source => _source;
        public string Directory { get; }
        public bool IsVerified(string name) => _verified.Contains(name);
        public bool IsBackupVerified => _backupVerified;

        public static async Task<Drill> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("MISTAKE_BACKUP_TEST_CONNECTION");
            var container = Environment.GetEnvironmentVariable("MISTAKE_BACKUP_TEST_PG_CONTAINER");
            if (string.IsNullOrWhiteSpace(admin) || string.IsNullOrWhiteSpace(container))
                throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_BACKUP_TEST_CONNECTION and MISTAKE_BACKUP_TEST_PG_CONTAINER for a dedicated PostgreSQL 15 container.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(container, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"))
                throw new InvalidOperationException("Invalid test container name.");
            var drill = new Drill(admin, container, Path.Combine(Path.GetTempPath(), "mistake-drill-" + Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(drill.Directory);
            try
            {
                var version = await drill.ScalarAsync(drill._settings.Database!, "SHOW server_version_num");
                if (!version.StartsWith("15", StringComparison.Ordinal))
                    throw new InvalidOperationException("The drill requires PostgreSQL 15.");
                var client = await drill.RunAsync(["exec", container, "pg_dump", "--version"]);
                if (client.ExitCode != 0 || !client.Output.StartsWith("pg_dump (PostgreSQL) 15.", StringComparison.Ordinal))
                    throw new InvalidOperationException("The drill requires the PostgreSQL 15 client in the named container.");
                await drill.CreateDatabaseAsync(drill.Source);
                return drill;
            }
            catch
            {
                await drill.DisposeAsync();
                throw;
            }
        }

        public MistakeDbContext Context(string name) => new(new DbContextOptionsBuilder<MistakeDbContext>()
            .UseNpgsql(Connection(name)).Options);

        private string Connection(string name) => new NpgsqlConnectionStringBuilder(_admin) { Database = name }.ConnectionString;

        private async Task CreateDatabaseAsync(string name)
        {
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            _owned.Add(name);
        }

        public async Task<string> NewDatabaseAsync()
        {
            var name = "mistake_drill_" + Guid.NewGuid().ToString("N");
            await CreateDatabaseAsync(name);
            return name;
        }

        public async Task ExecuteAsync(string name, string sql)
        {
            await using var connection = new NpgsqlConnection(Connection(name));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> ScalarAsync(string name, string sql)
        {
            await using var connection = new NpgsqlConnection(Connection(name));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }

        public async Task<Manifest> ManifestAsync(string name, bool target)
        {
            var schema = await ScalarAsync(name, """
                SELECT COALESCE((SELECT string_agg(table_name||'.'||column_name||':'||udt_name||':'||is_nullable, '|' ORDER BY table_name,ordinal_position)
                  FROM information_schema.columns WHERE table_schema='public'),'') || '|' ||
                  COALESCE((SELECT string_agg(indexname||':'||indexdef, '|' ORDER BY indexname)
                  FROM pg_indexes WHERE schemaname='public'),'') || '|' ||
                  COALESCE((SELECT string_agg(conname||':'||pg_get_constraintdef(oid), '|' ORDER BY conname)
                  FROM pg_constraint WHERE connamespace='public'::regnamespace),'')
                """);
            var data = new StringBuilder();
            var counts = new StringBuilder();
            var ids = new StringBuilder();
            foreach (var table in BusinessTables.Where(t => target || t != "reanalyze_job"))
            {
                var count = await ScalarAsync(name, $"SELECT count(*) FROM {table}");
                var rows = await ScalarAsync(name, $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id)::text,'[]') FROM {table} t");
                var keys = await ScalarAsync(name, $"SELECT COALESCE(string_agg(id::text,',' ORDER BY id),'') FROM {table}");
                counts.Append(table).Append('=').Append(count).Append(';');
                ids.Append(table).Append('=').Append(keys).Append(';');
                data.Append(table).Append('=').Append(rows).Append(';');
            }
            var history = await ScalarAsync(name, "SELECT COALESCE(string_agg(\"MigrationId\"||':'||\"ProductVersion\",',' ORDER BY \"MigrationId\"),'') FROM \"__EFMigrationsHistory\"");
            data.Append(history);
            return new Manifest(Hash(schema), Hash(data.ToString()), counts.ToString(), ids.ToString(), history.Length == 0 ? 0 : history.Split(',').Length);
        }

        public async Task<string> DumpAsync(bool denyAccess = false)
        {
            _backupManifest = null;
            _backupVerified = false;
            var file = Path.Combine(Directory, "source.dump");
            _backupHasJob = int.Parse(await ScalarAsync(Source, "SELECT count(*) FROM \"__EFMigrationsHistory\"")) >= 3;
            var before = await ManifestAsync(Source, _backupHasJob);
            var result = await RunAsync(["exec", "-e", "PGPASSWORD", _container, "pg_dump", "-h", "127.0.0.1", "-U", denyAccess ? "missing_drill_role" : _settings.Username ?? "", "-d", Source, "-Fc", "--no-owner", "--no-acl"], file);
            if (result.ExitCode != 0 || new FileInfo(file).Length == 0)
                throw new InvalidOperationException($"PostgreSQL backup failed (exit {result.ExitCode}); no backup is verified.");
            var after = await ManifestAsync(Source, _backupHasJob);
            if (before != after)
                throw new InvalidOperationException("Source changed during the backup drill; no backup is verified.");
            _backupManifest = before;
            return file;
        }

        public async Task RestoreAsync(string file, string target, CancellationToken cancellationToken = default,
            bool readOnly = false, Action? onStarted = null)
        {
            if (!_owned.Contains(target) || target == Source || _attempted.Contains(target))
                throw new InvalidOperationException("Restore target must be a fresh database owned by this drill.");
            cancellationToken.ThrowIfCancellationRequested();
            _attempted.Add(target);
            var arguments = new List<string> { "exec", "-i", "-e", "PGPASSWORD" };
            if (readOnly) arguments.AddRange(["-e", "PGOPTIONS=-c default_transaction_read_only=on"]);
            arguments.AddRange([_container, "pg_restore", "-h", "127.0.0.1", "-U", _settings.Username ?? "",
                "-d", target, "--exit-on-error", "--single-transaction", "--no-owner", "--no-acl"]);
            var result = await RunAsync(arguments, inputFile: file, cancellationToken: cancellationToken, onStarted: onStarted);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"PostgreSQL restore failed (exit {result.ExitCode}); target remains unverified and must be discarded.");
            var targetManifest = await ManifestAsync(target, _backupHasJob);
            if (_backupManifest is null || _backupManifest != targetManifest)
                throw new InvalidOperationException("Restored catalog or data differs from the source manifest; target remains unverified.");
            _verified.Add(target);
            _backupVerified = true;
        }

        private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, string? outputFile = null,
            string? inputFile = null, CancellationToken cancellationToken = default, Action? onStarted = null)
        {
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = inputFile is not null, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["PGPASSWORD"] = _settings.Password ?? "";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start PostgreSQL client container.");
            FileStream? outputStream = null;
            try
            {
                onStarted?.Invoke();
                var errors = process.StandardError.ReadToEndAsync(cancellationToken);
                Task output;
                if (outputFile is not null)
                {
                    outputStream = File.Create(outputFile);
                    output = process.StandardOutput.BaseStream.CopyToAsync(outputStream, cancellationToken);
                }
                else output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task input = Task.CompletedTask;
                if (inputFile is not null)
                    input = Task.Run(async () =>
                    {
                        await using var file = File.OpenRead(inputFile);
                        await file.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
                        process.StandardInput.Close();
                    }, cancellationToken);
                await Task.WhenAll(output, input, process.WaitForExitAsync(cancellationToken));
                await errors;
                return (process.ExitCode, output is Task<string> text ? text.Result : "");
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            finally
            {
                if (outputStream is not null) await outputStream.DisposeAsync();
            }
        }

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        public async ValueTask DisposeAsync()
        {
            foreach (var name in _owned)
            {
                await using var connection = new NpgsqlConnection(_admin);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
            }
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
