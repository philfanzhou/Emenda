using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Ruoyu.Study.Mistake.Database;

namespace Ruoyu.Study.Mistake.Service.Tests;

public class ImagePathsMigrationPostgresTests
{
    private const string Initial = "20260514051544_InitialCreate";
    private const string Conversion = "20260612030015_AddStudentIdToProcessLog";

    [Fact]
    public async Task EmptyDatabase_MigratesToLatestAndRepeatIsNoOp()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();

        await context.Database.MigrateAsync();
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(4, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('mistake_item','mistake_practice','process_log','reanalyze_job')"));
        Assert.Equal("jsonb", await database.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' AND table_name='mistake_practice' AND column_name='image_paths'"));
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM information_schema.columns WHERE table_schema='public' AND table_name='mistake_item' AND column_name='root_cause'"));
        await context.Database.MigrateAsync();
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task ExistingRows_SurviveUpDownUpWithKeysAndPaths()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Initial);

        var itemId = Guid.NewGuid();
        var practiceId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        await database.SeedAsync(itemId, practiceId, logId, "[]");
        var secondPracticeId = Guid.NewGuid();
        await database.ExecuteAsync("INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES (@id,@item,'student-1',now(),@paths,false)",
            new NpgsqlParameter("id", secondPracticeId), new NpgsqlParameter("item", itemId),
            new NpgsqlParameter("paths", "[\"uploads/中文 A.png\",\"Uploads/B.jpg\"]"));

        await migrator.MigrateAsync(Conversion);
        await AssertConvertedAsync(database, itemId, practiceId, logId, secondPracticeId);
        await migrator.MigrateAsync(Initial);
        Assert.Equal("text", await database.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name='mistake_practice' AND column_name='image_paths'"));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM information_schema.columns WHERE table_name='process_log' AND column_name='student_id'"));
        await migrator.MigrateAsync(Conversion);
        await AssertConvertedAsync(database, itemId, practiceId, logId, secondPracticeId);

        var before = await database.ScalarAsync<string>("SELECT image_paths::text FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", secondPracticeId));
        await context.Database.MigrateAsync();
        Assert.Equal(before, await database.ScalarAsync<string>("SELECT image_paths::text FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", secondPracticeId)));
    }

    [Fact]
    public async Task InvalidJson_RollsBackEntireMigration_ThenCanRetry()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Initial);
        var itemId = Guid.NewGuid();
        var practiceId = Guid.NewGuid();
        await database.SeedAsync(itemId, practiceId, Guid.NewGuid(), "not-json");

        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Conversion));
        Assert.Equal("22P02", error.SqlState);
        Assert.Equal("not-json", await database.ScalarAsync<string>("SELECT image_paths FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", practiceId)));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"=@id", new NpgsqlParameter("id", Conversion)));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM information_schema.columns WHERE table_name='process_log' AND column_name='student_id'"));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM pg_indexes WHERE tablename='process_log' AND indexname='IX_process_log_student_id'"));

        await database.ExecuteAsync("UPDATE mistake_practice SET image_paths='[]' WHERE id=@id", new NpgsqlParameter("id", practiceId));
        await migrator.MigrateAsync(Conversion);
        Assert.Equal("[]", await database.ScalarAsync<string>("SELECT image_paths::text FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", practiceId)));
    }

    [Fact]
    public async Task CancellationWhileWaitingForTableLock_LeavesHistoryRetryable()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Initial);
        await using var blocker = database.CreateConnection();
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE mistake_practice IN ACCESS EXCLUSIVE MODE", blocker, transaction))
            await command.ExecuteNonQueryAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await migrator.MigrateAsync(Conversion, cancellation.Token);
            Assert.Fail("Migration should stop while waiting for the table lock.");
        }
        catch (OperationCanceledException) { }
        catch (PostgresException error) when (error.SqlState == "57014") { }
        await transaction.RollbackAsync();

        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"=@id", new NpgsqlParameter("id", Conversion)));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM information_schema.columns WHERE table_name='process_log' AND column_name='student_id'"));
        await migrator.MigrateAsync(Conversion);
        Assert.Equal("jsonb", await database.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name='mistake_practice' AND column_name='image_paths'"));
    }

    private static async Task AssertConvertedAsync(IsolatedDatabase database, Guid itemId, Guid practiceId, Guid logId, Guid secondPracticeId)
    {
        Assert.Equal(2, await database.ScalarAsync<int>("SELECT count(*)::integer FROM mistake_practice"));
        Assert.Equal(itemId, await database.ScalarAsync<Guid>("SELECT mistake_item_id FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", practiceId)));
        Assert.Equal(itemId, await database.ScalarAsync<Guid>("SELECT mistake_item_id FROM process_log WHERE id=@id", new NpgsqlParameter("id", logId)));
        Assert.Equal("[]", await database.ScalarAsync<string>("SELECT image_paths::text FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", practiceId)));
        Assert.Equal("[\"uploads/中文 A.png\", \"Uploads/B.jpg\"]", await database.ScalarAsync<string>(
            "SELECT image_paths::text FROM mistake_practice WHERE id=@id", new NpgsqlParameter("id", secondPracticeId)));
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM information_schema.columns WHERE table_name='process_log' AND column_name='student_id'"));
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT count(*)::integer FROM pg_indexes WHERE tablename='process_log' AND indexname='IX_process_log_student_id'"));
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _name;
        private readonly string _connection;

        private IsolatedDatabase(string adminConnection, string name, string connection)
        {
            _adminConnection = adminConnection;
            _name = name;
            _connection = connection;
        }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("MISTAKE_MIGRATION_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection))
                throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_MIGRATION_TEST_CONNECTION to a dedicated PostgreSQL admin connection string.");
            var name = "mistake_migration_" + Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
                await command.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(connection) { Database = name }.ConnectionString;
            return new IsolatedDatabase(connection, name, target);
        }

        public MistakeDbContext CreateContext() => new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(_connection).Options);

        public NpgsqlConnection CreateConnection() => new(_connection);

        public async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddRange(parameters);
            return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Query returned no value."));
        }

        public async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddRange(parameters);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedAsync(Guid itemId, Guid practiceId, Guid logId, string paths)
        {
            await ExecuteAsync("INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES (@id,'student-1',1,1,'upload-1','[]',1,now())", new NpgsqlParameter("id", itemId));
            await ExecuteAsync("INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES (@id,@item,'student-1',now(),@paths,false)",
                new NpgsqlParameter("id", practiceId), new NpgsqlParameter("item", itemId), new NpgsqlParameter("paths", paths));
            await ExecuteAsync("INSERT INTO process_log (id,mistake_item_id,action,created_at) VALUES (@id,@item,1,now())",
                new NpgsqlParameter("id", logId), new NpgsqlParameter("item", itemId));
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
