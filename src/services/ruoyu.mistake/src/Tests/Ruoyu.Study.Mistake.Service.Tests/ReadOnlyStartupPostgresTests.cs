using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class ReadOnlyStartupPostgresTests
{
    private const string Initial = "20260514051544_InitialCreate";
    private const string ProcessLog = "20260612030015_AddStudentIdToProcessLog";
    private const string Reanalyze = "20260626000000_AddReanalyzeJob";

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task KnownHistoryUpgradesRowsAndReferencesToOneRepeatableTarget(int version, bool renamed)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(version switch
        {
            1 => Initial, 2 => ProcessLog, 3 => Reanalyze, _ => null
        });
        if (renamed)
            await database.ExecuteAsync("ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        var item = Guid.NewGuid();
        var practice = Guid.NewGuid();
        var log = Guid.NewGuid();
        var job = Guid.NewGuid();
        var cause = renamed || version == 4 ? "root_cause" : "review_comment";
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,{cause},created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'legacy cause',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'[\"uploads/a\",\"uploads/b\"]',false)");
        if (version >= 2)
            await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{log}','{item}','student-1',1,now())");
        else
            await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,action,created_at) VALUES ('{log}','{item}',1,now())");
        if (version >= 3)
            await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,teacher_description,created_at) VALUES ('{job}','{item}','teacher-1','student-1','retry details',now())");
        if (version < 4 && !renamed)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        if (renamed)
            await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        if (version < 4)
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));

        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        var savedItem = await context.MistakeItems.SingleAsync();
        var savedPractice = await context.MistakePractices.SingleAsync();
        var savedLog = await context.ProcessLogs.SingleAsync();
        Assert.Equal(item, savedItem.Id);
        Assert.Equal("legacy cause", savedItem.RootCause);
        Assert.Equal(practice, savedPractice.Id);
        Assert.Equal(item, savedPractice.MistakeItemId);
        Assert.Equal(new[] { "uploads/a", "uploads/b" }, savedPractice.ImagePaths);
        Assert.Equal(log, savedLog.Id);
        Assert.Equal(item, savedLog.MistakeItemId);
        Assert.Equal(version >= 2 ? "student-1" : null, savedLog.StudentId);
        if (version >= 3)
        {
            var savedJob = await context.ReanalyzeJobs.SingleAsync();
            Assert.Equal(job, savedJob.Id);
            Assert.Equal(item, savedJob.ItemId);
            Assert.Equal("retry details", savedJob.TeacherDescription);
        }
        var target = await database.SnapshotAsync();
        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        Assert.Equal(target, await database.SnapshotAsync());
    }

    [Fact]
    public async Task KnownUpgradeRejectsUnverifiedConditionsAndConflictingCatalog()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeKnownAsync(context, "wrong", true, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeKnownAsync(context, database.Name, false, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, false));
        Assert.Equal(before, await database.SnapshotAsync());
        await database.ExecuteAsync("ALTER TABLE process_log ADD COLUMN student_id varchar(36)");
        before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task InvalidJsonRollsBackKnownUpgradeAndConcurrentCommandsProduceTarget()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await setup.GetService<IMigrator>().MigrateAsync(Initial);
        var item = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        var practice = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'invalid JSON secret',false)");
        var before = await database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(setup, database.Name, true, true));
        Assert.DoesNotContain("invalid JSON secret", error.ToString());
        Assert.Equal(before, await database.SnapshotAsync());
        await database.ExecuteAsync($"UPDATE mistake_practice SET image_paths='[\"uploads/fixed\"]' WHERE id='{practice}'");

        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = database.CreateContext();
            await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        }));
        await using var check = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(check, NullLoggerFactory.Instance);
        Assert.Equal(4, (await check.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal("uploads/fixed", Assert.Single((await check.MistakePractices.SingleAsync()).ImagePaths));
    }

    [Fact]
    public async Task KnownUpgradeCancellationReadOnlyRoleAndDdlFailureLeaveSourceIntact()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        var before = await database.SnapshotAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true, canceled.Token));
        Assert.Equal(before, await database.SnapshotAsync());

        await using (var readOnly = database.CreateReadOnlyContext())
            await Assert.ThrowsAnyAsync<Exception>(() =>
                DatabaseInitializer.UpgradeKnownAsync(readOnly, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());

        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync("""
            CREATE FUNCTION upgrade_guard.fail_index() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TAG = 'CREATE INDEX' THEN RAISE EXCEPTION 'injected known upgrade failure'; END IF;
            END $$
            """);
        await database.ExecuteAsync("CREATE EVENT TRIGGER fail_known_upgrade_index ON ddl_command_end EXECUTE FUNCTION upgrade_guard.fail_index()");
        before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task FinalVerificationFailureRollsBackRenameAndHistory()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Reanalyze);
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync("""
            CREATE FUNCTION upgrade_guard.add_unexpected_relation() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TAG = 'ALTER TABLE' THEN
                    CREATE TABLE public.unexpected_during_upgrade (id integer);
                END IF;
            END $$
            """);
        await database.ExecuteAsync("CREATE EVENT TRIGGER inject_known_upgrade_relation ON ddl_command_end EXECUTE FUNCTION upgrade_guard.add_unexpected_relation()");
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("CREATE TABLE public.mistake (id integer PRIMARY KEY)")]
    [InlineData("ALTER TABLE mistake_item ADD COLUMN unexpected text")]
    [InlineData("CREATE INDEX unexpected_index ON mistake_item (grade)")]
    [InlineData("CREATE TRIGGER unexpected_trigger BEFORE INSERT ON mistake_item FOR EACH ROW EXECUTE FUNCTION pg_catalog.suppress_redundant_updates_trigger()")]
    [InlineData("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\"='20260612030015_AddStudentIdToProcessLog'")]
    public async Task KnownUpgradeRejectsUnknownStructuresWithoutMutation(string mutation)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        await database.ExecuteAsync(mutation);
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());
    }

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

        public string Name => _name;

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
