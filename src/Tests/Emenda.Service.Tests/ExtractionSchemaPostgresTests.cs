using Emenda.Database;
using Emenda.Database.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Emenda.Service.Tests;

public sealed class ExtractionSchemaPostgresTests
{
    [Fact]
    public async Task Version15Upgrade_PreservesRows_AndVerifiesExactTargetAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ProductIntakeSchema.Migration);
        var itemId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ({itemId},'existing',1,7,'source','[]'::jsonb,1,now())");
        var result = await DatabaseInitializer.UpgradeToTargetAsync(db);
        result.FromVersion.Should().Be(15);
        result.ToVersion.Should().Be(28);
        (await db.MistakeItems.SingleAsync()).Id.Should().Be(itemId);
        (await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded.Should().BeFalse();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain("20261116000000_AddExtractions");
    }

    [Fact]
    public async Task Version16AnalysisUpgradePreservesExtractionReceiptAndOriginalManifestAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(ExtractionSchema.Migration);
        var job = NewJob();
        job.SourcesJson = "[{\"id\":\"702b2566-b846-40e5-81dd-9d742166e009\",\"path\":\"mistakes/extractions/retained/original.png\",\"sha256\":\"original-digest\"}]";
        var receipt = NewRevision(job.Id);
        receipt.SnapshotJson = "{\"questions\":[]}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_job(id,caller,student_id,grade,source_key,payload_hash,status,sources_json,created_at,updated_at)
            VALUES ({job.Id},{job.Caller},{job.StudentId},{job.Grade},{job.SourceKey},{job.PayloadHash},{job.Status},
                {job.SourcesJson}::jsonb,{job.CreatedAt},{job.UpdatedAt})
            """);
        db.ExtractionRevisions.Add(receipt);
        await db.SaveChangesAsync();
        var upgraded = await DatabaseInitializer.UpgradeToTargetAsync(db);
        upgraded.FromVersion.Should().Be(16);
        upgraded.ToVersion.Should().Be(28);
        db.ChangeTracker.Clear();
        (await db.ExtractionJobs.SingleAsync()).SourcesJson.Should().Contain("original-digest");
        (await db.ExtractionRevisions.SingleAsync()).RequestKey.Should().Be(receipt.RequestKey);
        (await DatabaseInitializer.UpgradeToTargetAsync(db)).Upgraded.Should().BeFalse();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task EfPersistence_EnforcesReplayUniqueness_AndRetainsTombstoneAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var job = NewJob();
        var itemId = Guid.NewGuid();
        var question = new ExtractionQuestionEntity { Id = Guid.NewGuid(), JobId = job.Id, ItemId = itemId, Active = true };
        var revision = NewRevision(job.Id);
        db.ExtractionJobs.Add(job);
        db.ExtractionQuestions.Add(question);
        db.ExtractionRevisions.Add(revision);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await db.ExtractionJobs.SingleAsync()).SourcesJson.Should().Be("[]");
        (await db.ExtractionJobs.SingleAsync()).RetryKeysJson.Should().Be("[]");
        (await db.ExtractionRevisions.SingleAsync()).RequestKey.Should().Be(revision.RequestKey);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at,source_kind,content) VALUES ({itemId},'existing',1,7,'source','[]'::jsonb,1,now(),'legacy',jsonb_build_object('version',1,'images','[]'::jsonb))");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM mistake_item WHERE id={itemId}");
        (await db.ExtractionQuestions.SingleAsync()).ItemId.Should().Be(itemId);
        await RejectAsync(db, $"INSERT INTO extraction_job SELECT '{Guid.NewGuid()}'::uuid,caller,source_key,payload_hash,student_id,grade,status,version,sources_json,layout_json,candidate_json,error_code,created_at,updated_at,owner_id,fence,lease_until,attempt,last_retry_key,retry_keys_json,content_hash FROM extraction_job", PostgresErrorCodes.UniqueViolation);
        await RejectAsync(db, "INSERT INTO extraction_revision SELECT job_id,version+1,request_key,payload_hash,snapshot_json,actor,reason,model,created_at FROM extraction_revision", PostgresErrorCodes.UniqueViolation);
        await RejectAsync(db, $"INSERT INTO extraction_revision SELECT job_id,version,'{Guid.NewGuid()}'::uuid,payload_hash,snapshot_json,actor,reason,model,created_at FROM extraction_revision", PostgresErrorCodes.UniqueViolation);
        await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task RetainedFacts_RejectMutationDeletionTruncationAndNonemptyDowngradeAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var job = NewJob();
        db.ExtractionJobs.Add(job);
        db.ExtractionQuestions.Add(new ExtractionQuestionEntity { Id = Guid.NewGuid(), JobId = job.Id, ItemId = Guid.NewGuid(), Active = true });
        db.ExtractionRevisions.Add(NewRevision(job.Id));
        await db.SaveChangesAsync();
        foreach (var sql in new[] {
            "UPDATE extraction_revision SET reason=reason", "DELETE FROM extraction_revision", "TRUNCATE extraction_revision",
            "DELETE FROM extraction_question", "UPDATE extraction_question SET item_id=NULL", "UPDATE extraction_question SET job_id=gen_random_uuid()",
            "TRUNCATE extraction_question", "DELETE FROM extraction_job", "TRUNCATE extraction_job CASCADE" })
            await RejectAsync(db, sql, PostgresErrorCodes.RaiseException);
        await db.Database.ExecuteSqlRawAsync("UPDATE extraction_question SET active=false");
        await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ProductIntakeSchema.Migration));
        await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
        db.ChangeTracker.Clear();
        (await db.ExtractionRevisions.CountAsync()).Should().Be(1);
        (await db.ExtractionQuestions.SingleAsync()).Active.Should().BeFalse();
    }

    [Fact]
    public async Task Verifier_RejectsChangedExtractionGuardAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE OR REPLACE FUNCTION guard_extraction_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance));
    }

    private static ExtractionJobEntity NewJob() => new()
    {
        Id = Guid.NewGuid(), Caller = "schema-test", SourceKey = "source", PayloadHash = new string('a', 64),
        StudentId = "student", Grade = 7, Status = "Succeeded", Version = 1,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ExtractionRevisionEntity NewRevision(Guid jobId) => new()
    {
        JobId = jobId, Version = 1, RequestKey = Guid.NewGuid(), PayloadHash = new string('b', 64),
        SnapshotJson = "{}", Actor = "admin", Reason = "initial", Model = "model", CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task RejectAsync(MistakeDbContext db, string sql, string sqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        error.SqlState.Should().Be(sqlState);
    }
}
