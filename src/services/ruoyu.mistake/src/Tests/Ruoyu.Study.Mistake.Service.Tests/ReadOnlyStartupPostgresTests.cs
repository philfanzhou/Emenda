using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class ReadOnlyStartupPostgresTests
{
    private const string Reanalyze = "20260626000000_AddReanalyzeJob";

    [Fact]
    public async Task EmptyDatabaseRequiresExplicitUpgradeThenStartsWithoutWrites()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var before = await database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Contains("--upgrade-empty-db", error.Message);
        Assert.Equal(before, await database.SnapshotAsync());

        await DatabaseInitializer.UpgradeEmptyAsync(context);
        var after = await database.SnapshotAsync();
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        await DatabaseInitializer.UpgradeEmptyAsync(context);
        Assert.Equal(after, await database.SnapshotAsync());
    }

    [Fact]
    public async Task KnownRuntimeShapeStartsReadOnlyAndReadsRootCauseAndJsonPaths()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Reanalyze);
        await database.ExecuteAsync("ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        var item = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'legacy root',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-1',now(),'[\"uploads/a\"]'::jsonb,false)");
        var before = await database.SnapshotAsync();
        await using var readOnly = database.CreateReadOnlyContext();
        await DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance);
        await DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance);
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal("legacy root", (await context.MistakeItems.SingleAsync()).RootCause);
        Assert.Equal("uploads/a", Assert.Single((await context.MistakePractices.SingleAsync()).ImagePaths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("CREATE TABLE public.mistake (id integer PRIMARY KEY)", "public.mistake")]
    [InlineData("DROP TABLE reanalyze_job", "reanalyze_job")]
    [InlineData("ALTER TABLE mistake_practice ALTER COLUMN image_paths TYPE text USING image_paths::text", "image_paths")]
    [InlineData("ALTER TABLE mistake_item ADD COLUMN review_comment text", "review_comment")]
    [InlineData("ALTER TABLE mistake_item RENAME COLUMN root_cause TO review_comment", "root_cause")]
    [InlineData("DROP INDEX \"IX_process_log_student_id\"", "IX_process_log_student_id")]
    [InlineData("ALTER TABLE mistake_practice DROP CONSTRAINT \"FK_mistake_practice_mistake_item_mistake_item_id\"", "FK_mistake_practice")]
    [InlineData("INSERT INTO \"__EFMigrationsHistory\" VALUES ('99999999999999_Unknown','8.0.11')", "migration history")]
    public async Task ConflictingSchemaOrHistoryIsRejectedWithoutMutation(string mutation, string diagnostic)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await DatabaseInitializer.UpgradeEmptyAsync(context);
        await database.ExecuteAsync(mutation);
        var before = await database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Contains(diagnostic, error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task PartialSchemaAndHistoryOnlyAreRejectedWithoutWrites()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE public.mistake_item (id uuid PRIMARY KEY)");
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task HistoryOnlyIsRejectedWithoutWrites()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL)");
        await database.ExecuteAsync("INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('20260514051544_InitialCreate','8.0.11')");
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task DdlFailureRollsBackAllMigrationObjectsAndHistory()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync("""
            CREATE FUNCTION upgrade_guard.fail_index() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TAG = 'CREATE INDEX' THEN RAISE EXCEPTION 'injected index failure'; END IF;
            END $$
            """);
        await database.ExecuteAsync("CREATE EVENT TRIGGER fail_mistake_index ON ddl_command_end EXECUTE FUNCTION upgrade_guard.fail_index()");
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.UpgradeEmptyAsync(context));
        Assert.Contains("injected index failure", error.ToString());
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task ConcurrentUpgradeProducesOneVerifiedTarget()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = database.CreateContext();
            await DatabaseInitializer.UpgradeEmptyAsync(context);
        }));
        await using var check = database.CreateContext();
        Assert.Equal(4, (await check.Database.GetAppliedMigrationsAsync()).Count());
        await DatabaseInitializer.InitializeAsync(check, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task CancellationAndReadOnlyPrivilegeDoNotCreateHalfSchema()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseInitializer.UpgradeEmptyAsync(context, canceled.Token));
        Assert.Equal(before, await database.SnapshotAsync());

        await using var readOnly = database.CreateReadOnlyContext();
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.UpgradeEmptyAsync(readOnly));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name;
        private readonly string _connection;

        private IsolatedDatabase(string admin, string name)
        {
            _admin = admin;
            _name = name;
            _connection = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(admin))
                throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL admin connection string.");
            var name = "mistake_readonly_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            return new IsolatedDatabase(admin, name);
        }

        public MistakeDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(_connection).Options);

        public MistakeDbContext CreateReadOnlyContext() =>
            new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(
                new NpgsqlConnectionStringBuilder(_connection) { Options = "-c default_transaction_read_only=on" }.ConnectionString).Options);

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> SnapshotAsync()
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT COALESCE((SELECT string_agg(relname || ':' || relkind::text, ',' ORDER BY relname)
                    FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind IN ('r','i')),'') || '|' ||
                    COALESCE((SELECT string_agg(table_name || '.' || column_name || ':' || data_type, ',' ORDER BY table_name,column_name)
                    FROM information_schema.columns WHERE table_schema='public'),'') || '|' ||
                    COALESCE((SELECT string_agg(conname || ':' || contype::text || ':' || pg_get_constraintdef(oid), ',' ORDER BY conname)
                    FROM pg_constraint WHERE connamespace='public'::regnamespace),'') || '|' ||
                    COALESCE((SELECT string_agg(indexname || ':' || indexdef, ',' ORDER BY indexname)
                    FROM pg_indexes WHERE schemaname='public'),'')
                """, connection);
            var catalog = (string)(await command.ExecuteScalarAsync())!;
            command.CommandText = "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL";
            if (!(bool)(await command.ExecuteScalarAsync())!) return catalog;
            command.CommandText = "SELECT COALESCE(string_agg(\"MigrationId\" || ':' || \"ProductVersion\", ',' ORDER BY \"MigrationId\"),'') FROM public.\"__EFMigrationsHistory\"";
            return catalog + "|" + (string)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
