using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;

namespace Ruoyu.Study.Mistake.Service.Tests;

public class LegacySchemaGuardPostgresTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, true)]
    public async Task LegacyRelation_IsRejectedBeforeAnyWrite(
        bool newTable, bool history, bool emptyOldTable, bool referencingForeignKey)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await database.CreateLegacyFixtureAsync(newTable, history, emptyOldTable, referencingForeignKey);
        var before = await database.SnapshotAsync();
        await using var context = database.CreateReadOnlyContext();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));

        Assert.Contains("legacy relation public.mistake", error.Message);
        Assert.Contains("Automatic upgrade does not support this structure", error.Message);
        Assert.DoesNotContain("--upgrade", error.Message);
        Assert.DoesNotContain("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task ConcurrentInitializers_AllRejectAndPreserveReferences()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await database.CreateLegacyFixtureAsync(newTable: true, history: true, emptyOldTable: false, referencingForeignKey: true);
        var before = await database.SnapshotAsync();
        var attempts = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var context = database.CreateReadOnlyContext();
            return await Assert.ThrowsAsync<InvalidOperationException>(
                () => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        });
        var errors = await Task.WhenAll(attempts);
        Assert.All(errors, error => Assert.Contains("legacy relation public.mistake", error.Message));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task CheckConnectionFailure_StopsBeforeAnyWrite()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await database.CreateLegacyFixtureAsync(false, false, false, false);
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContextWithConnection(
            new NpgsqlConnectionStringBuilder(database.ConnectionString) { Port = 1, Timeout = 1 }.ConnectionString);

        await Assert.ThrowsAnyAsync<Exception>(
            () => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));

        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoPublicLegacyRelation_EmptyPublicSchemaUpgradesOnStartup(bool sameNameInOtherSchema)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        if (sameNameInOtherSchema)
            await database.ExecuteAsync("CREATE SCHEMA alternate; CREATE TABLE alternate.mistake (id integer PRIMARY KEY)");
        await using var context = database.CreateContextWithConnection(database.ConnectionString);

        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(13, (await context.Database.GetAppliedMigrationsAsync()).Count());
        if (sameNameInOtherSchema)
            Assert.Equal("alternate.mistake", await database.ScalarAsync<string>("SELECT to_regclass('alternate.mistake')::text"));
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _name;
        public string ConnectionString { get; }

        private IsolatedDatabase(string adminConnection, string name, string connectionString)
        {
            _adminConnection = adminConnection;
            _name = name;
            ConnectionString = connectionString;
        }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var adminConnection = Environment.GetEnvironmentVariable("MISTAKE_LEGACY_GUARD_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(adminConnection))
                throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_LEGACY_GUARD_TEST_CONNECTION to a dedicated PostgreSQL admin connection string.");
            var name = "mistake_guard_" + Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
                await command.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
            return new IsolatedDatabase(adminConnection, name, target);
        }

        public MistakeDbContext CreateReadOnlyContext() => CreateContextWithConnection(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Options = "-c default_transaction_read_only=on" }.ConnectionString);

        public MistakeDbContext CreateContextWithConnection(string connectionString) =>
            new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options);

        public async Task CreateLegacyFixtureAsync(bool newTable, bool history, bool emptyOldTable, bool referencingForeignKey)
        {
            await ExecuteAsync("CREATE TABLE public.mistake (id integer PRIMARY KEY, sentinel text NOT NULL); CREATE INDEX mistake_sentinel_idx ON public.mistake(sentinel)");
            if (!emptyOldTable)
                await ExecuteAsync("INSERT INTO public.mistake VALUES (1, 'preserve-me')");
            if (newTable)
                await ExecuteAsync("CREATE TABLE public.mistake_item (id integer PRIMARY KEY, sentinel text NOT NULL); INSERT INTO public.mistake_item VALUES (2, 'keep-new')");
            if (history)
                await ExecuteAsync("CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL); INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('20260514051544_InitialCreate', '8.0.11')");
            if (referencingForeignKey)
            {
                await ExecuteAsync("CREATE TABLE public.probe_reference (id integer PRIMARY KEY, mistake_id integer REFERENCES public.mistake(id))");
                if (!emptyOldTable)
                    await ExecuteAsync("INSERT INTO public.probe_reference VALUES (1, 1)");
            }
        }

        public async Task<string> SnapshotAsync()
        {
            var catalog = await ScalarAsync<string>(@"
                SELECT COALESCE(string_agg(relname || ':' || relkind::text, ',' ORDER BY relname), '')
                FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r','i')");
            var columns = await ScalarAsync<string>(@"
                SELECT COALESCE(string_agg(table_name || '.' || column_name || ':' || data_type, ',' ORDER BY table_name, ordinal_position), '')
                FROM information_schema.columns WHERE table_schema='public'");
            var constraints = await ScalarAsync<string>(@"
                SELECT COALESCE(string_agg(conname || ':' || contype::text, ',' ORDER BY conname), '')
                FROM pg_constraint WHERE connamespace='public'::regnamespace");
            var oldRows = await ScalarAsync<string>("SELECT COALESCE(string_agg(id::text || ':' || sentinel, ',' ORDER BY id), '') FROM public.mistake");
            var historyExists = await ScalarAsync<bool>("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL");
            var history = historyExists
                ? await ScalarAsync<string>("SELECT COALESCE(string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\"), '') FROM public.\"__EFMigrationsHistory\"")
                : string.Empty;
            var newExists = await ScalarAsync<bool>("SELECT to_regclass('public.mistake_item') IS NOT NULL");
            var newRows = newExists
                ? await ScalarAsync<string>("SELECT COALESCE(string_agg(id::text || ':' || sentinel, ',' ORDER BY id), '') FROM public.mistake_item")
                : string.Empty;
            var referenceExists = await ScalarAsync<bool>("SELECT to_regclass('public.probe_reference') IS NOT NULL");
            var referenceRows = referenceExists
                ? await ScalarAsync<string>("SELECT COALESCE(string_agg(id::text || ':' || mistake_id::text, ',' ORDER BY id), '') FROM public.probe_reference")
                : string.Empty;
            return string.Join('|', catalog, columns, constraints, oldRows, history, newRows, referenceRows);
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Query returned no value."));
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminConnection);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}
