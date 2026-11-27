using Emenda.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Emenda.Service.Tests;

/// <summary>
/// Schema v28 drops reanalyze_job and product_intake_request, unused since schema v19. Every remaining row is copied
/// unchanged into legacy_archive first, so nothing is lost and no older database is refused; v28 is forward-only.
/// </summary>
public sealed class RetiredIntakeTablesPostgresTests
{
    [Fact]
    public async Task UpgradeArchivesEveryRowUnchangedAndDropsBothTables()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(IdleAnalysisRemovalSchema.Migration);
        var item = await InsertItemAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reanalyze_job(id,item_id,reviewer_id,student_id,teacher_description,status,error_message,created_at,completed_at,attempt,fencing_version)
            VALUES ({Guid.NewGuid()},{item},'teacher','s','retry details',4,'upgrade_interrupted',now(),now(),2,3),
                   ({Guid.NewGuid()},{item},'teacher','s',NULL,1,NULL,now(),NULL,0,0)
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO product_intake_request(request_id,caller,student_id,payload_hash,response,created_at)
            VALUES ('req-1','portal','s',repeat('a',64),jsonb_build_object('itemId','x','status','accepted'),now()),
                   ('req-2','portal','s',repeat('b',64),NULL,now())
            """);
        var jobs = await RowsAsync(db, "SELECT to_jsonb(t)::text AS \"Value\" FROM reanalyze_job t ORDER BY id");
        var receipts = await RowsAsync(db, "SELECT to_jsonb(t)::text AS \"Value\" FROM product_intake_request t ORDER BY request_id");
        var items = await RowsAsync(db, "SELECT to_jsonb(t)::text AS \"Value\" FROM mistake_item t ORDER BY id");

        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(27, upgraded.FromVersion);
        Assert.Equal(28, upgraded.ToVersion);
        Assert.False(await TableAsync(db, "reanalyze_job"));
        Assert.False(await TableAsync(db, "product_intake_request"));
        Assert.Equal(jobs, await ArchivedAsync(db, "reanalyze_job", RetiredIntakeTablesSchema.ReanalyzeReason));
        Assert.Equal(receipts, await ArchivedAsync(db, "product_intake_request", RetiredIntakeTablesSchema.IntakeReason));
        Assert.Equal(items, await RowsAsync(db, "SELECT to_jsonb(t)::text AS \"Value\" FROM mistake_item t ORDER BY id"));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);

        // The archive keeps them: it stays append-only.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM legacy_archive WHERE source_table='reanalyze_job'"));
        Assert.Contains("legacy_archive_append_only", refused.MessageText);
    }

    [Fact]
    public async Task EmptyTablesAreDroppedWithoutArchiving()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(IdleAnalysisRemovalSchema.Migration);
        await InsertItemAsync(db);

        Assert.Equal(27, (await DatabaseInitializer.UpgradeToTargetAsync(db)).FromVersion);
        Assert.False(await TableAsync(db, "reanalyze_job"));
        Assert.False(await TableAsync(db, "product_intake_request"));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM legacy_archive").SingleAsync());
    }

    [Fact]
    public async Task DowngradeIsRefusedWithoutChangingTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(IdleAnalysisRemovalSchema.Migration));
        Assert.Contains("retired_intake_tables_irreversible", exception.ToString());
        Assert.False(await TableAsync(db, "reanalyze_job"));
        Assert.False(await TableAsync(db, "product_intake_request"));
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
    }

    private static async Task<Guid> InsertItemAsync(MistakeDbContext db)
    {
        var item = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at,source_kind,content)
            VALUES ({item},'s',2,3,'legacy','[]'::jsonb,1,'kept',now(),'legacy',jsonb_build_object('version',1,'images','[]'::jsonb))
            """);
        return item;
    }

    private static Task<List<string>> RowsAsync(MistakeDbContext db, string sql) =>
        db.Database.SqlQueryRaw<string>(sql).ToListAsync();

    private static Task<List<string>> ArchivedAsync(MistakeDbContext db, string table, string reason) => db.Database.SqlQuery<string>($"""
        SELECT payload::text AS "Value" FROM legacy_archive
        WHERE source_table={table} AND kind='row' AND reason={reason} ORDER BY source_key
        """).ToListAsync();

    private static async Task<bool> TableAsync(MistakeDbContext db, string table) => await db.Database
        .SqlQuery<bool>($"SELECT to_regclass('public.' || {table}) IS NOT NULL AS \"Value\"").SingleAsync();
}
