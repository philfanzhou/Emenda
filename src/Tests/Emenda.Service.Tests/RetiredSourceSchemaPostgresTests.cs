using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Emenda.Database;

namespace Emenda.Service.Tests;

/// <summary>Schema v19 removes the managed source structures without touching mistake data.</summary>
public sealed class RetiredSourceSchemaPostgresTests
{
    private static readonly string[] RetiredTables =
    [
        "source_intake", "source_intake_group", "source_intake_request", "source_target_object", "source_target_attempt",
        "source_return", "source_return_object", "source_return_attempt", "source_handoff",
        "legacy_return", "legacy_return_object", "legacy_return_attempt",
        "layout_job", "layout_job_image", "layout_request", "layout_result", "layout_result_region"
    ];

    [Fact]
    public async Task Version18UpgradeKeepsMistakeDataAndDropsOnlyRetiredStructuresAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ConversationMistakeSchema.Migration);
        var itemId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at)
            VALUES ({itemId},'s1',2,7,'extract:job','[]'::jsonb,2,'retained cause',now())
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO process_log(id,mistake_item_id,student_id,action,details,created_at) VALUES ({Guid.NewGuid()},{itemId},'s1',1,'created',now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_analysis(mistake_item_id,revision,teacher_feedback,input_digest) VALUES ({itemId},3,'[]'::jsonb,'digest')");

        var result = await DatabaseInitializer.UpgradeToTargetAsync(db);

        Assert.Equal(18, result.FromVersion);
        Assert.Equal(25, result.ToVersion);
        Assert.Equal("retained cause", (await db.MistakeItems.AsNoTracking().SingleAsync()).RootCause);
        Assert.Equal(1, await Scalar<long>(database, "SELECT count(*) FROM process_log"));
        Assert.Equal(3L, await Scalar<long>(database, "SELECT revision FROM mistake_analysis"));
        foreach (var table in RetiredTables)
            Assert.False(await Scalar<bool>(database, $"SELECT to_regclass('public.{table}') IS NOT NULL"), table);
        Assert.Equal("derive_mistake_item_source,guard_extraction_deletion_receipt,guard_extraction_object_reference,guard_extraction_retention,guard_extraction_revision,guard_legacy_archive",
            await Scalar<string>(database, "SELECT string_agg(proname, ',' ORDER BY proname) FROM pg_proc WHERE pronamespace='public'::regnamespace"));
        Assert.Equal(3L, await Scalar<long>(database, """
            SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
            WHERE NOT t.tgisinternal AND c.relname IN ('mistake_item','mistake_practice','process_log','reanalyze_job')
            """));
        // Core tables stay writable once the managed-source guards are gone.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET review_status=3, updated_at=now() WHERE id={itemId}");
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task PopulatedRetiredTableRefusesTheUpgradeAndLeavesVersion18UnchangedAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ConversationMistakeSchema.Migration);
        // The v18 guards forbid hand-written managed rows; bypass them to model leftover platform data.
        await Execute(database, """
            SET session_replication_role = replica;
            INSERT INTO source_intake VALUES (gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'s1','{}','manual',gen_random_uuid(),0,NULL,false);
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(db));

        Assert.Contains("version 18", error.Message);
        Assert.Contains("rolls back", error.Message);
        Assert.Equal(1L, await Scalar<long>(database, "SELECT count(*) FROM source_intake"));
        Assert.DoesNotContain(RetiredSourceSchema.Migration, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Version19IsForwardOnlyAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(RetiredSourceSchema.Migration);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync(ConversationMistakeSchema.Migration));
        Assert.Contains("retired_source_schema_irreversible", error.MessageText);
        Assert.Contains(RetiredSourceSchema.Migration, await db.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<T> Scalar<T>(PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task Execute(PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
