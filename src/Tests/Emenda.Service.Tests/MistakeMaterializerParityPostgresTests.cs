using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

/// <summary>
/// Since schema v26 the pipelines write a mistake's source through <see cref="MistakeSource"/> and
/// <see cref="MistakeMaterializer"/> completes the unified columns; before, trg_mistake_item_source derived them from
/// the source-specific columns. For the same source data both must store the same row. Each case is saved through EF
/// on a v26 database and inserted with its old columns into a v25 database, where the trigger derives the columns;
/// the stored kind, caller, content and source_detail are compared as jsonb.
/// </summary>
public sealed class MistakeMaterializerParityPostgresTests
{
    [Fact]
    public async Task WritePathStoresWhatTheV24DerivationStoredForEveryCase()
    {
        await using var current = await PostgresTestDatabase.CreateAsync();
        await using var previous = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = current.CreateContext();
        await using var old = previous.CreateContext();
        await old.GetService<IMigrator>().MigrateAsync(ExtractionCallerRecoverySchema.Migration);
        var live = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var unsaved = Guid.NewGuid();
        foreach (var context in new[] { db, old })
        {
            AddJob(context, live, "caller:school");
            AddJob(context, admin, "admin:");
            await context.SaveChangesAsync();
        }
        // The trigger looks the task up at insert time; the materializer resolves one added in the same save.
        AddJob(old, unsaved, "caller:fresh");
        await old.SaveChangesAsync();
        AddJob(db, unsaved, "caller:fresh");

        var cases = new List<(PreUnifiedRow Row, string? Caller)>
        {
            (new(Item($"extract:{live:N}", [$"mistakes/extractions/{live:N}/retained/a.png", $"mistakes/extractions/{live:N}/retained/b.png"],
                    i => i.QuestionId = Guid.NewGuid().ToString()),
                """{"statement":"2+2=?","studentAnswer":"5","referenceAnswer":"4","explanation":"Add","evidence":"Wrote 5","sourceVersion":3}"""), null),
            // JSON nulls, a retained-file manifest and a preset caller kept because the task is gone.
            (new(Item($"extract:{deleted:N}", [$"mistakes/extractions/{deleted:N}/retained/c.png"], _ => { }),
                $$"""{"statement":null,"studentAnswer":"7","referenceAnswer":null,"explanation":"","evidence":"e","sourceVersion":2,"retainedFiles":[{"path":"mistakes/extractions/{{deleted:N}}/retained/c.png","contentType":"image/png","length":4,"sha256":"{{new string('c', 64)}}","note":null}]}"""),
                "kept"),
            (new(Item($"extract:{admin:N}", [], _ => { }), """{"statement":"admin"}"""), null),
            (new(Item($"extract:{unsaved:N}", [$"mistakes/extractions/{unsaved:N}/retained/d.png"], _ => { })), null),
            (new(Item("extract:NOT-A-JOB", ["mistakes/extractions/x/retained/e.png"], i => i.QuestionId = "q")), null),
            (Conversation("school", """
                {"version":1,"statement":"Solve x+1=3","studentAttempt":{"position":3,"quote":"x=1","assetId":null},"evidence":"Subtracted",
                 "referenceAnswer":"x=2","knowledgePoints":["Linear equations","Signs"],
                 "images":[{"sourcePosition":3,"assetId":null,"path":"mistakes/conversation/o.jpg","box":null},
                           {"sourcePosition":3,"assetId":"11111111-1111-4111-8111-111111111111","path":"mistakes/conversation/c.jpg","box":[10,20,300,400]},
                           {"sourcePosition":4,"assetId":"22222222-2222-4222-8222-222222222222","path":"mistakes/conversation/n.jpg","box":"bad"}]}
                """, "mistakes/conversation/o.jpg", "mistakes/conversation/c.jpg", "mistakes/conversation/n.jpg", "mistakes/conversation/missing.jpg"), null),
            (Conversation("tutor", """{"statement":null,"studentAttempt":null,"knowledgePoints":"none","images":"none","referenceAnswer":5}""",
                "mistakes/conversation/x.jpg"), null),
            (Conversation(null, "[]", "mistakes/conversation/y.jpg"), null),
            (new(Item("upload-1", ["mistakes/2026/06/08/upload_0.jpg", "mistakes/2026/06/08/upload_1.jpg"],
                i => i.SourceRegions[0].BoundingBox = new() { X1 = 1, Y1 = 2, X2 = 30, Y2 = 40 })), null),
            (new(Item("", [], _ => { })), null),
            // A legacy row ignores an analysis on both paths.
            (new(Item("legacy-2", [], _ => { }), """{"statement":"ignored for legacy","sourceVersion":1}"""), null)
        };
        var items = cases.Select(c => Entity(c.Row, c.Caller)).ToList();
        db.MistakeItems.AddRange(items);
        await db.SaveChangesAsync();
        foreach (var (row, caller) in cases) await PreUnifiedRows.InsertWithCallerAsync(old, row, caller);
        foreach (var item in items) await AssertSameAsync(db, old, item);

        // Updates on both paths: a correction, a conversation merge and a switch from legacy to extraction.
        const string corrected = """{"statement":"3+3=?","studentAnswer":"7","referenceAnswer":"6","explanation":null,"evidence":"e","sourceVersion":4}""";
        var correctedRegion = $"mistakes/extractions/{live:N}/retained/z.png";
        MistakeSource.SetAnalysis(items[0], corrected);
        items[0].SourceRegions = [new() { SourceImagePath = correctedRegion }];
        var merged = cases[5].Row.ConversationSource!.Replace("Signs", "Inverse operations");
        MistakeSource.SetConversation(items[5], items[5].SourceCaller, merged);
        items[9].SourceUploadId = $"extract:{live:N}";
        await db.SaveChangesAsync();
        await old.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE mistake_item SET analysis_snapshot={corrected}::jsonb,
                source_regions=jsonb_build_array(jsonb_build_object('sourceImagePath', {correctedRegion}, 'boundingBox', NULL)) WHERE id={items[0].Id}
            """);
        await old.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET conversation_source={merged}::jsonb WHERE id={items[5].Id}");
        await old.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET source_upload_id={items[9].SourceUploadId} WHERE id={items[9].Id}");
        foreach (var item in items) await AssertSameAsync(db, old, item);
        Assert.Equal("school", items[9].SourceCaller);
        Assert.Equal("kept", items[1].SourceCaller);
        Assert.Equal("fresh", items[3].SourceCaller);
        Assert.Null(items[2].SourceCaller);
    }

    private static async Task AssertSameAsync(MistakeDbContext db, MistakeDbContext old, MistakeItemEntity item)
    {
        var current = await StoredAsync(db, item.Id);
        var derived = await StoredAsync(old, item.Id);
        Assert.True(current == derived, $"{item.SourceUploadId}: v24 trigger {derived} / v26 write path {current}");
    }

    private static Task<string> StoredAsync(MistakeDbContext db, Guid id) => db.Database.SqlQuery<string>($"""
        SELECT jsonb_build_object('kind',source_kind,'caller',source_caller,'content',content,'detail',source_detail)::text AS "Value"
        FROM mistake_item WHERE id={id}
        """).SingleAsync();

    // The same source data written the v26 way.
    private static MistakeItemEntity Entity(PreUnifiedRow row, string? caller)
    {
        var source = row.Item;
        var item = new MistakeItemEntity
        {
            Id = source.Id, StudentId = source.StudentId, Subject = source.Subject, Grade = source.Grade, SourceUploadId = source.SourceUploadId,
            ReviewStatus = source.ReviewStatus, QuestionId = source.QuestionId, ConversationActive = source.ConversationActive,
            SourceRegions = source.SourceRegions.Select(r => new SourceRegionEntry
            {
                SourceImagePath = r.SourceImagePath,
                BoundingBox = r.BoundingBox is { } b ? new() { X1 = b.X1, Y1 = b.Y1, X2 = b.X2, Y2 = b.Y2 } : null
            }).ToList(),
            CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, SourceCaller = caller
        };
        if (row.ConversationSource is { } evidence) MistakeSource.SetConversation(item, row.ConversationCaller, evidence);
        if (row.AnalysisSnapshot is { } analysis) MistakeSource.SetAnalysis(item, analysis);
        return item;
    }

    private static void AddJob(MistakeDbContext db, Guid id, string caller) => db.ExtractionJobs.Add(new()
    {
        Id = id, Caller = caller, StudentId = "s", SourceKey = "worksheet", PayloadHash = "hash", Version = 1,
        Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    });

    private static MistakeItemEntity Item(string source, string[] paths, Action<MistakeItemEntity> configure)
    {
        var item = new MistakeItemEntity
        {
            Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = source, ReviewStatus = 1,
            SourceRegions = paths.Select(p => new SourceRegionEntry { SourceImagePath = p }).ToList(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        configure(item);
        return item;
    }

    private static PreUnifiedRow Conversation(string? caller, string source, params string[] paths) =>
        new(Item("", paths, i => i.ConversationActive = true), ConversationSource: source, ConversationCaller: caller);
}
