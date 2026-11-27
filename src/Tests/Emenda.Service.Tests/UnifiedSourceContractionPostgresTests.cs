using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

/// <summary>
/// Schema v26 drops analysis_snapshot, conversation_source and conversation_caller with the v24 trigger. The unified
/// columns must come through unchanged and every dropped value must stay recoverable from them; a row that would lose
/// anything stops the upgrade before it changes the database.
/// </summary>
public sealed class UnifiedSourceContractionPostgresTests
{
    private const string Analysis = """
        {"statement":"2+2=?","studentAnswer":"5","referenceAnswer":"4","explanation":"","evidence":"Wrote 5","sourceVersion":3}
        """;

    [Fact]
    public async Task UpgradeKeepsTheUnifiedColumnsAndEveryDroppedValueRecoverable()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ExtractionCallerRecoverySchema.Migration);
        var live = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        AddJob(db, live, "caller:school");
        await db.SaveChangesAsync();
        var retainedCrop = $"mistakes/extractions/{deleted:N}/retained/crop.png";
        var retainedAnalysis = Analysis.Replace("\"sourceVersion\":3", $$"""
            "sourceVersion":2,"retainedFiles":[{"path":"{{retainedCrop}}","contentType":"image/png","length":4,"sha256":"{{new string('c', 64)}}"}]
            """.Trim());
        var conversationSource = """
            {"version":1,"statement":"Solve x+1=3","studentAttempt":{"position":3,"quote":"x=1","assetId":null},"evidence":"Subtracted",
             "referenceAnswer":"x=2","knowledgePoints":["Linear equations"],"attempts":[],
             "images":[{"sourcePosition":3,"assetId":"11111111-1111-4111-8111-111111111111","path":"mistakes/conversation/c.jpg","box":[1,2,3,4]}]}
            """;
        var rows = new (PreUnifiedRow Row, string? Caller)[]
        {
            (new(Item($"extract:{live:N}", 2, $"mistakes/extractions/{live:N}/retained/a.png"), Analysis), null),
            (new(Item($"extract:{deleted:N}", 2, retainedCrop), retainedAnalysis), "school"),
            (new(Item($"extract:{live:N}", 3)), null),
            (new(Item("", 1, "mistakes/conversation/c.jpg"), ConversationSource: conversationSource, ConversationCaller: "tutor"), null),
            (new(Item("legacy-upload", 2, "mistakes/2026/06/08/upload_0.jpg")), null)
        };
        foreach (var (row, caller) in rows) await PreUnifiedRows.InsertWithCallerAsync(db, row, caller);
        var before = await UnifiedAsync(db);

        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(25, upgraded.FromVersion);
        Assert.Equal(28, upgraded.ToVersion);
        Assert.Equal(before, await UnifiedAsync(db));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_schema='public' AND table_name='mistake_item'
              AND column_name IN ('analysis_snapshot','conversation_source','conversation_caller')
            """).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM pg_trigger WHERE tgname='trg_mistake_item_source')
                  + (SELECT count(*) FROM pg_proc WHERE proname='derive_mistake_item_source'))::int AS "Value"
            """).SingleAsync());

        await using var read = database.CreateContext();
        var items = await read.MistakeItems.AsNoTracking().ToDictionaryAsync(i => i.Id);
        AssertJson(Analysis, MistakeSource.Analysis(items[rows[0].Row.Item.Id]));
        AssertJson(retainedAnalysis, MistakeSource.Analysis(items[rows[1].Row.Item.Id]));
        Assert.Equal("school", items[rows[1].Row.Item.Id].SourceCaller);
        Assert.Null(MistakeSource.Analysis(items[rows[2].Row.Item.Id]));
        var conversation = items[rows[3].Row.Item.Id];
        AssertJson(conversationSource, MistakeSource.ConversationSnapshot(conversation));
        Assert.Equal("tutor", conversation.SourceCaller);
        Assert.Null(MistakeSource.Analysis(conversation));
        Assert.Null(MistakeSource.ConversationSnapshot(items[rows[4].Row.Item.Id]));

        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("UPDATE mistake_item SET content=jsonb_set(content,ARRAY['statement'],to_jsonb('edited'::text)) WHERE source_kind='extraction'")]
    [InlineData("UPDATE mistake_item SET source_detail=source_detail-'sourceVersion' WHERE source_kind='extraction'")]
    [InlineData("UPDATE mistake_item SET source_detail=source_detail||jsonb_build_object('extra',1) WHERE source_kind='conversation'")]
    [InlineData("UPDATE mistake_item SET source_caller='other' WHERE source_kind='conversation'")]
    [InlineData("UPDATE mistake_item SET analysis_snapshot=analysis_snapshot||jsonb_build_object('note','x') WHERE source_kind='extraction'")]
    public async Task AnyValueTheUnifiedColumnsWouldLoseStopsTheUpgradeWithoutChanges(string divergence)
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ExtractionCallerRecoverySchema.Migration);
        var job = Guid.NewGuid();
        AddJob(db, job, "caller:school");
        await db.SaveChangesAsync();
        await PreUnifiedRows.InsertAsync(db, new(Item($"extract:{job:N}", 2, $"mistakes/extractions/{job:N}/retained/a.png"), Analysis),
            new(Item("", 1, "mistakes/conversation/o.jpg"), ConversationSource: """{"version":1,"statement":"s"}""", ConversationCaller: "school"));
        // Only a writer that bypassed the trigger could leave such a row; reproduce one.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE mistake_item DISABLE TRIGGER trg_mistake_item_source");
        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(divergence));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE mistake_item ENABLE TRIGGER trg_mistake_item_source");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(db));
        Assert.Contains("unified_mistake_contraction_mismatch", failure.Message);
        Assert.Equal(3, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_schema='public' AND table_name='mistake_item'
              AND column_name IN ('analysis_snapshot','conversation_source','conversation_caller')
            """).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname='trg_mistake_item_source'").SingleAsync());
    }

    [Fact]
    public async Task DowngradeIsRefusedWithoutChangingTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(UnifiedSourceContractionSchema.Migration);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ExtractionCallerRecoverySchema.Migration));
        Assert.Contains("unified_mistake_contraction_irreversible", exception.ToString());
        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(26, upgraded.FromVersion);
        Assert.Equal(28, upgraded.ToVersion);
    }

    private static void AssertJson(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, JsonDocument.Parse(actual).RootElement), actual);
    }

    private static Task<List<string>> UnifiedAsync(MistakeDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT jsonb_build_object('id',id,'kind',source_kind,'caller',source_caller,'content',content,'detail',source_detail,
            'updated',updated_at)::text AS "Value" FROM mistake_item ORDER BY id
        """).ToListAsync();

    private static void AddJob(MistakeDbContext db, Guid id, string caller) => db.ExtractionJobs.Add(new()
    {
        Id = id, Caller = caller, StudentId = "s", SourceKey = "worksheet", PayloadHash = "hash", Version = 1,
        Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    });

    private static MistakeItemEntity Item(string source, int status, params string[] paths) => new()
    {
        Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = source, ReviewStatus = status,
        QuestionId = source.StartsWith("extract:", StringComparison.Ordinal) ? Guid.NewGuid().ToString() : null,
        SourceRegions = paths.Select(p => new SourceRegionEntry { SourceImagePath = p }).ToList(),
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
}
