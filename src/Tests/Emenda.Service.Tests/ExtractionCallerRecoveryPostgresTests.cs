using System.Security.Cryptography;
using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Service.Tests;

public sealed class ExtractionCallerRecoveryPostgresTests
{
    /// <summary>
    /// A deletion receipt's owner hash as the application writes it (<c>ExtractionRules.Hash(job.Caller)</c>):
    /// SHA-256 of the JSON-serialized <c>caller:name</c> string, quotes and default escaping included.
    /// </summary>
    internal static string OwnerHash(string caller) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes("caller:" + caller, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

    [Fact]
    public async Task UpgradeFromV24ResolvesCallersOfDeletedTasks()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(UnifiedMistakeSchema.Migration);
        // Callers are known from live tasks. The application escapes "+" in "a+b" as backslash-u002B, which is exactly
        // what plain concatenation yields for another valid caller whose literal name is "a", backslash, "u002Bb".
        AddJob(db, Guid.NewGuid(), "caller:school");
        AddJob(db, Guid.NewGuid(), "caller:a" + (char)92 + "u002Bb");
        await db.SaveChangesAsync();
        var school = await RetainedAsync(db, "school");
        var escaped = await RetainedAsync(db, "a+b");
        var unknown = await RetainedAsync(db, "ghost");
        var updatedAt = await db.MistakeItems.AsNoTracking().OrderBy(i => i.Id).Select(i => i.UpdatedAt).ToListAsync();

        // The receipts are in the application's format, so each caller scope already sees its retained row,
        // while v24 left every one of these callers NULL.
        foreach (var (caller, id) in new[] { ("school", school), ("a+b", escaped), ("ghost", unknown) })
        {
            await using var scoped = database.CreateContext();
            scoped.ConversationCallerScope = caller;
            Assert.True(await scoped.MistakeItems.AnyAsync(i => i.Id == id));
            Assert.Null(await CallerAsync(db, id));
        }

        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(24, upgraded.FromVersion);
        Assert.Equal(25, upgraded.ToVersion);
        Assert.Equal("school", await CallerAsync(db, school));
        // A name the database cannot hash exactly is never compared, so the a+b row is not handed to that other caller;
        // it stays NULL and stays visible through the receipt.
        Assert.Null(await CallerAsync(db, escaped));
        Assert.Null(await CallerAsync(db, unknown));
        Assert.Equal(updatedAt, await db.MistakeItems.AsNoTracking().OrderBy(i => i.Id).Select(i => i.UpdatedAt).ToListAsync());

        // The recovered caller survives later writes even though the task is gone.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET root_cause='reviewed' WHERE id={school}");
        Assert.Equal("school", await CallerAsync(db, school));
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
    }

    [Fact]
    public async Task DowngradeIsRefusedWithoutChangingTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(UnifiedMistakeSchema.Migration));
        Assert.Contains("extraction_caller_recovery_irreversible", exception.ToString());
        Assert.False((await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded);
    }

    private static async Task<Guid> RetainedAsync(MistakeDbContext db, string caller)
    {
        var job = Guid.NewGuid();
        db.ExtractionDeletions.Add(new()
        {
            JobId = job, AdmissionHash = new string('a', 64), SourceBindingHash = new string('b', 64),
            OwnerHash = OwnerHash(caller), DeletedAt = DateTimeOffset.UtcNow, RetainedConfirmed = 1
        });
        var item = new MistakeItemEntity
        {
            Id = Guid.NewGuid(), StudentId = "s", Subject = 2, Grade = 4, SourceUploadId = $"extract:{job:N}", ReviewStatus = 2,
            SourceRegions = [new() { SourceImagePath = $"mistakes/extractions/{job:N}/retained/crop.png" }],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.MistakeItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static void AddJob(MistakeDbContext db, Guid id, string caller) => db.ExtractionJobs.Add(new()
    {
        Id = id, Caller = caller, StudentId = "s", SourceKey = "worksheet", PayloadHash = "hash", Version = 1,
        Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    });

    private static Task<string?> CallerAsync(MistakeDbContext db, Guid id) =>
        db.Database.SqlQuery<string?>($"SELECT source_caller AS \"Value\" FROM mistake_item WHERE id={id}").SingleAsync();
}
