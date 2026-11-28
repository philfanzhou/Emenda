using Emenda.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

public sealed class PracticeRemovalPostgresTests
{
    [Fact]
    public async Task UpgradePreservesConfirmedOriginalAnswerAndEvidence()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(RetiredIntakeTablesSchema.Migration);
        const string content = """{"version":1,"statement":"Find the length","studentAnswer":"12","referenceAnswer":"12 cm","evidence":"Missing unit","images":[]}""";
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id, student_id, subject, grade, source_upload_id, source_regions,
                review_status, root_cause, created_at, source_kind, content)
            VALUES ({id}, 'student', 2, 3, 'legacy', '[]'::jsonb, 2, 'Missing unit', now(), 'legacy', {content}::jsonb)
            """);

        var upgrade = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(28, upgrade.FromVersion);
        Assert.Equal(30, upgrade.ToVersion);
        Assert.True(await db.Database.SqlQuery<bool>($"""SELECT to_regclass('public.mistake_practice') IS NULL AS "Value" """).SingleAsync());
        var item = await db.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == id);
        Assert.Equal(2, item.ReviewStatus);
        Assert.Equal("Missing unit", item.RootCause);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(content), System.Text.Json.Nodes.JsonNode.Parse(item.Content)));
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
    }
}
