using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private const string RootCause = "20260929000001_RenameReviewCommentToRootCause";

    [Fact]
    public async Task LeaseUpgradeFailsOnlyOwnerlessRunningAndPreservesOtherJobs()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(RootCause);
        var item = Guid.NewGuid();
        var running = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var completed = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,status,created_at) VALUES ('{running}','{item}','teacher-1','student-1',2,now()),('{pending}','{item}','teacher-1','student-1',1,now()),('{completed}','{item}','teacher-1','student-1',3,now())");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(4, (await context.ReanalyzeJobs.SingleAsync(j => j.Id == running)).Status);
        Assert.Equal("upgrade_interrupted", (await context.ReanalyzeJobs.SingleAsync(j => j.Id == running)).ErrorMessage);
        Assert.Equal(1, (await context.ReanalyzeJobs.SingleAsync(j => j.Id == pending)).Status);
        Assert.Equal(3, (await context.ReanalyzeJobs.SingleAsync(j => j.Id == completed)).Status);
        Assert.Equal(1, await context.ProcessLogs.CountAsync(l => l.MistakeItemId == item));
        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        Assert.Equal(1, await context.ProcessLogs.CountAsync(l => l.MistakeItemId == item));
    }

    [Fact]
    public async Task ExactMisstampedTextPathsUpgradePreservesBusinessRowsAndIsRepeatable()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context);
        var item = Guid.NewGuid();
        var practice = Guid.NewGuid();
        var job = Guid.NewGuid();
        var log = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'原因',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'[\"Pictures/中文/A.JPG\",\"pictures/a.jpg\",\"Pictures/中文/A.JPG\"]',false)");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-1',now(),'[]',true)");
        await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{log}','{item}','student-1',1,now())");
        await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,created_at) VALUES ('{job}','{item}','teacher-1','student-1',now())");
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));
        Assert.Equal(before, await database.SnapshotAsync());

        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(item, (await context.MistakeItems.SingleAsync()).Id);
        Assert.Equal(job, (await context.ReanalyzeJobs.SingleAsync()).Id);
        Assert.Equal(log, (await context.ProcessLogs.SingleAsync()).Id);
        var practices = await context.MistakePractices.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, practices.Count);
        Assert.Equal(new[] { "Pictures/中文/A.JPG", "pictures/a.jpg", "Pictures/中文/A.JPG" },
            practices.Single(row => row.Id == practice).ImagePaths);
        Assert.Empty(practices.Single(row => row.Id != practice).ImagePaths);
        var target = await database.SnapshotAsync();
        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        Assert.Equal(target, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("invalid JSON secret")]
    [InlineData("{\"path\":\"uploads/a\"}")]
    [InlineData("[\"uploads/a\",null]")]
    [InlineData("[\"uploads/a\",42]")]
    public async Task MisstampedTextPathsRejectsNonStringArraysWithoutMutation(string value)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context);
        var item = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-1',now(),'{value}',false)");
        var before = await database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.DoesNotContain(value, error.ToString());
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("CREATE TABLE public.mistake (id integer PRIMARY KEY)")]
    [InlineData("DROP INDEX \"IX_process_log_student_id\"")]
    [InlineData("ALTER TABLE mistake_practice DROP CONSTRAINT \"FK_mistake_practice_mistake_item_mistake_item_id\"")]
    [InlineData("ALTER TABLE mistake_practice ADD COLUMN unknown text")]
    [InlineData("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20260626000000_AddReanalyzeJob'")]
    public async Task MisstampedTextPathsRejectsCatalogDifferencesWithoutMutation(string mutation)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context);
        await database.ExecuteAsync(mutation);
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task MisstampedTextPathsConcurrentUpgradeAndCancellationAreSafe()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup);
        var before = await database.SnapshotAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(setup, database.Name, true, true, canceled.Token));
        Assert.Equal(before, await database.SnapshotAsync());
        await using (var readOnly = database.CreateReadOnlyContext())
            await Assert.ThrowsAnyAsync<Exception>(() =>
                DatabaseInitializer.UpgradeKnownAsync(readOnly, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());

        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = database.CreateContext();
            await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        }));
        await using var check = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(check, NullLoggerFactory.Instance);
        Assert.Equal(5, (await check.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task MisstampedTextPathsConversionFailureRollsBackColumnAndHistory()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context);
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync("""
            CREATE FUNCTION upgrade_guard.reject_conversion() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TAG = 'ALTER TABLE' THEN RAISE EXCEPTION 'injected conversion failure'; END IF;
            END $$
            """);
        await database.ExecuteAsync("CREATE EVENT TRIGGER reject_misstamped_conversion ON ddl_command_end EXECUTE FUNCTION upgrade_guard.reject_conversion()");
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task MisstampedTextPathsCancellationAfterConversionRollsBackRowsAndHistory()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup);
        var (item, practice, log, job) = await SeedMisstampedRowsAsync(database);
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync("""
            CREATE FUNCTION upgrade_guard.pause_after_conversion() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TAG = 'ALTER TABLE' THEN PERFORM pg_sleep(20); END IF;
            END $$
            """);
        await database.ExecuteAsync("CREATE EVENT TRIGGER pause_misstamped_conversion ON ddl_command_end EXECUTE FUNCTION upgrade_guard.pause_after_conversion()");
        var before = await database.SnapshotAsync();
        var rowsBefore = await database.BusinessRowsAsync();
        var applicationName = "mistake_cancel_" + Guid.NewGuid().ToString("N");
        await using var upgrading = database.CreateContext(applicationName: applicationName);
        using var cancellation = new CancellationTokenSource();
        var upgrade = DatabaseInitializer.UpgradeKnownAsync(upgrading, database.Name, true, true, cancellation.Token);
        try
        {
            await database.WaitForConversionPauseAsync(applicationName);
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => upgrade);
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(rowsBefore, await database.BusinessRowsAsync());
        await database.ExecuteAsync("DROP EVENT TRIGGER pause_misstamped_conversion");
        await database.ExecuteAsync("DROP SCHEMA upgrade_guard CASCADE");

        await using var retry = database.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.InitializeAsync(retry, NullLoggerFactory.Instance));
        await DatabaseInitializer.UpgradeKnownAsync(retry, database.Name, true, true);
        await AssertMisstampedTargetAsync(retry, item, practice, log, job);
    }

    [Fact]
    public async Task MisstampedTextPathsLostCommitAcknowledgementIsReadOnlyRecheckedBeforeRetry()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup);
        var (item, practice, log, job) = await SeedMisstampedRowsAsync(database);
        var before = await database.SnapshotAsync();
        var rowsBefore = await database.BusinessRowsAsync();
        await using (var upgrading = database.CreateContext(new LostCommitAcknowledgementInterceptor()))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                DatabaseInitializer.UpgradeKnownAsync(upgrading, database.Name, true, true));
        }

        // The client saw no COMMIT result. Discard pooled sessions and inspect the durable state.
        NpgsqlConnection.ClearAllPools();
        var committed = await database.SnapshotAsync();
        var committedRows = await database.BusinessRowsAsync();
        Assert.NotEqual(before, committed);
        Assert.NotEqual(rowsBefore, committedRows);
        await using var recheck = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(recheck, NullLoggerFactory.Instance);
        await AssertMisstampedTargetAsync(recheck, item, practice, log, job);
        Assert.Equal(committed, await database.SnapshotAsync());
        Assert.Equal(committedRows, await database.BusinessRowsAsync());
        await using var retry = database.CreateContext();
        await DatabaseInitializer.UpgradeKnownAsync(retry, database.Name, true, true);
        Assert.Equal(committed, await database.SnapshotAsync());
        Assert.Equal(committedRows, await database.BusinessRowsAsync());
    }

    private static async Task<(Guid Item, Guid Practice, Guid Log, Guid Job)> SeedMisstampedRowsAsync(IsolatedDatabase database)
    {
        var item = Guid.NewGuid();
        var practice = Guid.NewGuid();
        var log = Guid.NewGuid();
        var job = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'cause',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'[\"Pictures/中文/A.JPG\",\"pictures/a.jpg\",\"Pictures/中文/A.JPG\"]',false)");
        await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{log}','{item}','student-1',1,now())");
        await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,created_at) VALUES ('{job}','{item}','teacher-1','student-1',now())");
        return (item, practice, log, job);
    }

    private static async Task AssertMisstampedTargetAsync(MistakeDbContext context, Guid item, Guid practice, Guid log, Guid job)
    {
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(item, (await context.MistakeItems.SingleAsync()).Id);
        var savedPractice = await context.MistakePractices.SingleAsync();
        Assert.Equal(practice, savedPractice.Id);
        Assert.Equal(item, savedPractice.MistakeItemId);
        Assert.Equal(new[] { "Pictures/中文/A.JPG", "pictures/a.jpg", "Pictures/中文/A.JPG" }, savedPractice.ImagePaths);
        Assert.Equal(log, (await context.ProcessLogs.SingleAsync()).Id);
        Assert.Equal(job, (await context.ReanalyzeJobs.SingleAsync()).Id);
    }

    private sealed class LostCommitAcknowledgementInterceptor : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated lost COMMIT acknowledgement.");
    }

    private static async Task CreateMisstampedTextPathsAsync(IsolatedDatabase database, MistakeDbContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        await database.ExecuteAsync("ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        await database.ExecuteAsync("ALTER TABLE process_log ADD COLUMN student_id character varying(36)");
        await database.ExecuteAsync("CREATE INDEX \"IX_process_log_student_id\" ON process_log (student_id)");
        await database.ExecuteAsync($"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{ProcessLog}','8.0.11')");
        await context.GetService<IMigrator>().MigrateAsync(Reanalyze);
    }

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
            1 => Initial, 2 => ProcessLog, 3 => Reanalyze, 4 => RootCause, _ => null
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
        if (version < 5 && !renamed)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        if (renamed)
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        if (version < 4)
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeEmptyAsync(context));

        await DatabaseInitializer.UpgradeKnownAsync(context, database.Name, true, true);
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync()).Count());
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
        Assert.Equal(5, (await check.Database.GetAppliedMigrationsAsync()).Count());
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
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync()).Count());
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance));
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
        Assert.Equal(5, (await check.Database.GetAppliedMigrationsAsync()).Count());
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

        public MistakeDbContext CreateContext(DbTransactionInterceptor? interceptor = null, string? applicationName = null)
        {
            var connection = new NpgsqlConnectionStringBuilder(_connection);
            if (applicationName != null) connection.ApplicationName = applicationName;
            var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection.ConnectionString);
            if (interceptor != null) options.AddInterceptors(interceptor);
            return new MistakeDbContext(options.Options);
        }

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

        public async Task<string> BusinessRowsAsync()
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT
                    (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id)::text, '[]') FROM mistake_item t) || '|' ||
                    (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id)::text, '[]') FROM mistake_practice t) || '|' ||
                    (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id)::text, '[]') FROM process_log t) || '|' ||
                    (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id)::text, '[]') FROM reanalyze_job t)
                """, connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task WaitForConversionPauseAsync(string applicationName)
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event = 'PgSleep'
                      AND query LIKE 'ALTER TABLE public.mistake_practice ALTER COLUMN image_paths TYPE jsonb%')
                """, connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!timeout.IsCancellationRequested)
            {
                if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
                await Task.Delay(25, timeout.Token);
            }
            Assert.Fail("Conversion did not reach the PostgreSQL DDL completion pause.");
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
