using System.Text.Json;
using Emenda.Database;
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
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_job(id,caller,student_id,grade,source_key,payload_hash,version,status,sources_json,candidate_json,created_at,updated_at)
            VALUES ({jobId}, 'caller:school', 's', 4, 'worksheet', 'hash', 2, 'Succeeded', {sources}::jsonb,
                {JsonSerializer.Serialize(draft, json)}::jsonb, now(), now())
            """);
        var itemSource = $"extract:{jobId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id, student_id, subject, grade, source_upload_id, question_id, source_regions, review_status, created_at)
            VALUES ({itemId}, 's', 2, 4, {itemSource}, {questionId.ToString()}, jsonb_build_array(jsonb_build_object('sourceImagePath', {currentCrop.Path})), 2, now())
            """);
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
        Assert.Equal(24, upgraded.ToVersion);
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
        using var analysis = JsonDocument.Parse((await db.MistakeItems.SingleAsync()).AnalysisSnapshot!);
        Assert.Equal(2, analysis.RootElement.GetProperty("sourceVersion").GetInt32());
        Assert.Equal("Visible 3 differs from 2", analysis.RootElement.GetProperty("evidence").GetString());
        Assert.True((await db.ExtractionObjects.SingleAsync(o => o.Path == oldCrop.Path)).Legacy);
        Assert.True((await db.ExtractionObjects.SingleAsync(o => o.Path == oldCrop.Path)).Pending);
        Assert.False((await db.ExtractionObjects.SingleAsync(o => o.Path == currentCrop.Path)).Pending);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Version20BackfillUsesCurrentRecoverableAnalysisWithoutOverwritingPopulatedRows()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ExtractionCorrectionsSchema.Migration);
        var jobId = Guid.NewGuid();
        var jobSource = $"extract:{jobId:N}";
        var orphanSource = $"extract:{Guid.NewGuid():N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_job(id,caller,student_id,grade,source_key,payload_hash,version,status,created_at,updated_at)
            VALUES ({jobId}, 'caller:school', 's', 4, 'backfill', 'hash', 3, 'Succeeded', now(), now())
            """);
        var recoverable = (Id: Guid.NewGuid(), QuestionId: Guid.NewGuid());
        var orphan = (Id: Guid.NewGuid(), QuestionId: Guid.NewGuid());
        var incomplete = (Id: Guid.NewGuid(), QuestionId: Guid.NewGuid());
        var inactive = (Id: Guid.NewGuid(), QuestionId: Guid.NewGuid());
        foreach (var item in new[] { recoverable, orphan, incomplete, inactive })
        {
            var source = item == orphan ? orphanSource : jobSource;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO mistake_item(id, student_id, subject, grade, source_upload_id, question_id, source_regions, review_status, root_cause, created_at)
                VALUES ({item.Id}, 's', 2, 4, {source}, {item.QuestionId.ToString()}, '[]'::jsonb, 2, 'Kept reviewer note', now())
                """);
            if (item != orphan) db.ExtractionQuestions.Add(new() { Id = item.QuestionId, JobId = jobId, ItemId = item.Id, Active = item != inactive });
        }
        var snapshot = $$$"""
            {"questions":[
              {"itemId":"{{{recoverable.Id}}}","question":{"id":"{{{recoverable.QuestionId}}}","outcome":"incorrect","statement":"6 × 4","studentAnswer":"20","referenceAnswer":"24","explanation":"","evidence":"20 is 4 short"}},
              {"itemId":"{{{incomplete.Id}}}","question":{"id":"{{{incomplete.QuestionId}}}","outcome":"incorrect","statement":"1 + 1","studentAnswer":"3","referenceAnswer":"2","explanation":""}},
              {"itemId":"{{{inactive.Id}}}","question":{"id":"{{{inactive.QuestionId}}}","outcome":"incorrect","statement":"6 × 4","studentAnswer":"20","referenceAnswer":"24","explanation":"","evidence":"20 is 4 short"}}
            ]}
            """;
        db.ExtractionRevisions.Add(new() { JobId = jobId, Version = 3, RequestKey = Guid.NewGuid(), PayloadHash = "receipt",
            SnapshotJson = snapshot, Actor = "teacher", Reason = "supplement", Model = "supplement_upload", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(20, upgraded.FromVersion);
        Assert.Equal(24, upgraded.ToVersion);
        var items = await db.MistakeItems.AsNoTracking().ToDictionaryAsync(item => item.Id);
        var recovered = items[recoverable.Id];
        Assert.Equal(jobSource, recovered.SourceUploadId);
        Assert.Equal(recoverable.QuestionId.ToString(), recovered.QuestionId);
        Assert.Equal("Kept reviewer note", recovered.RootCause);
        Assert.Equal(2, recovered.ReviewStatus);
        var expected = """{"statement":"6 × 4","studentAnswer":"20","referenceAnswer":"24","explanation":"","evidence":"20 is 4 short","sourceVersion":3}""";
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), JsonSerializer.Deserialize<JsonElement>(recovered.AnalysisSnapshot!)));
        Assert.All(new[] { orphan.Id, incomplete.Id, inactive.Id }, id => Assert.Null(items[id].AnalysisSnapshot));

        var newer = """{"statement":"6 × 4","studentAnswer":"21","referenceAnswer":"24","explanation":"","evidence":"21 is 3 short","sourceVersion":4}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET analysis_snapshot={newer}::jsonb WHERE id={recoverable.Id}");
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(WorksheetAnalysisSchema.Backfill));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(WorksheetAnalysisSchema.Backfill));
        var persisted = await db.MistakeItems.Where(item => item.Id == recoverable.Id).Select(item => item.AnalysisSnapshot).SingleAsync();
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(newer), JsonSerializer.Deserialize<JsonElement>(persisted!)));
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
    }
}
