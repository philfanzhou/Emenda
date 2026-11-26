using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

public sealed class UnifiedMistakeSchemaPostgresTests
{
    private const string Analysis = """
        {"statement":"2+2=?","studentAnswer":"5","referenceAnswer":"4","explanation":"Add","evidence":"Wrote 5","sourceVersion":3}
        """;

    [Fact]
    public async Task UpgradeDerivesOneShapeForEverySource()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ImageVariantSchema.Migration);
        var liveJob = Guid.NewGuid();
        var deletedJob = Guid.NewGuid();
        AddJob(db, liveJob, "caller:school");
        db.ExtractionDeletions.Add(new()
        {
            JobId = deletedJob, AdmissionHash = new string('a', 64), SourceBindingHash = new string('b', 64),
            OwnerHash = ExtractionCallerRecoveryPostgresTests.OwnerHash("school"),
            DeletedAt = DateTimeOffset.UtcNow, RetainedConfirmed = 1
        });
        await db.SaveChangesAsync();

        var crop = $"mistakes/extractions/{liveJob:N}/retained/crop.png";
        var extraction = Item($"extract:{liveJob:N}", crop);
        extraction.QuestionId = Guid.NewGuid().ToString();
        extraction.AnalysisSnapshot = Analysis;
        var retainedCrop = $"mistakes/extractions/{deletedJob:N}/retained/crop.png";
        var retained = Item($"extract:{deletedJob:N}", retainedCrop);
        retained.AnalysisSnapshot = Analysis.Replace("\"sourceVersion\":3", $$"""
            "sourceVersion":2,"retainedFiles":[{"path":"{{retainedCrop}}","contentType":"image/png","length":4,"sha256":"{{new string('c', 64)}}"}]
            """.Trim());
        var (conversation, _) = Conversation("school");
        var legacy = Item("0f4c1a2e-legacy", "mistakes/2026/06/08/upload_0.jpg");
        legacy.SourceRegions[0].BoundingBox = new() { X1 = 1, Y1 = 2, X2 = 30, Y2 = 40 };
        db.MistakeItems.AddRange(extraction, retained, conversation, legacy);
        await db.SaveChangesAsync();
        var updatedAt = await db.MistakeItems.AsNoTracking().OrderBy(i => i.Id).Select(i => i.UpdatedAt).ToListAsync();
        // The receipt is in the application's format: the caller scope already sees the retained row through it.
        await using (var scoped = database.CreateContext())
        {
            scoped.ConversationCallerScope = "school";
            Assert.True(await scoped.MistakeItems.AnyAsync(i => i.Id == retained.Id));
        }

        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(23, upgraded.FromVersion);
        Assert.Equal(25, upgraded.ToVersion);
        Assert.Equal(updatedAt, await db.MistakeItems.AsNoTracking().OrderBy(i => i.Id).Select(i => i.UpdatedAt).ToListAsync());

        using (var row = await ReadAsync(db, extraction.Id))
        {
            var root = row.RootElement;
            Assert.Equal("extraction", root.GetProperty("kind").GetString());
            Assert.Equal("school", root.GetProperty("caller").GetString());
            var content = root.GetProperty("content");
            Assert.Equal(1, content.GetProperty("version").GetInt32());
            Assert.Equal("2+2=?", content.GetProperty("statement").GetString());
            Assert.Equal("5", content.GetProperty("studentAnswer").GetString());
            Assert.Equal("4", content.GetProperty("referenceAnswer").GetString());
            Assert.Equal("Add", content.GetProperty("explanation").GetString());
            Assert.Equal("Wrote 5", content.GetProperty("evidence").GetString());
            Assert.Equal(0, content.GetProperty("knowledgePoints").GetArrayLength());
            var image = Assert.Single(content.GetProperty("images").EnumerateArray());
            Assert.Equal(crop, image.GetProperty("path").GetString());
            Assert.Equal("question", image.GetProperty("role").GetString());
            Assert.Equal(JsonValueKind.Null, image.GetProperty("box").ValueKind);
            var detail = root.GetProperty("detail");
            Assert.Equal(liveJob, detail.GetProperty("jobId").GetGuid());
            Assert.Equal(extraction.QuestionId, detail.GetProperty("questionId").GetString());
            Assert.Equal(3, detail.GetProperty("sourceVersion").GetInt32());
            Assert.False(detail.TryGetProperty("retainedFiles", out _));
        }

        using (var row = await ReadAsync(db, retained.Id))
        {
            // The task is gone; v25 recovers its caller from the deletion receipt's owner hash.
            Assert.Equal("school", row.RootElement.GetProperty("caller").GetString());
            var file = Assert.Single(row.RootElement.GetProperty("detail").GetProperty("retainedFiles").EnumerateArray());
            Assert.Equal(retainedCrop, file.GetProperty("path").GetString());
            Assert.False(row.RootElement.GetProperty("detail").TryGetProperty("questionId", out _));
        }

        using (var row = await ReadAsync(db, conversation.Id))
        {
            var root = row.RootElement;
            Assert.Equal("conversation", root.GetProperty("kind").GetString());
            Assert.Equal("school", root.GetProperty("caller").GetString());
            // The evidence snapshot moves byte for byte, so conversation evidence hashes stay valid.
            Assert.True(await db.Database.SqlQuery<bool>(
                $"SELECT source_detail=conversation_source AS \"Value\" FROM mistake_item WHERE id={conversation.Id}").SingleAsync());
            var content = root.GetProperty("content");
            Assert.Equal("Solve x+1=3", content.GetProperty("statement").GetString());
            Assert.Equal("x=1", content.GetProperty("studentAnswer").GetString());
            Assert.Equal("x=2", content.GetProperty("referenceAnswer").GetString());
            Assert.Equal(JsonValueKind.Null, content.GetProperty("explanation").ValueKind);
            Assert.Equal("Subtracted wrongly", content.GetProperty("evidence").GetString());
            Assert.Equal(new[] { "Linear equations" }, content.GetProperty("knowledgePoints").EnumerateArray().Select(p => p.GetString()));
            var images = content.GetProperty("images").EnumerateArray().ToArray();
            Assert.Equal(new[] { "source", "question" }, images.Select(i => i.GetProperty("role").GetString()));
            Assert.Equal(JsonValueKind.Null, images[0].GetProperty("box").ValueKind);
            Assert.Equal(new[] { 10, 20, 300, 400 }, images[1].GetProperty("box").EnumerateArray().Select(v => v.GetInt32()));
        }

        using (var row = await ReadAsync(db, legacy.Id))
        {
            var root = row.RootElement;
            Assert.Equal("legacy", root.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("caller").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("detail").ValueKind);
            var content = root.GetProperty("content");
            Assert.Equal(JsonValueKind.Null, content.GetProperty("statement").ValueKind);
            var image = Assert.Single(content.GetProperty("images").EnumerateArray());
            Assert.Equal("question", image.GetProperty("role").GetString());
            Assert.Equal(new[] { 1, 2, 30, 40 }, image.GetProperty("box").EnumerateArray().Select(v => v.GetInt32()));
        }

        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task EveryWriterKeepsTheDerivedColumnsCurrent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var job = Guid.NewGuid();
        AddJob(db, job, "caller:school");
        await db.SaveChangesAsync();
        var item = Item($"extract:{job:N}", $"mistakes/extractions/{job:N}/retained/a.png");
        db.MistakeItems.Add(item);
        var (conversation, _) = Conversation("tutor");
        db.MistakeItems.Add(conversation);
        await db.SaveChangesAsync();

        using (var row = await ReadAsync(db, item.Id))
        {
            Assert.Equal("school", row.RootElement.GetProperty("caller").GetString());
            Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("content").GetProperty("statement").ValueKind);
        }
        using (var row = await ReadAsync(db, conversation.Id))
            Assert.Equal("tutor", row.RootElement.GetProperty("caller").GetString());

        // A correction rewrites the analysis and the crops; the derived shape follows in the same statement.
        item.AnalysisSnapshot = Analysis;
        item.SourceRegions = [new() { SourceImagePath = $"mistakes/extractions/{job:N}/retained/b.png" },
            new() { SourceImagePath = $"mistakes/extractions/{job:N}/retained/c.png" }];
        await db.SaveChangesAsync();
        using (var row = await ReadAsync(db, item.Id))
        {
            var content = row.RootElement.GetProperty("content");
            Assert.Equal("2+2=?", content.GetProperty("statement").GetString());
            Assert.Equal(new[] { "b.png", "c.png" }, content.GetProperty("images").EnumerateArray()
                .Select(i => Path.GetFileName(i.GetProperty("path").GetString())));
        }

        // Raw SQL writers are covered too, and a caller resolved earlier survives once its task is gone.
        var orphan = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at,source_caller)
            VALUES ({orphan},'s',2,4,{$"extract:{Guid.NewGuid():N}"},'[]'::jsonb,2,now(),'kept')
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET root_cause='reviewed' WHERE id={orphan}");
        using (var row = await ReadAsync(db, orphan))
        {
            Assert.Equal("extraction", row.RootElement.GetProperty("kind").GetString());
            Assert.Equal("kept", row.RootElement.GetProperty("caller").GetString());
        }

        // Derived columns cannot be set independently of the authoritative ones.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET source_kind='legacy', content='{{}}'::jsonb WHERE id={item.Id}");
        using (var row = await ReadAsync(db, item.Id))
        {
            Assert.Equal("extraction", row.RootElement.GetProperty("kind").GetString());
            Assert.Equal("2+2=?", row.RootElement.GetProperty("content").GetProperty("statement").GetString());
        }
    }

    [Fact]
    public async Task DowngradeIsRefusedWithoutChangingTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(UnifiedMistakeSchema.Migration);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(ImageVariantSchema.Migration));
        Assert.Contains("unified_mistake_source_irreversible", exception.ToString());
        Assert.Equal(24, (await DatabaseInitializer.UpgradeToTargetAsync(db)).FromVersion);
    }

    [Fact]
    public async Task ExactClassifierRejectsAMissingDerivationTrigger()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER trg_mistake_item_source ON mistake_item");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(db));
    }

    private static void AddJob(MistakeDbContext db, Guid id, string caller) => db.ExtractionJobs.Add(new()
    {
        Id = id, Caller = caller, StudentId = "s", SourceKey = "worksheet", PayloadHash = "hash", Version = 1,
        Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    });

    private static MistakeItemEntity Item(string source, params string[] paths) => new()
    {
        Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = source, ReviewStatus = 2,
        SourceRegions = paths.Select(p => new SourceRegionEntry { SourceImagePath = p }).ToList(),
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static (MistakeItemEntity Item, string Source) Conversation(string caller)
    {
        var asset = Guid.NewGuid();
        var original = $"mistakes/conversation/{caller}/student/original.jpg";
        var crop = $"mistakes/conversation/{caller}/student/crop.jpg";
        var source = $$"""
            {"version":1,"conversationId":"{{Guid.NewGuid()}}","questionId":"{{Guid.NewGuid()}}","position":4,
             "statement":"Solve x+1=3","studentAttempt":{"position":3,"quote":"x=1","assetId":null},"evidence":"Subtracted wrongly",
             "referenceAnswer":"x=2","knowledgePoints":["Linear equations"],"sources":[],"attempts":[],
             "images":[{"sourcePosition":3,"assetId":null,"path":"{{original}}","box":null},
                       {"sourcePosition":3,"assetId":"{{asset}}","path":"{{crop}}","box":[10,20,300,400]}]}
            """;
        var item = Item("", original, crop);
        item.ReviewStatus = 1;
        item.ConversationCaller = caller;
        item.ConversationActive = true;
        item.ConversationSource = source;
        return (item, source);
    }

    private static async Task<JsonDocument> ReadAsync(MistakeDbContext db, Guid id) =>
        JsonDocument.Parse(await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('kind',source_kind,'caller',source_caller,'content',content,'detail',source_detail)::text AS "Value"
            FROM mistake_item WHERE id={id}
            """).SingleAsync());
}
