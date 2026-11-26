using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Service.Tests;

/// <summary>
/// MistakeMaterializer (C#, on every EF save) and trg_mistake_item_source (PostgreSQL) implement one mapping.
/// Each case is saved through EF; the values the materializer left on the tracked entity must equal what the
/// trigger stored, compared as jsonb.
/// </summary>
public sealed class MistakeMaterializerParityPostgresTests
{
    [Fact]
    public async Task MaterializerAndTriggerDeriveTheSameShapeForEveryCase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var live = Guid.NewGuid();
        var admin = Guid.NewGuid();
        AddJob(db, live, "caller:school");
        AddJob(db, admin, "admin:");
        await db.SaveChangesAsync();
        var deleted = Guid.NewGuid();
        var unsaved = Guid.NewGuid();
        // A task added in the same save is resolved from the change tracker.
        AddJob(db, unsaved, "caller:fresh");

        var items = new List<MistakeItemEntity>
        {
            Item($"extract:{live:N}", [$"mistakes/extractions/{live:N}/retained/a.png", $"mistakes/extractions/{live:N}/retained/b.png"], i =>
            {
                i.QuestionId = Guid.NewGuid().ToString();
                i.AnalysisSnapshot = """{"statement":"2+2=?","studentAnswer":"5","referenceAnswer":"4","explanation":"Add","evidence":"Wrote 5","sourceVersion":3}""";
            }),
            // JSON nulls, a retained-file manifest and a preset caller kept because the task is gone.
            Item($"extract:{deleted:N}", [$"mistakes/extractions/{deleted:N}/retained/c.png"], i =>
            {
                i.SourceCaller = "kept";
                i.AnalysisSnapshot = $$"""{"statement":null,"studentAnswer":"7","referenceAnswer":null,"explanation":"","evidence":"e","sourceVersion":2,"retainedFiles":[{"path":"mistakes/extractions/{{deleted:N}}/retained/c.png","contentType":"image/png","length":4,"sha256":"{{new string('c', 64)}}","note":null}]}""";
            }),
            Item($"extract:{admin:N}", [], i => i.AnalysisSnapshot = """{"statement":"admin"}"""),
            Item($"extract:{unsaved:N}", [$"mistakes/extractions/{unsaved:N}/retained/d.png"], _ => { }),
            Item("extract:NOT-A-JOB", ["mistakes/extractions/x/retained/e.png"], i => i.QuestionId = "q"),
            Item($"extract:{Guid.NewGuid():N}", [], i => i.AnalysisSnapshot = "[1,2]"),
            Conversation("school", """
                {"version":1,"statement":"Solve x+1=3","studentAttempt":{"position":3,"quote":"x=1","assetId":null},"evidence":"Subtracted",
                 "referenceAnswer":"x=2","knowledgePoints":["Linear equations","Signs"],
                 "images":[{"sourcePosition":3,"assetId":null,"path":"mistakes/conversation/o.jpg","box":null},
                           {"sourcePosition":3,"assetId":"11111111-1111-4111-8111-111111111111","path":"mistakes/conversation/c.jpg","box":[10,20,300,400]},
                           {"sourcePosition":4,"assetId":"22222222-2222-4222-8222-222222222222","path":"mistakes/conversation/n.jpg","box":"bad"}]}
                """, "mistakes/conversation/o.jpg", "mistakes/conversation/c.jpg", "mistakes/conversation/n.jpg", "mistakes/conversation/missing.jpg"),
            Conversation("tutor", """{"statement":null,"studentAttempt":null,"knowledgePoints":"none","images":"none","referenceAnswer":5}""",
                "mistakes/conversation/x.jpg"),
            Conversation(null, "[]", "mistakes/conversation/y.jpg"),
            Item("upload-1", ["mistakes/2026/06/08/upload_0.jpg", "mistakes/2026/06/08/upload_1.jpg"],
                i => i.SourceRegions[0].BoundingBox = new() { X1 = 1, Y1 = 2, X2 = 30, Y2 = 40 }),
            Item("", [], i => i.AnalysisSnapshot = """{"statement":"ignored for legacy"}""")
        };
        db.MistakeItems.AddRange(items);
        await db.SaveChangesAsync();
        foreach (var item in items) await AssertSameAsync(db, item);

        // Updates go through the same pair: a correction, a conversation merge and a source switch.
        items[0].AnalysisSnapshot = """{"statement":"3+3=?","studentAnswer":"7","referenceAnswer":"6","explanation":null,"evidence":"e","sourceVersion":4}""";
        items[0].SourceRegions = [new() { SourceImagePath = $"mistakes/extractions/{live:N}/retained/z.png" }];
        items[6].ConversationSource = items[6].ConversationSource!.Replace("Signs", "Inverse operations");
        items[10].SourceUploadId = $"extract:{live:N}";
        await db.SaveChangesAsync();
        foreach (var item in items) await AssertSameAsync(db, item);
        Assert.Equal("school", items[10].SourceCaller);
        Assert.Equal("kept", items[1].SourceCaller);
        Assert.Equal("fresh", items[3].SourceCaller);
        Assert.Null(items[2].SourceCaller);
    }

    private static async Task AssertSameAsync(MistakeDbContext db, MistakeItemEntity item)
    {
        var stored = await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('kind',source_kind,'caller',source_caller,'content',content,'detail',source_detail,
                'same', source_kind={item.SourceKind} AND source_caller IS NOT DISTINCT FROM {item.SourceCaller}
                    AND content={item.Content}::jsonb AND source_detail IS NOT DISTINCT FROM {item.SourceDetail}::jsonb)::text AS "Value"
            FROM mistake_item WHERE id={item.Id}
            """).SingleAsync();
        using var row = JsonDocument.Parse(stored);
        Assert.True(row.RootElement.GetProperty("same").GetBoolean(),
            $"{item.SourceUploadId}: trigger {stored} / materializer kind={item.SourceKind} caller={item.SourceCaller} content={item.Content} detail={item.SourceDetail}");
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

    private static MistakeItemEntity Conversation(string? caller, string source, params string[] paths) => Item("", paths, i =>
    {
        i.ConversationCaller = caller;
        i.ConversationSource = source;
        i.ConversationActive = true;
    });
}
