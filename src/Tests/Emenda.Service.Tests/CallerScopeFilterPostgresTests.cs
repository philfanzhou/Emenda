using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Service.Tests;

/// <summary>
/// The caller-scope query filter reads source_kind/source_caller since phase B of the unified intake. For every
/// ownership case it must show each caller exactly the rows the previous filter (conversation_caller plus the
/// extraction task and deletion-receipt subqueries, reproduced here in SQL) showed.
/// </summary>
public sealed class CallerScopeFilterPostgresTests
{
    [Fact]
    public async Task SourceCallerFilterShowsEachCallerExactlyWhatThePreviousFilterShowed()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var school = Guid.NewGuid();
        var tutor = Guid.NewGuid();
        var admin = Guid.NewGuid();
        AddJob(db, school, "caller:school");
        AddJob(db, tutor, "caller:tutor");
        AddJob(db, admin, "admin:");
        var deletedResolved = Guid.NewGuid();
        var deletedUnresolved = Guid.NewGuid();
        var deletedOther = Guid.NewGuid();
        Receipt(db, deletedResolved, "school");
        Receipt(db, deletedUnresolved, "school");
        Receipt(db, deletedOther, "tutor");
        db.MistakeItems.AddRange(
            Item("legacy-upload"),
            Item(""),
            Item($"extract:{school:N}"),
            Item($"extract:{tutor:N}"),
            Item($"extract:{admin:N}"),
            Item($"extract:{deletedResolved:N}", caller: "school"),
            Item($"extract:{deletedUnresolved:N}"),
            Item($"extract:{deletedOther:N}"),
            Item($"extract:{Guid.NewGuid():N}"),
            Item("extract:not-a-job"),
            Conversation("school"),
            Conversation("tutor"),
            Conversation(null));
        await db.SaveChangesAsync();
        // A caller recovered earlier survives; one never resolved stays NULL and is matched through its receipt.
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE mistake_item SET source_caller=NULL WHERE source_upload_id={$"extract:{deletedUnresolved:N}"}"));

        foreach (var scope in new[] { "school", "tutor", "nobody" })
        {
            await using var scoped = database.CreateContext();
            scoped.ConversationCallerScope = scope;
            var visible = await scoped.MistakeItems.Select(i => i.Id).OrderBy(id => id).ToListAsync();
            var hash = ExtractionCallerRecoveryPostgresTests.OwnerHash(scope);
            var previous = await db.Database.SqlQuery<Guid>($"""
                SELECT e.id AS "Value" FROM mistake_item e
                WHERE (e.conversation_source IS NULL OR e.conversation_caller = {scope})
                  AND (e.source_upload_id NOT LIKE 'extract:%'
                    OR EXISTS (SELECT 1 FROM extraction_job j
                        WHERE e.source_upload_id = 'extract:' || replace(j.id::text, '-', '') AND j.caller = 'caller:' || {scope})
                    OR EXISTS (SELECT 1 FROM extraction_deletion d
                        WHERE e.source_upload_id = 'extract:' || replace(d.job_id::text, '-', '') AND d.owner_hash = {hash}))
                ORDER BY e.id
                """).ToListAsync();
            Assert.Equal(previous, visible);
        }
        await using (var schoolScope = database.CreateContext())
        {
            schoolScope.ConversationCallerScope = "school";
            Assert.Equal(6, await schoolScope.MistakeItems.CountAsync());
        }
    }

    private static void AddJob(MistakeDbContext db, Guid id, string caller) => db.ExtractionJobs.Add(new()
    {
        Id = id, Caller = caller, StudentId = "s", SourceKey = "worksheet", PayloadHash = "hash", Version = 1,
        Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    });

    private static void Receipt(MistakeDbContext db, Guid job, string caller) => db.ExtractionDeletions.Add(new()
    {
        JobId = job, AdmissionHash = new string('a', 64), SourceBindingHash = new string('b', 64),
        OwnerHash = ExtractionCallerRecoveryPostgresTests.OwnerHash(caller), DeletedAt = DateTimeOffset.UtcNow, RetainedConfirmed = 1
    });

    private static MistakeItemEntity Item(string source, string? caller = null) => new()
    {
        Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = source, ReviewStatus = 2,
        SourceRegions = [], SourceCaller = caller, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static MistakeItemEntity Conversation(string? caller)
    {
        var item = Item("");
        item.ConversationCaller = caller;
        item.ConversationSource = """{"version":1,"statement":"s"}""";
        return item;
    }
}
