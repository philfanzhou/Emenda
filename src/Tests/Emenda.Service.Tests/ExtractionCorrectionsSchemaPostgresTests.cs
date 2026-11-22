using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

public sealed class ExtractionCorrectionsSchemaPostgresTests
{
    [Fact]
    public async Task UpgradeRetiresGeometryButPreservesOriginalsReceiptsItemsAndPractice()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(RetiredSourceSchema.Migration);
        var jobId = Guid.NewGuid();
        var source = new ExtractionSource(Guid.NewGuid(), $"mistakes/extractions/{jobId:N}/originals/keep.png", "image/png", 4, "sha", 20, 10, null);
        var questionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var draft = new ExtractionQuestionDraft { Id = questionId, Subject = 2, Statement = "Problem", StudentAnswer = "3", ReferenceAnswer = "2",
            Outcome = "incorrect", Evidence = "Visible 3 differs from 2", Regions = [new(source.Id, 0, 0, 1, 1)] };
        var oldCrop = new ExtractionCrop($"mistakes/extractions/{jobId:N}/versions/old/crop.png", 20, 10, source.Id, draft.Regions[0], 4, "sha");
        var currentCrop = oldCrop with { Path = $"mistakes/extractions/{jobId:N}/versions/current/crop.png", Region = draft.Regions[0] with { X1 = .5 } };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var sources = JsonSerializer.Serialize(new[] { source }, json);
        db.ExtractionJobs.Add(new() { Id = jobId, Caller = "caller:school", StudentId = "s", Grade = 4, SourceKey = "worksheet", PayloadHash = "hash", Version = 2,
            Status = "Succeeded", SourcesJson = sources, CandidateJson = JsonSerializer.Serialize(draft, json), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.MistakeItems.Add(new() { Id = itemId, StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = $"extract:{jobId:N}", QuestionId = questionId.ToString(),
            SourceRegions = [new() { SourceImagePath = currentCrop.Path }], ReviewStatus = 2 });
        db.ExtractionQuestions.Add(new() { Id = questionId, JobId = jobId, ItemId = itemId, Active = true });
        for (var version = 1; version <= 2; version++)
            db.ExtractionRevisions.Add(new() { JobId = jobId, Version = version, RequestKey = Guid.NewGuid(), PayloadHash = "receipt" + version,
                SnapshotJson = JsonSerializer.Serialize(new ExtractionSnapshot([new(draft, itemId, false, [version == 1 ? oldCrop : currentCrop]),
                    new(draft with { Id = Guid.NewGuid(), Outcome = "correct" }, null, false, [oldCrop])]), json), Actor = "teacher", Reason = "correction", Model = "model", CreatedAt = DateTimeOffset.UtcNow });
        db.ProcessLogs.Add(new() { Id = Guid.NewGuid(), MistakeItemId = itemId, Action = 2,
            Details = JsonSerializer.Serialize(new { previous = new { sourceRegions = new[] { oldCrop.Path }, reviewStatus = 2 } }), CreatedAt = DateTimeOffset.UtcNow });
        db.MistakePractices.Add(new() { Id = Guid.NewGuid(), MistakeItemId = itemId, StudentId = "s", ImagePaths = [] });
        await db.SaveChangesAsync();
        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(19, upgraded.FromVersion);
        Assert.Equal(20, upgraded.ToVersion);
        db.ChangeTracker.Clear();
        Assert.Equal(source, JsonSerializer.Deserialize<List<ExtractionSource>>((await db.ExtractionJobs.SingleAsync()).SourcesJson, json)!.Single());
        Assert.Null((await db.ExtractionJobs.SingleAsync()).CandidateJson);
        Assert.Equal(2, await db.ExtractionRevisions.CountAsync());
        Assert.DoesNotContain(oldCrop.Path, (await db.ExtractionRevisions.SingleAsync(r => r.Version == 1)).SnapshotJson);
        var current = JsonSerializer.Deserialize<ExtractionSnapshot>((await db.ExtractionRevisions.SingleAsync(r => r.Version == 2)).SnapshotJson, json)!;
        Assert.Single(current.Questions[0].Images);
        Assert.Empty(current.Questions[1].Images);
        Assert.DoesNotContain(oldCrop.Path, (await db.ProcessLogs.SingleAsync()).Details!);
        Assert.Equal(1, await db.MistakePractices.CountAsync());
        Assert.Equal(2, (await db.MistakeItems.SingleAsync()).ReviewStatus);
        Assert.True((await db.ExtractionObjects.SingleAsync(o => o.Path == oldCrop.Path)).Legacy);
        Assert.True((await db.ExtractionObjects.SingleAsync(o => o.Path == oldCrop.Path)).Pending);
        Assert.False((await db.ExtractionObjects.SingleAsync(o => o.Path == currentCrop.Path)).Pending);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
