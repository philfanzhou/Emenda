using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace Emenda.Service.Tests;

public sealed class ExtractionMistakeRemovalPostgresTests
{
    private static readonly ExtractionAccess Caller = new("removal-tests", "learner");

    [Fact]
    public async Task RemovesOnePendingMultipartMistake_ReclaimsItsOwnFiles_AndKeepsEverythingElse()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0], job.Questions[1]);
        var item = q12.ItemId!.Value;
        // Question 12 continues onto page 2, which question 13 also uses; one extra page was supplied for 12 only.
        var multipart = await service.ReviseQuestionAsync(Caller, job.Id, q12.Question.Id, new(Guid.NewGuid(), job.Version,
            "continues on page 2", q12.Question with { Regions = [q12.Question.Regions[0], new(job.Sources[1].Id, 0, .5, 1, 1)] }));
        var supplement = await service.RequestSupplementAsync(Caller, job.Id, q12.Question.Id,
            new(Guid.NewGuid(), multipart.Version, item, "请补拍验算"));
        await service.AppendSupplementAsync(Caller, job.Id, q12.Question.Id, supplement.Id,
            new(Guid.NewGuid(), supplement.Version, item, [Image(3)]));
        await using (var context = db.CreateContext())
        {
            var neighbor = await context.MistakeItems.SingleAsync(i => i.Id == q13.ItemId);
            neighbor.ReviewStatus = (int)ReviewStatus.Confirmed;
            neighbor.ReviewerId = "teacher";
            neighbor.RootCause = "confirmed neighbor";
            await context.SaveChangesAsync();
        }
        var before = await service.GetAsync(Caller, job.Id);
        var target = before.Questions.Single(q => q.Question.Id == q12.Question.Id);
        var extra = Assert.Single(before.Sources.Except(job.Sources));
        Assert.Equal(2, target.Images.Count);
        var owned = target.Images.Select(i => i.Path).Append(extra.Path).ToList();
        foreach (var path in owned) await db.Files.CopyObjectAsync(path, ThumbnailHelper.GetAllThumbnailPaths(path).First());
        var request = new ExtractionMistakeRemovalRequest(before.Version, item, (int)ReviewStatus.PendingReview);

        var receipt = await service.RemoveMistakeAsync(Caller, job.Id, q12.Question.Id, request);

        Assert.Equal(new ExtractionMistakeRemovalView(job.Id, "student", q12.Question.Id, item, before.Version + 1, true), receipt);
        var after = await service.GetAsync(Caller, job.Id);
        Assert.Equal(receipt.Version, after.Version);
        Assert.Equal(JsonSerializer.Serialize(before.Questions.Where(q => q.Question.Id == q13.Question.Id)),
            JsonSerializer.Serialize(after.Questions));
        Assert.Equal(job.Sources.Select(s => s.Id), after.Sources.Select(s => s.Id));
        foreach (var source in job.Sources) Assert.True(await db.Files.ObjectExistsAsync(source.Path), "originals are retained");
        foreach (var crop in after.Questions.Single().Images) Assert.True(await db.Files.ObjectExistsAsync(crop.Path));
        foreach (var path in owned.SelectMany(p => ThumbnailHelper.GetAllThumbnailPaths(p).Prepend(p)))
            Assert.False(await db.Files.ObjectExistsAsync(path), path);
        await using (var check = db.CreateContext())
        {
            Assert.False(await check.MistakeItems.AnyAsync(i => i.Id == item));
            Assert.False(await check.ProcessLogs.AnyAsync(l => l.MistakeItemId == item));
            Assert.False(await check.ExtractionSupplements.AnyAsync(s => s.QuestionId == q12.Question.Id));
            Assert.False(await check.ExtractionObjects.AnyAsync(o => owned.Contains(o.Path)));
            var neighbor = await check.MistakeItems.SingleAsync(i => i.Id == q13.ItemId);
            Assert.Equal(((int)ReviewStatus.Confirmed, "confirmed neighbor"), (neighbor.ReviewStatus, neighbor.RootCause));
            Assert.Equal(q13.Images.Select(i => i.Path), neighbor.SourceRegions.Select(r => r.SourceImagePath));
        }

        // A lost response is retried with the same body; the inspected version still identifies this removal.
        Assert.Equal(receipt, await service.RemoveMistakeAsync(Caller, job.Id, q12.Question.Id, request));
        Assert.Equal(receipt.Version, (await service.GetAsync(Caller, job.Id)).Version);
        var again = await Assert.ThrowsAsync<ExtractionException>(() => service.RemoveMistakeAsync(Caller, job.Id,
            q12.Question.Id, request with { ExpectedVersion = receipt.Version }));
        Assert.Equal((404, "not_found"), (again.Status, again.Code));
    }

    [Fact]
    public async Task InterruptedCleanup_ReportsPending_ThenTheSameRequestCompletesIt()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        var target = job.Questions[0];
        var crop = target.Images[0].Path;
        db.Oss.Setup(o => o.DeleteIfExistsAsync(crop)).ReturnsAsync(false);
        var request = new ExtractionMistakeRemovalRequest(job.Version, target.ItemId!.Value, (int)ReviewStatus.PendingReview);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pending = await Assert.ThrowsAsync<ExtractionException>(() =>
                service.RemoveMistakeAsync(Caller, job.Id, target.Question.Id, request));
            Assert.Equal((503, "cleanup_pending"), (pending.Status, pending.Code));
        }
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal((job.Version + 1, 1), (read.Version, read.Questions.Count));
        Assert.True(await db.Files.ObjectExistsAsync(crop), "the failed delete stays queued");

        db.Oss.Setup(o => o.DeleteIfExistsAsync(crop)).Returns((string path) => ((IOssService)db.Files).DeleteIfExistsAsync(path));
        var receipt = await service.RemoveMistakeAsync(Caller, job.Id, target.Question.Id, request);

        Assert.Equal((job.Version + 1, true), (receipt.Version, receipt.CleanupComplete));
        Assert.False(await db.Files.ObjectExistsAsync(crop));
        await using var check = db.CreateContext();
        Assert.False(await check.ExtractionObjects.AnyAsync(o => o.Path == crop));
        Assert.Equal(job.Version + 1, (await check.ExtractionJobs.SingleAsync()).Version);
    }

    [Fact]
    public async Task ConfirmationCommittedWhileRemovalWaits_SurvivesAndRemovalConflicts()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        var target = job.Questions[0];
        await using var reviewer = new NpgsqlConnection(db.Connection);
        await reviewer.OpenAsync();
        await using var tx = await reviewer.BeginTransactionAsync();
        // The review endpoint holds this job lock while it confirms.
        await using (var lockJob = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", reviewer, tx))
        {
            lockJob.Parameters.AddWithValue("key", BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(job.Id.ToString())), 0));
            await lockJob.ExecuteNonQueryAsync();
        }

        var removal = service.RemoveMistakeAsync(Caller, job.Id, target.Question.Id,
            new(job.Version, target.ItemId!.Value, (int)ReviewStatus.PendingReview));
        await Task.Delay(500);
        Assert.False(removal.IsCompleted, "removal waits for the reviewer's job lock");
        await using (var confirm = new NpgsqlCommand(
            "UPDATE mistake_item SET review_status=2, reviewer_id='teacher', root_cause='teacher cause' WHERE id=@id", reviewer, tx))
        {
            confirm.Parameters.AddWithValue("id", target.ItemId.Value);
            Assert.Equal(1, await confirm.ExecuteNonQueryAsync());
        }
        await tx.CommitAsync();

        var error = await Assert.ThrowsAsync<ExtractionException>(() => removal);
        Assert.Equal((409, "review_conflict"), (error.Status, error.Code));
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(JsonSerializer.Serialize(job.Questions), JsonSerializer.Serialize(read.Questions));
        Assert.True(await db.Files.ObjectExistsAsync(target.Images[0].Path));
        await using var check = db.CreateContext();
        var item = await check.MistakeItems.SingleAsync(i => i.Id == target.ItemId);
        Assert.Equal(((int)ReviewStatus.Confirmed, "teacher cause"), (item.ReviewStatus, item.RootCause));
    }

    [Fact]
    public async Task RemovesAnUnknownJudgmentLikeAnyPendingMistake_AndKeepsTheOriginals()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db, new Analyzer { Unknown13 = true });
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0], job.Questions[1]);
        Assert.Equal("unknown", q13.Question.Outcome);

        var receipt = await service.RemoveMistakeAsync(Caller, job.Id, q13.Question.Id,
            new(job.Version, q13.ItemId!.Value, (int)ReviewStatus.PendingReview));

        Assert.Equal(job.Version + 1, receipt.Version);
        var after = await service.GetAsync(Caller, job.Id);
        Assert.Equal(JsonSerializer.Serialize(new[] { q12 }), JsonSerializer.Serialize(after.Questions));
        foreach (var source in job.Sources) Assert.True(await db.Files.ObjectExistsAsync(source.Path), "originals are retained");
        Assert.False(await db.Files.ObjectExistsAsync(q13.Images[0].Path));
        await using var check = db.CreateContext();
        Assert.Equal(q12.ItemId, (await check.MistakeItems.SingleAsync()).Id);
    }

    [Fact]
    public async Task UnknownJudgmentPublishedWithoutCrops_GetsItsItemOnlyWhenASnapshotIsRebuilt()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db, new Analyzer { Unknown13 = true });
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0], job.Questions[1]);
        // Before unknown judgments were collected, question 13 was published with neither an item nor crops.
        await LegacyUnknownAsync(db, job, q13, rejectedItem: false);

        // A removal republishes question 13 as it is, so it cannot create an item without crops.
        var removed = await service.RemoveMistakeAsync(Caller, job.Id, q12.Question.Id,
            new(job.Version, q12.ItemId!.Value, (int)ReviewStatus.PendingReview));
        var after = Assert.Single((await service.GetAsync(Caller, job.Id)).Questions);
        Assert.Equal((q13.Question.Id, (Guid?)null), (after.Question.Id, after.ItemId));
        await using (var context = db.CreateContext()) Assert.False(await context.MistakeItems.AnyAsync());

        // Rebuilding the snapshot crops it and creates its pending item.
        var revised = await service.ReviseQuestionAsync(Caller, job.Id, q13.Question.Id,
            new(Guid.NewGuid(), removed.Version, "题干补全", after.Question with { Statement = "Question 13 in full" }));
        var collected = revised.Question!;
        Assert.NotNull(collected.ItemId);
        Assert.True(await db.Files.ObjectExistsAsync(Assert.Single(collected.Images).Path));
        await using var check = db.CreateContext();
        var item = await check.MistakeItems.SingleAsync();
        Assert.Equal((collected.ItemId, (int)ReviewStatus.PendingReview), (item.Id, item.ReviewStatus));
        Assert.Equal(collected.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
    }

    [Fact]
    public async Task UnknownJudgmentWhoseItemWasRejectedForIt_IsReopenedOnlyWhenASnapshotIsRebuilt()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db, new Analyzer { Unknown13 = true });
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0], job.Questions[1]);
        // Before unknown judgments were collected, changing question 13 to unknown rejected its item and dropped its crops.
        await LegacyUnknownAsync(db, job, q13, rejectedItem: true);

        var removed = await service.RemoveMistakeAsync(Caller, job.Id, q12.Question.Id,
            new(job.Version, q12.ItemId!.Value, (int)ReviewStatus.PendingReview));
        await using (var context = db.CreateContext())
            Assert.Equal((int)ReviewStatus.Rejected, (await context.MistakeItems.SingleAsync()).ReviewStatus);

        // A rebuild that leaves question 13 unchanged still crops it and reopens the same item for review.
        var unchanged = Assert.Single((await service.GetAsync(Caller, job.Id)).Questions).Question;
        var rebuilt = await service.ReviseAsync(Caller, job.Id, new(Guid.NewGuid(), removed.Version, "重建", [unchanged]));
        var view = Assert.Single(rebuilt.Questions);
        Assert.Equal(q13.ItemId, view.ItemId);
        Assert.True(await db.Files.ObjectExistsAsync(Assert.Single(view.Images).Path));
        await using var check = db.CreateContext();
        var item = await check.MistakeItems.SingleAsync();
        Assert.Equal(((int)ReviewStatus.PendingReview, (DateTimeOffset?)null), (item.ReviewStatus, item.ReviewedAt));
        Assert.Equal(view.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
    }

    /// <summary>
    /// Recreates question 13 as published before unknown judgments were collected: no crops, and either no item or one
    /// rejected without a reviewer. Bypasses the guards that keep a published binding immutable.
    /// </summary>
    private static async Task LegacyUnknownAsync(PostgresTestDatabase db, ExtractionJobView job, ExtractionQuestionView q13,
        bool rejectedItem)
    {
        await using var context = db.CreateContext();
        var revision = await context.ExtractionRevisions.SingleAsync(r => r.JobId == job.Id && r.Version == job.Version);
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(revision.SnapshotJson)!;
        var legacy = snapshot["questions"]![1]!;
        if (!rejectedItem) legacy["itemId"] = null;
        legacy["images"] = new System.Text.Json.Nodes.JsonArray();
        var json = snapshot.ToJsonString();
        var empty = "[]";
        if (rejectedItem)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SET session_replication_role = replica;
                UPDATE extraction_revision SET snapshot_json={json}::jsonb WHERE job_id={job.Id} AND version={job.Version};
                UPDATE mistake_item SET review_status={(int)ReviewStatus.Rejected}, reviewed_at=NULL, source_regions={empty}::jsonb
                    WHERE id={q13.ItemId};
                SET session_replication_role = origin;
                """);
        else
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SET session_replication_role = replica;
                UPDATE extraction_revision SET snapshot_json={json}::jsonb WHERE job_id={job.Id} AND version={job.Version};
                UPDATE extraction_question SET item_id=NULL WHERE id={q13.Question.Id};
                DELETE FROM mistake_item WHERE id={q13.ItemId};
                SET session_replication_role = origin;
                """);
    }

    [Fact]
    public async Task MismatchedStaleForeignOrConfirmedTargets_AreRejectedWithoutChanges()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0], job.Questions[1]);
        var valid = new ExtractionMistakeRemovalRequest(job.Version, q12.ItemId!.Value, (int)ReviewStatus.PendingReview);
        async Task<(int, string)> Reject(ExtractionMistakeRemovalRequest request, ExtractionAccess? access = null, Guid? question = null)
        {
            var error = await Assert.ThrowsAsync<ExtractionException>(() =>
                service.RemoveMistakeAsync(access ?? Caller, job.Id, question ?? q12.Question.Id, request));
            return (error.Status, error.Code);
        }

        Assert.Equal((400, "invalid_request"), await Reject(valid with { ExpectedReviewStatus = (int)ReviewStatus.Confirmed }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { ItemId = Guid.Empty }));
        Assert.Equal((404, "not_found"), await Reject(valid, new("someone-else", "learner")));
        Assert.Equal((409, "version_conflict"), await Reject(valid with { ExpectedVersion = job.Version + 1 }));
        Assert.Equal((409, "item_binding_conflict"), await Reject(valid with { ItemId = q13.ItemId!.Value }));
        Assert.Equal((404, "not_found"), await Reject(valid, question: Guid.NewGuid()));
        await using (var context = db.CreateContext())
        {
            var item = await context.MistakeItems.SingleAsync(i => i.Id == q12.ItemId);
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            item.ReviewerId = "teacher";
            await context.SaveChangesAsync();
        }
        Assert.Equal((409, "review_conflict"), await Reject(valid));

        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(job.Version, read.Version);
        Assert.Equal(JsonSerializer.Serialize(job.Questions), JsonSerializer.Serialize(read.Questions));
        await using var check = db.CreateContext();
        Assert.Equal(2, await check.MistakeItems.CountAsync());
    }

    private static ExtractionService Service(PostgresTestDatabase db, Analyzer? analyzer = null) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, analyzer ?? new Analyzer(),
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    private static async Task<ExtractionJobView> Completed(ExtractionService service)
    {
        var queued = await service.SubmitAsync(Caller, new("student", 4, "two-pages", [Image(1), Image(2)]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        Assert.Equal("Succeeded", job.Status);
        Assert.All(job.Questions, q => Assert.Single(q.Images));
        return job;
    }

    private static ExtractionImage Image(int n)
    {
        using var image = new ImageMagick.MagickImage(ExtractionHttpPostgresTests.TestPng());
        image.Resize(20, 10);
        image.Colorize(new ImageMagick.MagickColor((byte)(n * 40), 20, 40), new ImageMagick.Percentage(100));
        return new() { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(ImageMagick.MagickFormat.Png) };
    }

    /// <summary>Question 12 sits on the top half of page 1; question 13 on the top half of page 2.</summary>
    private sealed class Analyzer : IQuestionAnalysisClient
    {
        /// <summary>Judges question 13 unknown: the answer could not be read.</summary>
        public bool Unknown13 { get; init; }

        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            ExtractionQuestionDraft Question(string number, int page) => new()
            { QuestionNumber = number, Subject = 2, Statement = "Question " + number, StudentAnswer = "3", ReferenceAnswer = "2",
                Explanation = "2 is correct", Evidence = "Visible answer 3 differs from 2", Outcome = "incorrect",
                Regions = [new(request.Images[page].SourceId, 0, 0, 1, .5)] };
            var q13 = Question("13", 1);
            if (Unknown13) q13 = q13 with { Outcome = "unknown", StudentAnswer = null, Evidence = "" };
            return Task.FromResult(new QuestionAnalysisResult("controlled", [Question("12", 0), q13]));
        }
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "controlled", Width = 20, Height = 10,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
