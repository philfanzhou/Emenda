using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

public sealed class ImageVariantSchemaPostgresTests
{
    [Fact]
    public async Task UpgradeQueuesEveryImageAMistakeOrTaskShows_ForAutomaticBackfill()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ExtractionDeletionSchema.Migration);
        var jobId = Guid.NewGuid();
        var source = new ExtractionSource(Guid.NewGuid(), $"mistakes/extractions/{jobId:N}/originals/page.png", "image/png", 4, "sha", 20, 10, null);
        var draft = new ExtractionQuestionDraft { Id = Guid.NewGuid(), Subject = 2, Statement = "Problem", StudentAnswer = "3", ReferenceAnswer = "2",
            Outcome = "incorrect", Evidence = "Visible 3 differs from 2", Regions = [new(source.Id, 0, 0, 1, 1)] };
        var crop = new ExtractionCrop($"mistakes/extractions/{jobId:N}/versions/current/crop.png", 20, 10, source.Id, draft.Regions[0], 4, "sha");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var itemId = Guid.NewGuid();
        db.ExtractionJobs.Add(new() { Id = jobId, Caller = "caller:school", StudentId = "s", SourceKey = "worksheet", PayloadHash = "hash",
            Version = 1, Status = "Succeeded", SourcesJson = JsonSerializer.Serialize(new[] { source }, json), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.ExtractionRevisions.Add(new() { JobId = jobId, Version = 1, RequestKey = Guid.NewGuid(), PayloadHash = "receipt",
            SnapshotJson = JsonSerializer.Serialize(new ExtractionSnapshot([new(draft, itemId, false, [crop])]), json),
            Actor = "teacher", Reason = "initial", Model = "model", CreatedAt = DateTimeOffset.UtcNow });
        db.ExtractionObjects.Add(new ExtractionObjectEntity { Path = crop.Path, JobId = jobId, Pending = false, Legacy = false, NextAttempt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        const string legacy = "mistakes/2025/06/14/def/image.jpg", conversation = "mistakes/conversation/abc/def/question.png";
        db.MistakeItems.AddRange(
            new() { Id = itemId, StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = $"extract:{jobId:N}", QuestionId = draft.Id.ToString(),
                SourceRegions = [new() { SourceImagePath = crop.Path }], ReviewStatus = 2 },
            new() { Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = "legacy", QuestionId = "q",
                SourceRegions = [new() { SourceImagePath = legacy }, new() { SourceImagePath = "mistakes/2025/06/14/def/image_small.jpg" }], ReviewStatus = 2 },
            new() { Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = "conversation", QuestionId = "c",
                SourceRegions = [new() { SourceImagePath = conversation }], ReviewStatus = 1 });
        await db.SaveChangesAsync();

        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(22, upgraded.FromVersion);
        Assert.Equal(25, upgraded.ToVersion);
        var queued = await db.Database.SqlQuery<string>($"SELECT path AS \"Value\" FROM image_variant_task ORDER BY path COLLATE \"C\"").ToListAsync();
        Assert.Equal(new[] { legacy, conversation, source.Path, crop.Path }.Order(StringComparer.Ordinal), queued);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
    }
}
