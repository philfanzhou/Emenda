using Emenda.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

/// <summary>
/// Schema v27 drops mistake_analysis and reanalyze_job.analysis_input, which nothing has used since schema v19. It
/// refuses to run while either holds data, and like v26 it is forward-only.
/// </summary>
public sealed class IdleAnalysisRemovalPostgresTests
{
    [Fact]
    public async Task UpgradeDropsOnlyTheIdleAnalysis()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(UnifiedSourceContractionSchema.Migration);
        var item = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at,source_kind,content)
            VALUES ({item},'s',2,3,'legacy','[]'::jsonb,1,'kept',now(),'legacy',jsonb_build_object('version',1,'images','[]'::jsonb))
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reanalyze_job(id,item_id,reviewer_id,student_id,status,created_at) VALUES ({Guid.NewGuid()},{item},'t','s',3,now())
            """);
        var reanalyzeColumns = await ColumnsAsync(db, "reanalyze_job");

        await db.GetService<IMigrator>().MigrateAsync(IdleAnalysisRemovalSchema.Migration);
        Assert.False(await AnalysisTableAsync(db));
        Assert.Equal(reanalyzeColumns.Where(c => c != "analysis_input"), await ColumnsAsync(db, "reanalyze_job"));
        Assert.Equal("kept", (await db.MistakeItems.AsNoTracking().SingleAsync()).RootCause);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM reanalyze_job").SingleAsync());
        // The exact classifier accepts the v27 shape and upgrades it on to the target.
        Assert.Equal(27, (await DatabaseInitializer.UpgradeToTargetAsync(db)).FromVersion);
    }

    [Fact]
    public async Task DowngradeIsRefusedWithoutChangingTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(IdleAnalysisRemovalSchema.Migration);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(UnifiedSourceContractionSchema.Migration));
        Assert.Contains("idle_mistake_analysis_irreversible", exception.ToString());
        Assert.False(await AnalysisTableAsync(db));
        Assert.Equal(27, (await DatabaseInitializer.UpgradeToTargetAsync(db)).FromVersion);
    }

    [Theory]
    [InlineData("INSERT INTO mistake_analysis(mistake_item_id,revision,teacher_feedback) VALUES ({0},1,'[]'::jsonb)")]
    [InlineData("INSERT INTO reanalyze_job(id,item_id,reviewer_id,student_id,status,created_at,analysis_input) VALUES (gen_random_uuid(),{0},'t','s',3,now(),'null'::jsonb)")]
    public async Task AnyRemainingAnalysisDataStopsTheUpgradeWithoutChanges(string insert)
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(UnifiedSourceContractionSchema.Migration);
        var item = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at,source_kind,content)
            VALUES ({item},'s',2,3,'legacy','[]'::jsonb,1,now(),'legacy',jsonb_build_object('version',1,'images','[]'::jsonb))
            """);
        // A JSON null is still data in the column: the guard counts every non-NULL analysis_input.
        await db.Database.ExecuteSqlRawAsync(insert, item);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(db));
        Assert.Contains("idle_mistake_analysis_present", failure.Message);
        Assert.True(await AnalysisTableAsync(db));
        Assert.Contains("analysis_input", await ColumnsAsync(db, "reanalyze_job"));
        Assert.DoesNotContain(IdleAnalysisRemovalSchema.Migration, await db.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<bool> AnalysisTableAsync(MistakeDbContext db) => await db.Database
        .SqlQuery<bool>($"SELECT to_regclass('public.mistake_analysis') IS NOT NULL AS \"Value\"").SingleAsync();

    private static Task<List<string>> ColumnsAsync(MistakeDbContext db, string table) => db.Database.SqlQuery<string>($"""
        SELECT column_name::text AS "Value" FROM information_schema.columns
        WHERE table_schema='public' AND table_name={table} ORDER BY ordinal_position
        """).ToListAsync();
}
