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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseUpgradeFailsOnlyOwnerlessRunningAndPreservesOtherJobs(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        if (lowercaseIndex)
            await CreateMisstampedTextPathsAsync(database, context, true);
        else
            await context.GetService<IMigrator>().MigrateAsync(RootCause);
        var item = Guid.NewGuid();
        var running = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var completed = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,status,created_at) VALUES ('{running}','{item}','teacher-1','student-1',2,now()),('{pending}','{item}','teacher-1','student-1',1,now()),('{completed}','{item}','teacher-1','student-1',3,now())");
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(4, (await context.ReanalyzeJobs.SingleAsync(j => j.Id == running)).Status);
        Assert.Equal("upgrade_interrupted", (await context.ReanalyzeJobs.SingleAsync(j => j.Id == running)).ErrorMessage);
        Assert.Equal(1, (await context.ReanalyzeJobs.SingleAsync(j => j.Id == pending)).Status);
        Assert.Equal(3, (await context.ReanalyzeJobs.SingleAsync(j => j.Id == completed)).Status);
        Assert.Equal(1, await context.ProcessLogs.CountAsync(l => l.MistakeItemId == item));
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(1, await context.ProcessLogs.CountAsync(l => l.MistakeItemId == item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactMisstampedTextPathsUpgradePreservesBusinessRowsAndIsRepeatable(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context, lowercaseIndex);
        var item = Guid.NewGuid();
        var practice = Guid.NewGuid();
        var job = Guid.NewGuid();
        var log = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'原因',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'[\"Pictures/中文/A.JPG\",\"pictures/a.jpg\",\"Pictures/中文/A.JPG\"]',false)");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-1',now(),'[]',true)");
        await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{log}','{item}','student-1',1,now())");
        await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,created_at) VALUES ('{job}','{item}','teacher-1','student-1',now())");
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(item, (await context.MistakeItems.SingleAsync()).Id);
        Assert.Equal(job, (await context.ReanalyzeJobs.SingleAsync()).Id);
        Assert.Equal(log, (await context.ProcessLogs.SingleAsync()).Id);
        var practices = await context.MistakePractices.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, practices.Count);
        Assert.Equal(new[] { "Pictures/中文/A.JPG", "pictures/a.jpg", "Pictures/中文/A.JPG" },
            practices.Single(row => row.Id == practice).ImagePaths);
        Assert.Empty(practices.Single(row => row.Id != practice).ImagePaths);
        var target = await database.SnapshotAsync();
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(target, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("invalid JSON secret", false)]
    [InlineData("invalid JSON secret", true)]
    [InlineData("{\"path\":\"uploads/a\"}", false)]
    [InlineData("{\"path\":\"uploads/a\"}", true)]
    [InlineData("[\"uploads/a\",null]", false)]
    [InlineData("[\"uploads/a\",null]", true)]
    [InlineData("[\"uploads/a\",42]", false)]
    [InlineData("[\"uploads/a\",42]", true)]
    public async Task MisstampedTextPathsArchivesAndResetsNonStringArrays(string value, bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context, lowercaseIndex);
        var item = Guid.NewGuid();
        var invalid = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{invalid}','{item}','student-1',now(),'{value}',false)");
        var result = await DatabaseInitializer.UpgradeToTargetAsync(context);
        Assert.Equal(1, result.ResetImagePaths);
        Assert.Equal(1, result.ArchivedRows);
        Assert.DoesNotContain(value, string.Join(",", result.Steps));
        Assert.Empty((await context.MistakePractices.SingleAsync()).ImagePaths);
        Assert.Equal($"mistake_practice|{invalid}|invalid_value|{value}|invalid_image_paths_reset", await database.ScalarAsync<string>(
            "SELECT source_table || '|' || source_key || '|' || kind || '|' || (payload->>'image_paths') || '|' || reason FROM legacy_archive"));
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
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisstampedTextPathsConcurrentUpgradeAndCancellationAreSafe(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup, lowercaseIndex);
        var before = await database.SnapshotAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseInitializer.InitializeAsync(setup, NullLoggerFactory.Instance, canceled.Token));
        Assert.Equal(before, await database.SnapshotAsync());
        await using (var readOnly = database.CreateReadOnlyContext())
            await Assert.ThrowsAnyAsync<Exception>(() =>
                DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());

        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = database.CreateContext();
            await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        }));
        await using var check = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(check, NullLoggerFactory.Instance);
        Assert.Equal(11, (await check.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisstampedTextPathsConversionFailureRollsBackColumnAndHistory(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context, lowercaseIndex);
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
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisstampedTextPathsCancellationAfterConversionRollsBackRowsAndHistory(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup, lowercaseIndex);
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
        var upgrade = DatabaseInitializer.InitializeAsync(upgrading, NullLoggerFactory.Instance, cancellation.Token);
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
        await DatabaseInitializer.InitializeAsync(retry, NullLoggerFactory.Instance);
        await AssertMisstampedTargetAsync(retry, item, practice, log, job);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisstampedTextPathsLostCommitAcknowledgementIsReadOnlyRecheckedBeforeRetry(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup, lowercaseIndex);
        var (item, practice, log, job) = await SeedMisstampedRowsAsync(database);
        var before = await database.SnapshotAsync();
        var rowsBefore = await database.BusinessRowsAsync();
        await using (var upgrading = database.CreateContext(new LostCommitAcknowledgementInterceptor()))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                DatabaseInitializer.InitializeAsync(upgrading, NullLoggerFactory.Instance));
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
        await DatabaseInitializer.InitializeAsync(retry, NullLoggerFactory.Instance);
        Assert.Equal(committed, await database.SnapshotAsync());
        Assert.Equal(committedRows, await database.BusinessRowsAsync());
    }

    [Fact]
    public async Task LowercaseEmptyShapeUpgradesOnStartupButRejectsWrongExplicitDatabaseAsync()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context, true);
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context, "wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context, ""));
        Assert.Equal(before, await database.SnapshotAsync());
        var result = await DatabaseInitializer.UpgradeToTargetAsync(context, database.Name);
        Assert.Equal(3, result.FromVersion);
        Assert.Equal(11, result.ToVersion);
        Assert.Contains("rename ix_process_log_student_id", result.Steps);
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Empty(await context.MistakeItems.ToListAsync());
        Assert.Empty(await context.MistakePractices.ToListAsync());
        Assert.Empty(await context.ProcessLogs.ToListAsync());
        Assert.Empty(await context.ReanalyzeJobs.ToListAsync());
    }

    [Theory]
    [InlineData("DROP INDEX ix_process_log_student_id")]
    [InlineData("CREATE INDEX \"IX_process_log_student_id\" ON process_log (student_id)")]
    [InlineData("DROP INDEX ix_process_log_student_id; CREATE UNIQUE INDEX ix_process_log_student_id ON process_log (student_id)")]
    [InlineData("DROP INDEX ix_process_log_student_id; CREATE INDEX ix_process_log_student_id ON process_log (action)")]
    [InlineData("DROP INDEX ix_process_log_student_id; CREATE INDEX ix_process_log_student_id ON process_log (student_id) WHERE student_id IS NOT NULL")]
    [InlineData("DROP INDEX ix_process_log_student_id; CREATE INDEX ix_process_log_student_id ON process_log (lower(student_id))")]
    [InlineData("DROP INDEX ix_process_log_student_id; CREATE INDEX ix_process_log_student_id ON process_log USING hash (student_id)")]
    [InlineData("DROP INDEX ix_process_log_student_id; CREATE INDEX ix_process_log_student_id ON process_log (student_id) INCLUDE (action)")]
    [InlineData("ALTER TABLE mistake_item ADD COLUMN unknown text")]
    [InlineData("CREATE TABLE public.mistake(id integer)")]
    [InlineData("UPDATE \"__EFMigrationsHistory\" SET \"ProductVersion\"='8.0.10'")]
    public async Task LowercaseVariantRejectsAnyCatalogDeviationWithoutWritesAsync(string mutation)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context, true);
        await SeedMisstampedRowsAsync(database);
        await database.ExecuteAsync(mutation);
        var before = await database.SnapshotAsync();
        var rows = await database.BusinessRowsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context, database.Name));
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(rows, await database.BusinessRowsAsync());
    }

    [Theory]
    [InlineData("ALTER INDEX")]
    [InlineData("ALTER TABLE")]
    public async Task LowercaseRenameOrConversionFailureRollsBackEveryBusinessRowAsync(string stage)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, context, true);
        await SeedMisstampedRowsAsync(database);
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync($"CREATE FUNCTION upgrade_guard.fail_stage() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN IF TG_TAG = '{stage}' THEN RAISE EXCEPTION 'injected failure'; END IF; END $$");
        await database.ExecuteAsync("CREATE EVENT TRIGGER fail_stage ON ddl_command_end EXECUTE FUNCTION upgrade_guard.fail_stage()");
        var before = await database.SnapshotAsync();
        var rows = await database.BusinessRowsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(rows, await database.BusinessRowsAsync());
    }

    [Fact]
    public async Task LowercaseLockWaitCancellationDoesNotRenameOrChangeRowsAsync()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup, true);
        await SeedMisstampedRowsAsync(database);
        var before = await database.SnapshotAsync();
        var rows = await database.BusinessRowsAsync();
        await using var locking = database.CreateContext();
        await using var transaction = await locking.Database.BeginTransactionAsync();
        await locking.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(5932735329232308563)");
        await using var upgrading = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.InitializeAsync(upgrading, NullLoggerFactory.Instance, cancellation.Token));
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(rows, await database.BusinessRowsAsync());
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
        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
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

    private static async Task CreateMisstampedTextPathsAsync(IsolatedDatabase database, MistakeDbContext context, bool lowercaseIndex = false)
    {
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        await database.ExecuteAsync("ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        await database.ExecuteAsync("ALTER TABLE process_log ADD COLUMN student_id character varying(36)");
        await database.ExecuteAsync("CREATE INDEX \"IX_process_log_student_id\" ON process_log (student_id)");
        await database.ExecuteAsync($"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{ProcessLog}','8.0.11')");
        await context.GetService<IMigrator>().MigrateAsync(Reanalyze);
        if (lowercaseIndex)
            await database.ExecuteAsync("ALTER INDEX \"IX_process_log_student_id\" RENAME TO ix_process_log_student_id");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    public async Task KnownHistoryUpgradesRowsAndReferencesToOneRepeatableTarget(int version, bool renamed)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(version switch
        {
            1 => Initial, 2 => ProcessLog, 3 => Reanalyze, 4 => RootCause, 5 => "20260930000001_LeaseReanalyzeJobs", 6 => SourceIntakeSchema.Migration, 7 => SourceTargetSchema.Migration, 8 => SourceReturnSchema.Migration, 9 => SourceHandoffSchema.Migration,
            10 => Ruoyu.Study.Common.StorageReferences.StorageReferenceSchema.MistakeMigration, _ => null
        });
        if (renamed)
            await database.ExecuteAsync("ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        var item = Guid.NewGuid();
        var practice = Guid.NewGuid();
        var log = Guid.NewGuid();
        var job = Guid.NewGuid();
        var cause = renamed || version >= 4 ? "root_cause" : "review_comment";
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,{cause},created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'legacy cause',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'[\"uploads/a\",\"uploads/b\"]',false)");
        if (version >= 2)
            await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{log}','{item}','student-1',1,now())");
        else
            await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,action,created_at) VALUES ('{log}','{item}',1,now())");
        if (version >= 3)
            await database.ExecuteAsync($"INSERT INTO reanalyze_job (id,item_id,reviewer_id,student_id,teacher_description,created_at) VALUES ('{job}','{item}','teacher-1','student-1','retry details',now())");
        // Normal startup is the upgrade entry; no command, flag or backup precondition is involved.
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
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
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(target, await database.SnapshotAsync());
    }

    [Fact]
    public async Task KnownUpgradeRejectsWrongExplicitDatabaseAndConflictingCatalog()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        var before = await database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context, "wrong"));
        Assert.Contains("--database", error.Message);
        Assert.Equal(before, await database.SnapshotAsync());
        await database.ExecuteAsync("ALTER TABLE process_log ADD COLUMN student_id varchar(36)");
        before = await database.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task InvalidJsonOnKnownUpgradeIsArchivedOnceUnderConcurrentStartups()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await setup.GetService<IMigrator>().MigrateAsync(Initial);
        var item = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        var practice = Guid.NewGuid();
        var valid = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{practice}','{item}','student-1',now(),'invalid JSON secret',false),('{valid}','{item}','student-1',now(),'[\"uploads/fixed\"]',true)");

        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = database.CreateContext();
            await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        }));
        await using var check = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(check, NullLoggerFactory.Instance);
        Assert.Equal(11, (await check.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty((await check.MistakePractices.SingleAsync(row => row.Id == practice)).ImagePaths);
        Assert.Equal("uploads/fixed", Assert.Single((await check.MistakePractices.SingleAsync(row => row.Id == valid)).ImagePaths));
        Assert.Equal($"{practice}:invalid JSON secret", await database.ScalarAsync<string>(
            "SELECT string_agg(source_key || ':' || (payload->>'image_paths'), ',') FROM legacy_archive"));
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
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance, canceled.Token));
        Assert.Equal(before, await database.SnapshotAsync());

        await using (var readOnly = database.CreateReadOnlyContext())
            await Assert.ThrowsAnyAsync<Exception>(() =>
                DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance));
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
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalVerificationFailureRollsBackRenameAndHistory(bool lowercaseIndex)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        if (lowercaseIndex)
        {
            await CreateMisstampedTextPathsAsync(database, context, true);
            await SeedMisstampedRowsAsync(database);
        }
        else
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
        var rows = await database.BusinessRowsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(rows, await database.BusinessRowsAsync());
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
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task EmptyDatabaseUpgradesOnStartupThenStartsWithoutWrites()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        var after = await database.SnapshotAsync();
        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        var repeated = await DatabaseInitializer.UpgradeToTargetAsync(context);
        Assert.False(repeated.Upgraded);
        Assert.Equal(11, repeated.FromVersion);
        Assert.Equal(after, await database.SnapshotAsync());
    }

    [Fact]
    public async Task KnownRuntimeShapeNeedsWritableAccountThenUpgradesOnStartup()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Reanalyze);
        await database.ExecuteAsync("ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause");
        var item = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,'legacy root',now())");
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-1',now(),'[\"uploads/a\"]'::jsonb,false)");
        var before = await database.SnapshotAsync();
        await using (var readOnly = database.CreateReadOnlyContext())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance));
            Assert.Contains("SQLSTATE 25006", error.Message);
        }
        Assert.Equal(before, await database.SnapshotAsync());
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(item, (await context.MistakeItems.SingleAsync()).Id);
        Assert.Equal("legacy root", (await context.MistakeItems.SingleAsync()).RootCause);
        Assert.Equal("uploads/a", Assert.Single((await context.MistakePractices.SingleAsync()).ImagePaths));
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
        await DatabaseInitializer.UpgradeToTargetAsync(context);
        await database.ExecuteAsync(mutation);
        var before = await database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Contains(diagnostic, error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context));
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Contains("SQLSTATE P0001", error.Message);
        Assert.Contains("database stays unchanged", error.Message);
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task ConcurrentUpgradeProducesOneVerifiedTarget()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var context = database.CreateContext();
            return await DatabaseInitializer.UpgradeToTargetAsync(context);
        }));
        Assert.Single(results, result => result.Upgraded);
        await using var check = database.CreateContext();
        Assert.Equal(11, (await check.Database.GetAppliedMigrationsAsync()).Count());
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance, canceled.Token));
        Assert.Equal(before, await database.SnapshotAsync());

        await using var readOnly = database.CreateReadOnlyContext();
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.UpgradeToTargetAsync(readOnly));
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task KnownHistoryWithDuplicateSourceItemsUpgradesOnStartupAndKeepsEveryRow(int version)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(version switch
        {
            1 => Initial, 3 => Reanalyze, 5 => "20260930000001_LeaseReanalyzeJobs", 9 => SourceHandoffSchema.Migration, _ => null
        });
        var items = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var item in items)
        {
            // Same student, same source upload and same regions: legacy rows carry no uniqueness guarantee.
            await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-dup','[{{\"sourceImagePath\":\"uploads/a.jpg\"}}]'::jsonb,1,now())");
            await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{item}','student-1',now(),'[\"uploads/p.jpg\"]',false)");
            if (version >= 2)
                await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,student_id,action,created_at) VALUES ('{Guid.NewGuid()}','{item}','student-1',1,now())");
            else
                await database.ExecuteAsync($"INSERT INTO process_log (id,mistake_item_id,action,created_at) VALUES ('{Guid.NewGuid()}','{item}',1,now())");
        }

        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);

        Assert.Equal(11, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(items.Order(), (await context.MistakeItems.Select(row => row.Id).ToListAsync()).Order());
        foreach (var item in items)
        {
            Assert.Single(await context.MistakePractices.Where(row => row.MistakeItemId == item).ToListAsync());
            Assert.Single(await context.ProcessLogs.Where(row => row.MistakeItemId == item).ToListAsync());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task ConcurrentStartupsOnKnownHistoryUpgradeOnceWithoutDuplicateHistory(int version)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
            await setup.GetService<IMigrator>().MigrateAsync(version == 1 ? Initial : "20260930000001_LeaseReanalyzeJobs");
        var item = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{item}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var context = database.CreateContext();
            return await DatabaseInitializer.UpgradeToTargetAsync(context);
        }));

        var upgraded = Assert.Single(results, result => result.Upgraded);
        Assert.Equal(version, upgraded.FromVersion);
        Assert.All(results.Where(result => !result.Upgraded), result => Assert.Equal(11, result.FromVersion));
        await using var check = database.CreateContext();
        Assert.Equal(11, (await check.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(item, (await check.MistakeItems.SingleAsync()).Id);
    }

    [Fact]
    public async Task LimitedAccountStartsOnTargetWithoutWritesButCannotUpgrade()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using (var owner = database.CreateContext())
            await DatabaseInitializer.InitializeAsync(owner, NullLoggerFactory.Instance);
        var target = await database.SnapshotAsync();
        await using (var limited = await database.CreateLimitedContextAsync())
        {
            var result = await DatabaseInitializer.UpgradeToTargetAsync(limited);
            Assert.False(result.Upgraded);
            await DatabaseInitializer.InitializeAsync(limited, NullLoggerFactory.Instance);
        }
        await using (var readOnly = database.CreateReadOnlyContext())
            await DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance);
        Assert.Equal(target, await database.SnapshotAsync());

        await using var known = await IsolatedDatabase.CreateAsync();
        await using (var owner = known.CreateContext())
            await owner.GetService<IMigrator>().MigrateAsync(Reanalyze);
        await known.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{Guid.NewGuid()}','student-1',1,7,'upload-1','[]'::jsonb,1,now())");
        var before = await known.SnapshotAsync();
        var rows = await known.BusinessRowsAsync();
        await using (var limited = await known.CreateLimitedContextAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(limited, NullLoggerFactory.Instance));
            Assert.Contains("SQLSTATE 42501", error.Message);
            Assert.DoesNotContain("Password", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before, await known.SnapshotAsync());
        Assert.Equal(rows, await known.BusinessRowsAsync());

        await using var empty = await IsolatedDatabase.CreateAsync();
        var emptyBefore = await empty.SnapshotAsync();
        await using (var limited = await empty.CreateLimitedContextAsync())
            Assert.Contains("SQLSTATE 42501", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(limited, NullLoggerFactory.Instance))).Message);
        Assert.Equal(emptyBefore, await empty.SnapshotAsync());
    }

    [Fact]
    public async Task ConnectionTerminatedDuringUpgradeRollsBackAndNextStartupUpgrades()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        await CreateMisstampedTextPathsAsync(database, setup, true);
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
        var applicationName = "mistake_terminate_" + Guid.NewGuid().ToString("N");
        await using (var upgrading = database.CreateContext(applicationName: applicationName))
        {
            var upgrade = DatabaseInitializer.InitializeAsync(upgrading, NullLoggerFactory.Instance);
            await database.WaitForConversionPauseAsync(applicationName);
            await database.ExecuteAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '{applicationName}'");
            await Assert.ThrowsAnyAsync<Exception>(() => upgrade);
        }
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(rowsBefore, await database.BusinessRowsAsync());
        await database.ExecuteAsync("DROP EVENT TRIGGER pause_misstamped_conversion");
        await database.ExecuteAsync("DROP SCHEMA upgrade_guard CASCADE");

        await using var retry = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(retry, NullLoggerFactory.Instance);
        await AssertMisstampedTargetAsync(retry, item, practice, log, job);
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name;
        private readonly string _connection;
        private readonly List<string> _roles = [];

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

        /// <summary>A login that may connect and read existing tables but owns nothing and cannot create.</summary>
        public async Task<MistakeDbContext> CreateLimitedContextAsync()
        {
            var role = "mistake_limited_" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N");
            _roles.Add(role);
            await ExecuteAsync($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}'; " +
                $"GRANT CONNECT ON DATABASE \"{_name}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\"; " +
                $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{role}\"");
            var connection = new NpgsqlConnectionStringBuilder(_connection) { Username = role, Password = password };
            return new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection.ConnectionString).Options);
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

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
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
            foreach (var role in _roles)
            {
                command.CommandText = $"DROP ROLE IF EXISTS \"{role}\"";
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
