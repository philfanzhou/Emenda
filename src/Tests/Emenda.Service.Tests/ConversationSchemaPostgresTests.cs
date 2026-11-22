using Emenda.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

public sealed class ConversationSchemaPostgresTests
{
    [Fact]
    public async Task Version17UpgradeRetainsStructuredAnalysisAndVerifiesExactSchema19()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(MistakeAnalysisSchema.Migration);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ({id},'existing',2,3,'legacy','[]'::jsonb,1,'Teacher retained',now())");
        var feedback = """["Teacher feedback"]""";
        var report = """{"summary":"Retained report"}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_analysis(mistake_item_id,revision,teacher_feedback,draft_report) VALUES ({id},7,{feedback}::jsonb,{report}::jsonb)");
        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(17, upgraded.FromVersion); Assert.Equal(19, upgraded.ToVersion);
        Assert.Equal("Teacher retained", (await db.MistakeItems.SingleAsync()).RootCause);
        Assert.Equal(7, (await db.MistakeAnalyses.SingleAsync()).Revision);
        Assert.Contains(MistakeAnalysisSchema.Migration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(ConversationMistakeSchema.Migration, await db.Database.GetAppliedMigrationsAsync());
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ExactClassifierRejectsMissingCallerScopeColumn()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversation_collection_job DROP COLUMN caller_name");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(db));
    }

    [Fact]
    public async Task DowngradeRefusesToDiscardDurableCollectionWork()
    {
        // v18 exercises the conversation migration's own guard; v19 is forward-only.
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ConversationMistakeSchema.Migration);
        db.ConversationCollections.Add(new() { Id = Guid.NewGuid(), CallerName = "platform", ActorId = "actor", StudentId = "opaque-learner", OwnerId = "owner", Grade = 3,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(MistakeAnalysisSchema.Migration));
        Assert.Single(await db.ConversationCollections.AsNoTracking().ToListAsync());
    }
}
