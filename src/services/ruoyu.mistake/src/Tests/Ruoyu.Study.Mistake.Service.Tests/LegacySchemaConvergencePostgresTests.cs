using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Common.StorageReferences;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using static Ruoyu.Study.Mistake.Service.Tests.LegacyShapeFixtures;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Startup convergence of the structures left by the automatic schema maintenance of historical builds (IKK6UF).
/// Each shape is produced by replaying the DDL those builds executed, including their sample rows.
/// </summary>
public sealed class LegacySchemaConvergencePostgresTests
{
    private const string Initial = "20260514051544_InitialCreate";
    private static readonly Guid Item1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Item2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Item3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Item4 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static TheoryData<string, string> Shapes => new()
    {
        // shape, expected archive rows per source table
        { "A", "__EFMigrationsHistory:2,mistake_detail:1" },
        { "B", "__EFMigrationsHistory:2,mistake_detail:1,mistake_item:2" },
        { "C", "mistake_item:1,mistake_practice:1" },
        { "E", "mistake_practice:1" },
        { "G", "__EFMigrationsHistory:1,mistake_detail:1,mistake_image_fingerprint:1" },
        { "F1", "__EFMigrationsHistory:2,mistake_detail:1,mistake_item:1" },
        { "F2", "__EFMigrationsHistory:2,mistake_detail:1,mistake_item:1" },
        { "F3", "__EFMigrationsHistory:2,mistake_detail:1,mistake_item:1" },
        { "Fs", "__EFMigrationsHistory:2,mistake_detail:1,mistake_item:1,mistake_practice:1,process_log:1" },
        { "Fs2", "__EFMigrationsHistory:2,mistake_detail:1,mistake_item:1,mistake_practice:1,process_log:1" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task LegacyShapeConvergesOnStartupKeepsRowsAndArchivesEveryRemovedValue(string shape, string expectedArchive)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        var before = await database.LegacyRowsAsync();

        await using (var context = database.CreateContext())
            await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);

        await using var check = database.CreateContext();
        Assert.Equal(13, (await check.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(before.Items.Order(), (await check.MistakeItems.Select(row => row.Id).ToListAsync()).Order());
        var orphanPractices = before.Practices.Where(p => !before.Items.Contains(p.Parent)).Select(p => p.Id).ToHashSet();
        var orphanLogs = before.Logs.Where(l => !before.Items.Contains(l.Parent)).Select(l => l.Id).ToHashSet();
        var practices = await check.MistakePractices.ToListAsync();
        Assert.Equal(before.Practices.Where(p => !orphanPractices.Contains(p.Id)).Select(p => p.Id).Order(),
            practices.Select(p => p.Id).Order());
        foreach (var practice in practices)
        {
            var original = before.Practices.Single(p => p.Id == practice.Id);
            Assert.Equal(original.Parent, practice.MistakeItemId);
            Assert.Equal(StringArrayOrEmpty(original.Paths), practice.ImagePaths);
        }
        var logs = await check.ProcessLogs.ToListAsync();
        Assert.Equal(before.Logs.Where(l => !orphanLogs.Contains(l.Id)).Select(l => l.Id).Order(), logs.Select(l => l.Id).Order());
        Assert.All(logs, log => Assert.Equal(before.Logs.Single(l => l.Id == log.Id).Parent, log.MistakeItemId));

        var archive = await database.ArchiveAsync();
        Assert.Equal(expectedArchive, string.Join(',', archive.GroupBy(row => row.Table).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")));
        Assert.Equal(archive.Count, archive.Select(row => (row.Table, row.Key, row.Kind)).Distinct().Count());
        foreach (var row in archive)
        {
            switch (row.Table, row.Kind)
            {
                case ("__EFMigrationsHistory", "history_row"):
                    Assert.Contains(row.Key, new[] { "20260501042151_InitialCreate", "20260503021905_RemoveImageFingerprint" });
                    break;
                case ("mistake_detail", "row"):
                    Assert.Contains(Guid.Parse(row.Key), before.Details);
                    Assert.Equal("老备注", row.Payload.GetProperty("comments").GetString());
                    break;
                case ("mistake_image_fingerprint", "row"):
                    Assert.Equal("mistakes/s1/a.jpg", row.Payload.GetProperty("oss_path").GetString());
                    break;
                case ("mistake_item", "column_values"):
                    Assert.Contains(Guid.Parse(row.Key), before.Items);
                    Assert.True(row.Payload.TryGetProperty("type", out _));
                    break;
                case ("mistake_practice", "row"):
                    Assert.Contains(Guid.Parse(row.Key), orphanPractices);
                    break;
                case ("process_log", "row"):
                    Assert.Contains(Guid.Parse(row.Key), orphanLogs);
                    break;
                case ("mistake_practice", "invalid_value"):
                    Assert.Equal("not json secret-path", row.Payload.GetProperty("image_paths").GetString());
                    break;
                default:
                    Assert.Fail($"Unexpected archive row {row.Table}/{row.Kind}.");
                    break;
            }
        }

        // Converged and archived once: a later start writes nothing.
        var target = await database.SnapshotAsync();
        await using (var again = database.CreateContext())
        {
            var repeated = await DatabaseInitializer.UpgradeToTargetAsync(again);
            Assert.False(repeated.Upgraded);
            await DatabaseInitializer.InitializeAsync(again, NullLoggerFactory.Instance);
        }
        Assert.Equal(target, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("A", "11111111-1111-1111-1111-111111111111", "mistakes/s1/a.jpg|-,mistakes/s1/b.jpg|-")]
    [InlineData("B", "11111111-1111-1111-1111-111111111111", "mistakes/s1/a.jpg|-,mistakes/s1/b.jpg|-")]
    [InlineData("G", "11111111-1111-1111-1111-111111111111", "mistakes/s1/a.jpg|-,mistakes/s1/b.jpg|-")]
    [InlineData("F1", "33333333-3333-3333-3333-333333333333", "mistakes/s1/c.jpg|-")]
    [InlineData("F2", "33333333-3333-3333-3333-333333333333", "mistakes/s1/c.jpg|1,2,30,40")]
    [InlineData("F3", "33333333-3333-3333-3333-333333333333", "mistakes/s1/c.jpg|-")]
    [InlineData("Fs", "33333333-3333-3333-3333-333333333333", "mistakes/s1/c.jpg|-")]
    public async Task LegacyItemImagesAreVisibleThroughItemReadsAndPresignedOwnership(string shape, string item, string regions)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        await using (var context = database.CreateContext())
            await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);

        await using var check = database.CreateContext();
        var repository = new MistakeItemRepository(check);
        var model = await repository.GetByIdAsync(Guid.Parse(item));
        Assert.NotNull(model);
        Assert.Equal(regions, string.Join(',', model!.SourceRegions.Select(region => region.SourceImagePath + "|" +
            (region.BoundingBox is { } box ? $"{box.X1},{box.Y1},{box.X2},{box.Y2}" : "-"))));
        foreach (var region in model.SourceRegions)
            Assert.True(await repository.IsObjectPathReferencedByStudentsAsync(region.SourceImagePath, ["s1"]));
        Assert.False(await repository.IsObjectPathReferencedByStudentsAsync("mistakes/s1/a.jpg", ["s2"]));
    }

    [Theory]
    [InlineData("Fs", 6)]
    [InlineData("P2", 7)]
    public async Task ConcurrentStartupsConvergeOnceAndArchiveOnce(string shape, int count)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var context = database.CreateContext();
            return await DatabaseInitializer.UpgradeToTargetAsync(context);
        }));
        var upgraded = Assert.Single(results, result => result.Upgraded);
        Assert.Equal(count, upgraded.ArchivedRows);
        Assert.All(results.Where(result => !result.Upgraded), result => Assert.Equal(13, result.FromVersion));
        Assert.Equal(count, (await database.ArchiveAsync()).Count);
    }

    [Theory]
    [InlineData("DROP TABLE", "Fs")]
    [InlineData("DROP TABLE", "P2")]
    [InlineData("ALTER TABLE", "Fs")]
    [InlineData("ALTER TABLE", "P2")]
    [InlineData("CREATE INDEX", "Fs")]
    [InlineData("CREATE INDEX", "P2")]
    [InlineData("CREATE TABLE", "Fs")]
    [InlineData("CREATE TABLE", "P2")]
    public async Task FailureAtAnyStageRollsBackConvergenceAndArchive(string stage, string shape)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        await database.ExecuteAsync($"CREATE FUNCTION upgrade_guard.fail_stage() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN IF TG_TAG = '{stage}' THEN RAISE EXCEPTION 'injected failure'; END IF; END $$");
        await database.ExecuteAsync("CREATE EVENT TRIGGER fail_stage ON ddl_command_end EXECUTE FUNCTION upgrade_guard.fail_stage()");
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Contains("SQLSTATE P0001", error.Message);
        Assert.Contains("database stays unchanged", error.Message);
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("A")]
    [InlineData("P2")]
    public async Task ReadOnlyAndLimitedAccountsCannotConvergeAndLeaveDatabaseUnchanged(string shape)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        var before = await database.SnapshotAsync();
        await using (var readOnly = database.CreateContext(readOnly: true))
            Assert.Contains("SQLSTATE 25006", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(readOnly, NullLoggerFactory.Instance))).Message);
        await using (var limited = await database.CreateLimitedContextAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(limited, NullLoggerFactory.Instance));
            Assert.Contains("SQLSTATE 42501", error.Message);
            Assert.DoesNotContain("Password", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData(false, "A", 3)]
    [InlineData(true, "A", 3)]
    [InlineData(false, "P2", 7)]
    [InlineData(true, "P2", 7)]
    public async Task CancellationOrConnectionLossDuringConvergenceRollsBackAndNextStartConverges(bool terminate, string shape, int count)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        await database.PauseOnAsync("DROP TABLE", 20);
        var before = await database.SnapshotAsync();
        var applicationName = "mistake_legacy_" + Guid.NewGuid().ToString("N");
        await using (var upgrading = database.CreateContext(applicationName: applicationName))
        {
            using var cancellation = new CancellationTokenSource();
            var upgrade = DatabaseInitializer.InitializeAsync(upgrading, NullLoggerFactory.Instance, cancellation.Token);
            try
            {
                await database.WaitForPauseAsync(applicationName, "DROP TABLE public.mistake_detail%");
            }
            finally
            {
                if (terminate)
                    await database.ExecuteAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '{applicationName}'");
                else
                    cancellation.Cancel();
            }
            await Assert.ThrowsAnyAsync<Exception>(() => upgrade);
        }
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(before, await database.SnapshotAsync());
        await database.ExecuteAsync("DROP EVENT TRIGGER pause_stage; DROP SCHEMA upgrade_guard CASCADE");

        await using var retry = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(retry, NullLoggerFactory.Instance);
        Assert.Equal(13, (await retry.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(count, (await database.ArchiveAsync()).Count);
    }

    [Theory]
    [InlineData("A", "CREATE TABLE public.stranger (id integer)", "stranger")]
    [InlineData("A", "CREATE INDEX odd_grade_idx ON mistake_item (grade)", "odd_grade_idx")]
    [InlineData("B", "CREATE INDEX odd_type_idx ON mistake_item (type)", "odd_type_idx")]
    [InlineData("A", "CREATE INDEX odd_detail_idx ON mistake_detail (comments)", "odd_detail_idx")]
    [InlineData("A", "ALTER TABLE mistake_detail ADD COLUMN extra text", "mistake_detail")]
    [InlineData("A", "ALTER TABLE mistake_item ADD COLUMN extra text", "extra")]
    [InlineData("Fs", "CREATE TABLE public.reanalyze_job (id uuid PRIMARY KEY)", "reanalyze_job")]
    [InlineData("C", "CREATE TABLE public.mistake (id integer PRIMARY KEY)", "public.mistake")]
    public async Task UnknownObjectsBesideLegacyMarkersAreRejectedWithoutChanges(string shape, string mutation, string diagnostic)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        await database.ExecuteAsync(mutation);
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.True(error.Message.Contains(diagnostic), error.Message);
        Assert.DoesNotContain("老备注", error.ToString());
        Assert.DoesNotContain("Password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task StepsLongerThanTheCommandTimeoutStillComplete()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, "B");
        await database.PauseOnAsync("ALTER TABLE", 2);
        await using var context = database.CreateContext(commandTimeout: 1);
        var configuredTimeout = context.Database.GetCommandTimeout();
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(13, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(configuredTimeout, context.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task WaitingForTheUpgradeLockLongerThanTheCommandTimeoutStillCompletes()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, "E");
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(5932735329232308563)", holder))
            await command.ExecuteNonQueryAsync();
        await using var context = database.CreateContext(commandTimeout: 1);
        var upgrade = DatabaseInitializer.UpgradeToTargetAsync(context);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(upgrade.IsCompleted);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_unlock(5932735329232308563)", holder))
            await command.ExecuteNonQueryAsync();
        var result = await upgrade;
        Assert.Equal(3, result.FromVersion);
        Assert.Equal(13, result.ToVersion);
        Assert.Equal(1, result.ResetImagePaths);
    }

    [Fact]
    public async Task CancellationStillStopsAnUpgradeWithoutCommandTimeout()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, "B");
        await database.PauseOnAsync("ALTER TABLE", 20);
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext(commandTimeout: 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance, cancellation.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(before, await database.SnapshotAsync());
    }

    [Fact]
    public async Task InvalidPathsOfTheFirstVersionAreArchivedAndReset()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
            await setup.GetService<IMigrator>().MigrateAsync(Initial);
        await database.ExecuteAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('{Item4}','s1',1,7,'u','[]',1,now())");
        var invalid = Guid.NewGuid();
        var valid = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{invalid}','{Item4}','s1',now(),'not json secret-path',false),('{valid}','{Item4}','s1',now(),'[\"uploads/a\"]',true)");
        await using var context = database.CreateContext();
        var result = await DatabaseInitializer.UpgradeToTargetAsync(context);
        Assert.Equal(1, result.FromVersion);
        Assert.Equal(1, result.ResetImagePaths);
        Assert.Equal(1, result.ArchivedRows);
        Assert.Empty((await context.MistakePractices.SingleAsync(p => p.Id == invalid)).ImagePaths);
        Assert.Equal(new[] { "uploads/a" }, (await context.MistakePractices.SingleAsync(p => p.Id == valid)).ImagePaths);
        var row = Assert.Single(await database.ArchiveAsync());
        Assert.Equal(("mistake_practice", invalid.ToString(), "invalid_value"), (row.Table, row.Key, row.Kind));
    }

    [Fact]
    public async Task ArchiveIsAppendOnlyAndBlocksDowngradeWhileItHasRows()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, "A");
        await using var context = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        foreach (var statement in new[] { "UPDATE legacy_archive SET reason='x'", "DELETE FROM legacy_archive", "TRUNCATE legacy_archive" })
            Assert.Contains("legacy_archive_append_only",
                (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(statement))).MessageText);
        // Test the archive migration's own atomic downgrade; EF commits separate migrations independently.
        await context.GetService<IMigrator>().MigrateAsync(LegacyArchiveSchema.Migration);
        var before = await database.SnapshotAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => context.GetService<IMigrator>().MigrateAsync(StorageReferenceSchema.MistakeMigration));
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.Equal(3, (await database.ArchiveAsync()).Count);
    }

    [Fact]
    public async Task EmptyArchiveDowngradesToTheTenthMigrationAndStartupUpgradesAgain()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        await context.GetService<IMigrator>().MigrateAsync(StorageReferenceSchema.MistakeMigration);
        Assert.Equal(10, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(await database.ScalarAsync<bool>("SELECT to_regclass('public.legacy_archive') IS NOT NULL"));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM pg_proc WHERE proname='guard_legacy_archive'"));
        var result = await DatabaseInitializer.UpgradeToTargetAsync(context);
        Assert.Equal(10, result.FromVersion);
        Assert.Equal(13, (await context.Database.GetAppliedMigrationsAsync()).Count());
    }

    private static string[] StringArrayOrEmpty(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                   document.RootElement.EnumerateArray().All(element => element.ValueKind == JsonValueKind.String)
                ? document.RootElement.EnumerateArray().Select(element => element.GetString()!).ToArray()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }


    [Theory]
    [InlineData("P0", 5)]
    [InlineData("P0f", 6)]
    [InlineData("P1", 7)]
    [InlineData("P1x", 6)]
    [InlineData("P2", 7)]
    [InlineData("P2f", 8)]
    [InlineData("M-da", 9)]
    [InlineData("M-05", 9)]
    [InlineData("M-e0", 9)]
    [InlineData("M-e0f", 10)]
    public async Task OldTableLayoutsPreserveRowsRelationsImagesAndOriginalArchive(string shape, int count)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, shape);
        var oldItems = await database.ScalarAsync<string>("SELECT jsonb_object_agg(id::text,to_jsonb(m))::text FROM mistake m");
        var oldPractices = await database.ScalarAsync<string>("SELECT jsonb_object_agg(id::text,to_jsonb(p)-'mistake_id'-'image_paths')::text FROM mistake_practice p");
        var oldLogs = await database.ScalarAsync<string>("SELECT jsonb_object_agg(id::text,to_jsonb(p)-'mistake_id'-'action')::text FROM process_log p");
        var mixed = shape.StartsWith("M-", StringComparison.Ordinal);
        var existing = mixed ? await database.ScalarAsync<string>("SELECT jsonb_object_agg(id::text,to_jsonb(i)-'type'-'source_image_path'-'bbox_x1'-'bbox_y1'-'bbox_x2'-'bbox_y2'-'question_number'-'question_text'-'student_answer'-'ai_confidence'-'source_regions')::text FROM mistake_item i") : null;
        await using (var context = database.CreateContext())
        {
            var result = await DatabaseInitializer.UpgradeToTargetAsync(context);
            Assert.Equal(count, result.ArchivedRows);
            Assert.Equal(13, (await context.Database.GetAppliedMigrationsAsync()).Count());
        }
        Assert.False(await database.ScalarAsync<bool>("SELECT to_regclass('public.mistake') IS NOT NULL"));
        Assert.Equal(oldPractices, await database.ScalarAsync<string>("SELECT jsonb_object_agg(id::text,to_jsonb(p)-'mistake_item_id'-'image_paths')::text FROM mistake_practice p"));
        Assert.Equal(oldLogs, await database.ScalarAsync<string>("SELECT jsonb_object_agg(id::text,to_jsonb(p)-'mistake_item_id'-'action'-'student_id')::text FROM process_log p"));
        Assert.True(await database.ScalarAsync<bool>("SELECT bool_and(action=CASE details WHEN 'created' THEN 1 WHEN 'old-action-2' THEN 2 WHEN 'old-action-3' THEN 5 WHEN 'old-action-4' THEN 6 END) FROM process_log"));
        Assert.True(await database.ScalarAsync<bool>("SELECT bool_and(to_jsonb(i)-'source_regions'-'source_upload_id'-'reviewer_id'-'reviewed_at'-'root_cause'-'review_status'=a.payload-'date'-'type'-'status' AND i.review_status=(a.payload->>'status')::integer) FROM mistake_item i JOIN legacy_archive a ON a.source_key=i.id::text WHERE a.reason='legacy_mistake_converted'"));
        Assert.Equal(4L, await database.ScalarAsync<long>($"SELECT count(*) FROM process_log WHERE mistake_item_id='{Item2}' AND action IN (1,2,5,6)"));
        Assert.Equal(Item1, await database.ScalarAsync<Guid>("SELECT mistake_item_id FROM mistake_practice"));
        Assert.Equal("[\"mistakes/s1/p.jpg\"]", await database.ScalarAsync<string>("SELECT image_paths::text FROM mistake_practice"));
        Assert.Equal(2, await database.ScalarAsync<int>($"SELECT review_status FROM mistake_item WHERE id='{Item2}'"));
        Assert.Equal("", await database.ScalarAsync<string>($"SELECT source_upload_id FROM mistake_item WHERE id='{Item2}'"));
        Assert.Equal(oldItems, await database.ScalarAsync<string>("SELECT jsonb_object_agg(source_key,payload)::text FROM legacy_archive WHERE source_table='mistake'"));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM legacy_archive WHERE reason='legacy_action_renumbered' AND payload IN ('{\"action\":3}'::jsonb,'{\"action\":4}'::jsonb)"));
        Assert.Equal(mixed ? 1L : 0L, await database.ScalarAsync<long>("SELECT count(*) FROM legacy_archive WHERE reason='legacy_mistake_superseded'"));
        Assert.Equal(mixed ? 2L : 0L, await database.ScalarAsync<long>("SELECT count(*) FROM legacy_archive WHERE source_table='mistake_item'"));
        if (mixed)
            Assert.Equal(existing, await database.ScalarAsync<string>($"SELECT jsonb_object_agg(id::text,to_jsonb(i)-'root_cause'-'source_regions' || jsonb_build_object('review_comment',root_cause))::text FROM mistake_item i WHERE id IN ('{Item1}','{Item3}')"));
        await using var read = database.CreateContext();
        var repository = new MistakeItemRepository(read);
        var item = await repository.GetByIdAsync(Item1);
        Assert.NotNull(item);
        Assert.Equal(mixed ? "mistakes/s1/c.jpg" : "mistakes/s1/a.jpg", item!.SourceRegions[0].SourceImagePath);
        if (shape == "M-da")
        {
            Assert.NotNull(item.SourceRegions[0].BoundingBox);
            var box = item.SourceRegions[0].BoundingBox!;
            Assert.Equal((1, 2, 30, 40), (box.X1, box.Y1, box.X2, box.Y2));
        }
        else Assert.All(item.SourceRegions, region => Assert.Null(region.BoundingBox));
        foreach (var region in item.SourceRegions)
        {
            Assert.True(await repository.IsObjectPathReferencedByStudentsAsync(region.SourceImagePath, ["s1"]));
            Assert.False(await repository.IsObjectPathReferencedByStudentsAsync(region.SourceImagePath, ["s2"]));
        }
        var snapshot = await database.SnapshotAsync();
        await using var again = database.CreateContext();
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(again)).Upgraded);
        Assert.Equal(snapshot, await database.SnapshotAsync());
    }

    [Theory]
    [InlineData("CREATE INDEX stranger ON mistake(grade)")]
    [InlineData("ALTER TABLE mistake ADD COLUMN stranger text")]
    [InlineData("ALTER TABLE mistake ADD COLUMN stranger integer GENERATED ALWAYS AS IDENTITY")]
    [InlineData("ALTER TABLE mistake ADD COLUMN stranger integer GENERATED BY DEFAULT AS IDENTITY")]
    [InlineData("ALTER TABLE mistake_practice ADD COLUMN stranger integer GENERATED ALWAYS AS IDENTITY")]
    [InlineData("ALTER TABLE mistake_practice ADD COLUMN stranger integer GENERATED BY DEFAULT AS IDENTITY")]
    [InlineData("ALTER TABLE process_log ADD COLUMN stranger integer GENERATED ALWAYS AS IDENTITY")]
    [InlineData("ALTER TABLE process_log ADD COLUMN stranger integer GENERATED BY DEFAULT AS IDENTITY")]
    [InlineData("ALTER TABLE mistake_detail ADD COLUMN stranger integer GENERATED ALWAYS AS IDENTITY")]
    [InlineData("ALTER TABLE mistake_detail ADD COLUMN stranger integer GENERATED BY DEFAULT AS IDENTITY")]
    [InlineData("ALTER TABLE mistake_image_fingerprint ADD COLUMN stranger integer GENERATED ALWAYS AS IDENTITY")]
    [InlineData("ALTER TABLE mistake_image_fingerprint ADD COLUMN stranger integer GENERATED BY DEFAULT AS IDENTITY")]
    [InlineData("ALTER TABLE mistake ADD COLUMN stranger integer GENERATED ALWAYS AS (1) STORED")]
    [InlineData("ALTER TABLE mistake_practice ADD COLUMN stranger integer GENERATED ALWAYS AS (1) STORED")]
    [InlineData("ALTER TABLE process_log ADD COLUMN stranger integer GENERATED ALWAYS AS (1) STORED")]
    [InlineData("ALTER TABLE mistake_detail ADD COLUMN stranger integer GENERATED ALWAYS AS (1) STORED")]
    [InlineData("ALTER TABLE mistake_image_fingerprint ADD COLUMN stranger integer GENERATED ALWAYS AS (1) STORED")]
    [InlineData("ALTER TABLE mistake ALTER COLUMN grade ADD GENERATED BY DEFAULT AS IDENTITY")]
    [InlineData("CREATE FUNCTION odd_trigger() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; CREATE TRIGGER stranger BEFORE UPDATE ON mistake FOR EACH ROW EXECUTE FUNCTION odd_trigger()")]
    [InlineData("ALTER TABLE mistake_practice ALTER COLUMN image_paths TYPE jsonb USING image_paths::jsonb")]
    [InlineData("ALTER TABLE mistake_detail ALTER COLUMN comments SET NOT NULL")]
    [InlineData("ALTER TABLE process_log DROP CONSTRAINT \"FK_process_log_mistake_mistake_id\"; ALTER TABLE process_log ADD CONSTRAINT stranger FOREIGN KEY(mistake_id) REFERENCES mistake(id)")]
    public async Task UnknownOldTableLayoutIsRejectedBeforeWrites(string mutation)
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, "P1");
        await database.ExecuteAsync(mutation);
        var original = await database.SnapshotAsync();
        await using (var initialContext = database.CreateContext())
        {
            var initialError = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(initialContext, NullLoggerFactory.Instance));
            Assert.Contains("legacy relation public.mistake exists", initialError.Message);
            Assert.Contains("Automatic upgrade does not support this structure", initialError.Message);
        }
        Assert.Equal(original, await database.SnapshotAsync());
        // A rejecting event trigger proves that even transient DDL is never attempted.
        await database.ExecuteAsync("CREATE SCHEMA upgrade_guard");
        // Sequence changes survive rollback, so this also detects any attempted data write.
        await database.ExecuteAsync("CREATE SEQUENCE upgrade_guard.write_attempt; CREATE FUNCTION upgrade_guard.fail_row() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('upgrade_guard.write_attempt'); RAISE EXCEPTION 'data write attempted'; END $$");
        foreach (var table in new[] { "mistake_practice", "process_log", "mistake_detail", "mistake_image_fingerprint", "__EFMigrationsHistory" })
            await database.ExecuteAsync($"CREATE TRIGGER fail_row BEFORE INSERT OR UPDATE OR DELETE ON public.\"{table}\" FOR EACH STATEMENT EXECUTE FUNCTION upgrade_guard.fail_row()");
        await database.ExecuteAsync("CREATE FUNCTION upgrade_guard.fail_any() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'write attempted'; END $$; CREATE EVENT TRIGGER fail_any ON ddl_command_end EXECUTE FUNCTION upgrade_guard.fail_any()");
        var before = await database.SnapshotAsync();
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance));
        Assert.Contains("legacy relation public.mistake exists", error.Message);
        Assert.Contains("Automatic upgrade does not support this structure", error.Message);
        Assert.Equal(before, await database.SnapshotAsync());
        Assert.False(await database.ScalarAsync<bool>("SELECT is_called FROM upgrade_guard.write_attempt"));
    }

    [Fact]
    public async Task OnlyTheDetailActuallyUsedForImagesGetsTheBackfilledReason()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await BuildShapeAsync(database, "P2");
        await database.ExecuteAsync("DROP INDEX \"IX_mistake_detail_mistake_id\"; CREATE INDEX \"IX_mistake_detail_mistake_id\" ON mistake_detail(mistake_id); INSERT INTO mistake_detail SELECT gen_random_uuid(),mistake_id,'[\"mistakes/s1/other.jpg\"]','second' FROM mistake_detail");
        await using var context = database.CreateContext();
        await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM legacy_archive WHERE reason='legacy_table_dropped_images_backfilled'"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM legacy_archive WHERE source_table='mistake_detail' AND reason='legacy_table_dropped'"));
        Assert.True(await database.ScalarAsync<bool>($"SELECT (SELECT jsonb_agg(jsonb_build_object('sourceImagePath',p,'boundingBox',null)) FROM legacy_archive a CROSS JOIN LATERAL jsonb_array_elements_text((a.payload->>'original_images')::jsonb) p WHERE reason='legacy_table_dropped_images_backfilled')=source_regions FROM mistake_item WHERE id='{Item1}'"));
    }

    private static async Task BuildShapeAsync(IsolatedDatabase database, string shape)
    {
        if (shape.StartsWith("P", StringComparison.Ordinal) || shape.StartsWith("M-", StringComparison.Ordinal))
        {
            var fingerprint = shape is "P0f" or "P1" or "P2f" or "M-e0f";
            var after0503 = shape is "P2" or "P2f" || shape.StartsWith("M-", StringComparison.Ordinal);
            if (after0503)
            {
                await database.ExecuteAsync(Layout0503);
                if (fingerprint)
                    await database.ExecuteAsync(Layout0501[Layout0501.IndexOf("CREATE TABLE mistake_image_fingerprint", StringComparison.Ordinal)..]);
            }
            else
            {
                await database.ExecuteAsync(Layout0501);
                if (!fingerprint) await database.ExecuteAsync("DROP TABLE mistake_image_fingerprint");
            }
            if (shape is "P0" or "P0f") await database.ExecuteAsync("DROP TABLE \"__EFMigrationsHistory\"");

            await database.ExecuteAsync("INSERT INTO process_log (id,mistake_id,action,details,created_at) SELECT gen_random_uuid(),id,a,'old-action-' || a,created_at FROM mistake CROSS JOIN (VALUES (2),(3),(4)) actions(a) WHERE status=2");
            if (shape.StartsWith("M-", StringComparison.Ordinal))
            {
                var (layout, row) = shape switch
                {
                    "M-da" => (ItemLayoutDa97cc6d, ItemRowDa97cc6d),
                    "M-05" => (ItemLayout05a5bf16, ItemRow05a5bf16),
                    _ => (ItemLayoutE0e56e2b, ItemRowE0e56e2b),
                };
                await database.ExecuteAsync(layout, row, row.Replace(Item3.ToString(), Item1.ToString()));
            }
            return;
        }
        switch (shape)
        {
            case "A":
                await database.ExecuteAsync(Layout0503, ConvertHead, ConvertWithoutType, ConvertTail);
                await database.ExecuteUntilFailureAsync(Renames0514);
                await database.ExecuteAsync(Era0612, StampInitial, Reanalyze);
                break;
            case "B":
                await database.ExecuteAsync(Layout0503, ConvertHead, ConvertKeepType, ConvertTail);
                await database.ExecuteUntilFailureAsync(Renames0514);
                await database.ExecuteAsync(StampInitial, Era0612, Era0620, Reanalyze);
                break;
            case "G":
                await database.ExecuteAsync(Layout0501, ConvertHead, ConvertWithoutType, ConvertTail);
                await database.ExecuteUntilFailureAsync(Renames0514);
                await database.ExecuteAsync(Era0612, StampInitial, Reanalyze);
                break;
            case "C" or "E":
                await using (var setup = database.CreateContext())
                    await setup.GetService<IMigrator>().MigrateAsync(Initial);
                if (shape == "C")
                    await database.ExecuteAsync(AddType);
                var type = shape == "C" ? ",type" : "";
                var typeValue = shape == "C" ? ",3" : "";
                await database.ExecuteAsync(
                    $"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,review_comment,created_at{type}) VALUES ('{Item4}','s1',1,7,'u4','[]',1,'cause',now(){typeValue})",
                    $"INSERT INTO mistake_practice (id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES ('{Guid.NewGuid()}','{Item4}','s1',now(),'[\"mistakes/s1/c1.jpg\",\"mistakes/s1/c1.jpg\"]',true),('{Guid.NewGuid()}','{Item4}','s1',now(),'not json secret-path',false)",
                    $"INSERT INTO process_log (id,mistake_item_id,action,created_at) VALUES ('{Guid.NewGuid()}','{Item4}',1,now())");
                await database.ExecuteAsync(Era0612, Era0620, Reanalyze);
                break;
            case "F1" or "F2" or "F3":
                var (layout, row) = shape switch
                {
                    "F1" => (ItemLayoutE0e56e2b, ItemRowE0e56e2b),
                    "F2" => (ItemLayoutDa97cc6d, ItemRowDa97cc6d),
                    _ => (ItemLayout05a5bf16, ItemRow05a5bf16),
                };
                await database.ExecuteAsync(Layout0503WithoutChildRows, layout, row, DropMixed);
                await database.ExecuteUntilFailureAsync(Renames0514);
                await database.ExecuteAsync(StampInitial, Era0612, Era0620, Reanalyze);
                break;
            case "Fs":
                await database.ExecuteAsync(Layout0503, ItemLayoutE0e56e2b, ItemRowE0e56e2b, DropMixed);
                Assert.Equal(1, await database.ExecuteUntilFailureAsync(Renames0514));
                break;
            case "Fs2":
                await database.ExecuteAsync(Layout0503, ItemLayoutE0e56e2b, ItemRowE0e56e2b, DropMixed);
                Assert.Equal(4, await database.ExecuteUntilFailureAsync(RenamesLate));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private sealed record PracticeRow(Guid Id, Guid Parent, string Paths);
    private sealed record LogRow(Guid Id, Guid Parent);
    private sealed record LegacyRows(HashSet<Guid> Items, List<PracticeRow> Practices, List<LogRow> Logs, HashSet<Guid> Details);
    private sealed record ArchiveRow(string Table, string Key, string Kind, JsonElement Payload);

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name;
        private readonly List<string> _roles = [];

        private IsolatedDatabase(string admin, string name)
        {
            _admin = admin;
            _name = name;
            ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(admin))
                throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to a dedicated PostgreSQL admin connection string.");
            var name = "mistake_legacy_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            return new IsolatedDatabase(admin, name);
        }

        public MistakeDbContext CreateContext(bool readOnly = false, string? applicationName = null, int? commandTimeout = null)
        {
            var connection = new NpgsqlConnectionStringBuilder(ConnectionString);
            if (readOnly) connection.Options = "-c default_transaction_read_only=on";
            if (applicationName != null) connection.ApplicationName = applicationName;
            if (commandTimeout != null) connection.CommandTimeout = commandTimeout.Value;
            return new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection.ConnectionString).Options);
        }

        public async Task<MistakeDbContext> CreateLimitedContextAsync()
        {
            var role = "mistake_limited_" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N");
            _roles.Add(role);
            await ExecuteAsync($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}'; " +
                $"GRANT CONNECT ON DATABASE \"{_name}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\"; " +
                $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{role}\"");
            var connection = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = password };
            return new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection.ConnectionString).Options);
        }

        public async Task ExecuteAsync(params string[] statements)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            foreach (var sql in statements)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>Runs statements one by one like the old builds did and returns how many succeeded.</summary>
        public async Task<int> ExecuteUntilFailureAsync(string[] statements)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            for (var index = 0; index < statements.Length; index++)
            {
                try
                {
                    await using var command = new NpgsqlCommand(statements[index], connection);
                    await command.ExecuteNonQueryAsync();
                }
                catch (PostgresException)
                {
                    return index;
                }
            }
            return statements.Length;
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public async Task PauseOnAsync(string tag, int seconds)
        {
            await ExecuteAsync("CREATE SCHEMA upgrade_guard",
                // Pauses once per transaction, at the first matching DDL command.
                $"CREATE FUNCTION upgrade_guard.pause_stage() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN IF TG_TAG = '{tag}' AND COALESCE(current_setting('upgrade_guard.paused', true), '') = '' THEN PERFORM set_config('upgrade_guard.paused', '1', true); PERFORM pg_sleep({seconds}); END IF; END $$",
                "CREATE EVENT TRIGGER pause_stage ON ddl_command_end EXECUTE FUNCTION upgrade_guard.pause_stage()");
        }

        public async Task WaitForPauseAsync(string applicationName, string queryPattern)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application_name AND wait_event = 'PgSleep' AND query LIKE @pattern)
                """, connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            command.Parameters.AddWithValue("pattern", queryPattern);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!timeout.IsCancellationRequested)
            {
                if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
                await Task.Delay(25, timeout.Token);
            }
            Assert.Fail("Upgrade did not reach the DDL pause.");
        }

        public async Task<LegacyRows> LegacyRowsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            var items = new HashSet<Guid>();
            await using (var command = new NpgsqlCommand("SELECT id FROM mistake_item", connection))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) items.Add(reader.GetGuid(0));
            var practices = new List<PracticeRow>();
            await using (var command = new NpgsqlCommand(
                "SELECT id, COALESCE(to_jsonb(t)->>'mistake_item_id', to_jsonb(t)->>'mistake_id')::uuid, image_paths::text FROM mistake_practice t", connection))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) practices.Add(new PracticeRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
            var logs = new List<LogRow>();
            await using (var command = new NpgsqlCommand(
                "SELECT id, COALESCE(to_jsonb(t)->>'mistake_item_id', to_jsonb(t)->>'mistake_id')::uuid FROM process_log t", connection))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) logs.Add(new LogRow(reader.GetGuid(0), reader.GetGuid(1)));
            var details = new HashSet<Guid>();
            await using (var command = new NpgsqlCommand(
                "SELECT id FROM mistake_detail WHERE to_regclass('public.mistake_detail') IS NOT NULL", connection))
            {
                try
                {
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) details.Add(reader.GetGuid(0));
                }
                catch (PostgresException error) when (error.SqlState == "42P01")
                {
                }
            }
            return new LegacyRows(items, practices, logs, details);
        }

        public async Task<List<ArchiveRow>> ArchiveAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT source_table, source_key, kind, payload::text FROM legacy_archive ORDER BY id", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<ArchiveRow>();
            while (await reader.ReadAsync())
                rows.Add(new ArchiveRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    JsonDocument.Parse(reader.GetString(3)).RootElement.Clone()));
            return rows;
        }

        /// <summary>Catalog plus every row of every public table.</summary>
        public async Task<string> SnapshotAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT COALESCE((SELECT string_agg(relname || ':' || relkind::text, ',' ORDER BY relname)
                    FROM pg_class WHERE relnamespace='public'::regnamespace),'') || '|' ||
                    COALESCE((SELECT string_agg(table_name || '.' || column_name || ':' || data_type || ':' || COALESCE(column_default,''), ',' ORDER BY table_name,column_name)
                    FROM information_schema.columns WHERE table_schema='public'),'') || '|' ||
                    COALESCE((SELECT string_agg(conname || ':' || pg_get_constraintdef(oid), ',' ORDER BY conname)
                    FROM pg_constraint WHERE connamespace='public'::regnamespace),'') || '|' ||
                    COALESCE((SELECT string_agg(indexname || ':' || indexdef, ',' ORDER BY indexname)
                    FROM pg_indexes WHERE schemaname='public'),'') || '|' ||
                    COALESCE((SELECT string_agg(proname, ',' ORDER BY proname) FROM pg_proc WHERE pronamespace='public'::regnamespace),'')
                """, connection);
            var parts = new List<string> { (string)(await command.ExecuteScalarAsync())! };
            command.CommandText = "SELECT relname FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r' ORDER BY relname";
            var tables = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            foreach (var table in tables)
            {
                command.CommandText = $"SELECT COALESCE(string_agg(to_jsonb(t)::text, ',' ORDER BY to_jsonb(t)::text), '') FROM public.\"{table}\" t";
                parts.Add(table + "=" + (string)(await command.ExecuteScalarAsync())!);
            }
            return string.Join('\n', parts);
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
