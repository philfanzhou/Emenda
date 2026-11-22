using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using FluentAssertions;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Emenda.Service.Tests;

public sealed class ExtractionWorkflowPostgresTests
{
    private static readonly ExtractionAccess Caller = new("school", "school");
    private static readonly ExtractionAccess Admin = new("admin", "reviewer", true);

    [Fact]
    public async Task MultiImageQuestionAndSingleImageQuestions_OnlyActualMistakesAreMaterialized()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer(request => new("test-model", [
            Question("incorrect", [new(request.Images[0].SourceId, 0, 0, .5, 1), new(request.Images[1].SourceId, 0, 0, 1, 1)]) with { QuestionNumber = "三、2（1）" },
            Question("correct", [new(request.Images[0].SourceId, .5, 0, 1, 1)]) with { QuestionNumber = "三、2（1）", Statement = "2+2", StudentAnswer = "4", ReferenceAnswer = "4" },
            Question("unknown", [new(request.Images[1].SourceId, 0, 0, .5, .5)]) with { Statement = "Unreadable attempt", StudentAnswer = null, Evidence = "" }
        ]));
        var service = Service(database, analyzer);
        var upload = Upload(images: 2);
        var queued = await service.SubmitAsync(Caller, upload);
        queued.Status.Should().Be("Queued");
        await service.RunQueuedAsync(default);
        var result = await service.GetAsync(Caller, queued.Id);
        result.Status.Should().Be("Succeeded");
        result.Questions.Select(q => q.Question.Outcome).Should().Equal("incorrect", "correct", "unknown");
        result.Questions[0].Images.Should().HaveCount(2);
        result.Questions.Select(q => q.Question.QuestionNumber).Should().Equal("三、2（1）", "三、2（1）", null);
        result.Questions[0].Images.Select(i => i.SourceId).Should().Equal(result.Sources.Select(s => s.Id));
        (await service.GetVersionAsync(Caller, result.Id, 1)).Questions[0].Question.QuestionNumber.Should().Be("三、2（1）");
        result.Questions.Where(q => q.ItemId.HasValue).Should().ContainSingle();
        var original = await service.ReadSourceAsync(Caller, result.Id, result.Sources[0].Id);
        original.Bytes.Should().Equal(upload.Images[0].Content);
        var crop = await service.ReadCropAsync(Caller, result.Id, result.Questions[0].Question.Id, 1, 0);
        using var decoded = new MagickImage(crop.Bytes);
        decoded.Width.Should().Be(20);
        decoded.Height.Should().Be(20);
        await using var db = database.CreateContext();
        var item = await db.MistakeItems.SingleAsync();
        item.Id.Should().Be(result.Questions[0].ItemId!.Value);
        item.ReviewStatus.Should().Be(1);
        item.SourceRegions.Select(r => r.SourceImagePath).Should().Equal(result.Questions[0].Images.Select(i => i.Path));
        item.RootCause.Should().BeNull("a comparison explanation is not a proven root cause");
        await Assert.ThrowsAsync<ExtractionException>(() => service.GetAsync(new("other", "other"), result.Id));
    }

    [Fact]
    public async Task FortyQuestionUpload_KeepsAllOutcomesAndCrossPageOrderInPublishedVersion()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer(request => new("test-model", Enumerable.Range(1, 40).Select(n =>
        {
            var source = request.Images[(n - 1) / 20].SourceId;
            List<ExtractionRegion> regions = [new(source, 0, 0, 1, 1)];
            if (n == 20) regions.Add(new(request.Images[1].SourceId, 0, 0, .5, 1));
            var outcome = n is 20 or 40 ? "incorrect" : n == 33 ? "unknown" : "correct";
            return Question(outcome, regions) with { QuestionNumber = n.ToString(), Statement = $"Question {n}" };
        }).ToList()));
        var service = Service(database, analyzer);
        var job = await service.SubmitAsync(Caller, Upload(images: 2));
        await service.RunQueuedAsync(default);

        var result = await service.GetAsync(Caller, job.Id);
        result.Status.Should().Be("Succeeded", result.ErrorCode);
        var labels = Enumerable.Range(1, 40).Select(n => n.ToString());
        result.Questions.Select(q => q.Question.QuestionNumber).Should().Equal(labels);
        (await service.GetVersionAsync(Caller, job.Id, 1)).Questions.Select(q => q.Question.QuestionNumber).Should().Equal(labels);
        result.Questions[19].Question.Regions.Select(r => r.SourceId).Should().Equal(result.Sources.Select(s => s.Id));
        result.Questions[32].Question.Outcome.Should().Be("unknown");
        result.Questions.Where(q => q.ItemId.HasValue).Select(q => q.Question.QuestionNumber).Should().Equal("20", "40");
        await using var db = database.CreateContext();
        (await db.MistakeItems.Select(i => i.ReviewStatus).ToListAsync()).Should().Equal(1, 1);
    }

    [Fact]
    public async Task AddingQuestion33_KeepsExistingIdentitiesAndReviewAndDoesNotDuplicateOnReplay()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new Analyzer(request => new("test-model", Enumerable.Range(1, 32).Select(n =>
            Question(n == 1 ? "incorrect" : "correct", [new(request.Images[0].SourceId, 0, 0, 1, 1)]) with
            { QuestionNumber = n.ToString(), Statement = $"Question {n}" }).ToList())));
        var job = await Completed(service);
        var existing = job.Questions[0].ItemId!.Value;
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync();
            item.ReviewStatus = 2;
            item.ReviewerId = "teacher";
            await db.SaveChangesAsync();
        }
        var missed = Question("incorrect", [new(job.Sources[0].Id, .5, 0, 1, 1)]) with
        { Id = Guid.NewGuid(), QuestionNumber = "33", Statement = "Question 33" };
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), 1, "Mark missed question 33", [.. job.Questions.Select(q => q.Question), missed]);
        var updated = await service.ReviseAsync(Caller, job.Id, request);
        var replay = await service.ReviseAsync(Caller, job.Id, request);
        var read = await service.GetAsync(Caller, job.Id);

        updated.Version.Should().Be(2);
        replay.Version.Should().Be(2);
        read.Questions.Should().HaveCount(33);
        read.Questions.Take(32).Select(q => (q.Question.Id, q.ItemId)).Should().Equal(job.Questions.Select(q => (q.Question.Id, q.ItemId)));
        var added = read.Questions[32];
        added.Question.Id.Should().Be(missed.Id);
        replay.Questions[32].ItemId.Should().Be(added.ItemId);
        await using var check = database.CreateContext();
        var items = await check.MistakeItems.ToDictionaryAsync(i => i.Id);
        items.Should().HaveCount(2);
        items[existing].ReviewStatus.Should().Be(2);
        items[added.ItemId!.Value].ReviewStatus.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentHostsAndReplay_DoNotDuplicateJobsQuestionsOrItems()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var analyzer = DefaultAnalyzer();
        var first = Service(database, analyzer);
        var second = Service(database, analyzer);
        var upload = Upload();
        var receipts = await Task.WhenAll(first.SubmitAsync(Caller, upload), second.SubmitAsync(Caller, upload));
        receipts[0].Id.Should().Be(receipts[1].Id);
        await Task.WhenAll(first.RunQueuedAsync(default), second.RunQueuedAsync(default));
        var replay = await second.SubmitAsync(Caller, upload);
        replay.Version.Should().Be(1);
        analyzer.Calls.Should().Be(1);
        await using var db = database.CreateContext();
        (await db.ExtractionJobs.CountAsync()).Should().Be(1);
        (await db.ExtractionRevisions.CountAsync()).Should().Be(1);
        (await db.MistakeItems.CountAsync()).Should().Be(1);
        var conflict = await Assert.ThrowsAsync<ExtractionException>(() => first.SubmitAsync(Caller, upload with { StudentId = "different" }));
        conflict.Code.Should().Be("source_conflict");
    }

    [Fact]
    public async Task LostOriginalPut_RetainsBindingAndUnchangedRetryRecovers()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, DefaultAnalyzer());
        database.FailAfterPut = true;
        var upload = Upload();
        await Assert.ThrowsAsync<IOException>(() => service.SubmitAsync(Caller, upload));
        (await service.ListAsync(Caller, null)).Single().Status.Should().Be("Staging");
        var conflict = await Assert.ThrowsAsync<ExtractionException>(() => service.SubmitAsync(Caller, upload with { Grade = 9 }));
        conflict.Code.Should().Be("source_conflict");
        database.FailAfterPut = false;
        var queued = await service.SubmitAsync(Caller, upload);
        await service.RunQueuedAsync(default);
        (await service.GetAsync(Caller, queued.Id)).Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task CallerCorrection_IsAtomicAndVersioned_PreservesPracticeAndRetiresOldImages()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, DefaultAnalyzer());
        var job = await Completed(service);
        var q = job.Questions.Single();
        var itemId = q.ItemId!.Value;
        var practiceId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync();
            item.ReviewStatus = 2; item.RootCause = "teacher cause"; item.ReviewerId = "teacher";
            db.MistakePractices.Add(new() { Id = practiceId, MistakeItemId = itemId, StudentId = "student", PracticeDate = DateTimeOffset.UtcNow, ImagePaths = [] });
            await db.SaveChangesAsync();
        }
        var oldBytes = (await service.ReadCropAsync(Admin, job.Id, q.Question.Id, 1, 0)).Bytes;
        var changed = q.Question with { Regions = [new(job.Sources[0].Id, .5, 0, 1, 1)] };
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), 1, "Fix truncated diagram", [changed]);
        var corrected = await service.ReviseAsync(Caller, job.Id, request);
        corrected.Version.Should().Be(2);
        corrected.Questions.Single().ItemId.Should().Be(itemId);
        (await service.ReviseAsync(Caller, job.Id, request)).Version.Should().Be(2);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.ReadCropAsync(Admin, job.Id, q.Question.Id, 1, 0))).Status.Should().Be(410);
        (await service.ReadCropAsync(Admin, job.Id, q.Question.Id, 2, 0)).Bytes.Should().NotEqual(oldBytes);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.ReviseAsync(Caller, job.Id, request with { RequestKey = Guid.NewGuid() }))).Code.Should().Be("version_conflict");
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync();
            item.ReviewStatus.Should().Be(1);
            item.RootCause.Should().BeNull();
            (await db.MistakePractices.SingleAsync()).Id.Should().Be(practiceId);
            (await db.ProcessLogs.AnyAsync(l => l.Details!.Contains("teacher cause"))).Should().BeTrue();
        }
        var removed = await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 2, "Merged into another collection", []));
        removed.Questions.Should().BeEmpty();
        (await Assert.ThrowsAsync<ExtractionException>(() => service.GetVersionAsync(Admin, job.Id, 1))).Code.Should().Be("version_retired");
        (await service.ReviseAsync(Admin, job.Id, request)).Version.Should().Be(2, "exact request replay returns its accepted version, not latest state");
    }

    [Fact]
    public async Task DeletedItemIsNotRecreatedByCorrectionOrHistoricalRestore()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, DefaultAnalyzer());
        var job = await Completed(service);
        var q = job.Questions.Single();
        await using (var db = database.CreateContext())
        {
            db.MistakeItems.Remove(await db.MistakeItems.SingleAsync());
            await db.SaveChangesAsync();
        }
        var corrected = await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 1, "Restore crop only", [q.Question with { Explanation = "Updated" }]));
        corrected.Questions.Single().ItemDeleted.Should().BeTrue();
        corrected.Questions.Single().ItemId.Should().Be(q.ItemId);
        await using var check = database.CreateContext();
        (await check.MistakeItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task FailedCropResume_ReusesAcceptedLayoutAndModelResult_AndRetryKeyIsIdempotent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var analyzer = DefaultAnalyzer();
        var layout = new LayoutClient();
        var service = Service(database, analyzer, layout);
        var job = await service.SubmitAsync(Caller, Upload());
        database.BeforeUpload = attempt => attempt == 2 ? Task.FromException(new IOException("crop store unavailable")) : Task.CompletedTask;
        await service.RunQueuedAsync(default);
        var failed = await service.GetAsync(Caller, job.Id);
        failed.Status.Should().Be("Failed");
        failed.Version.Should().Be(0);
        var retry = new ExtractionRetryRequest(Guid.NewGuid());
        await service.RetryAsync(Caller, job.Id, retry);
        await service.RetryAsync(Caller, job.Id, retry);
        database.BeforeUpload = _ => Task.CompletedTask;
        await service.RunQueuedAsync(default);
        var recovered = await service.GetAsync(Caller, job.Id);
        recovered.Status.Should().Be("Succeeded");
        recovered.Attempt.Should().Be(2);
        analyzer.Calls.Should().Be(1);
        layout.Calls.Should().Be(1);
        (await service.RetryAsync(Caller, job.Id, retry)).Attempt.Should().Be(2);
    }

    [Fact]
    public async Task LateWorkerCannotPublishAfterItsLeaseWasReplaced()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var analyzer = new Analyzer(async request =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { entered.SetResult(); await release.Task; }
            return new("test-model", [Question(call == 1 ? "incorrect" : "unknown", [new(request.Images[0].SourceId, 0, 0, 1, 1)])]);
        });
        var first = Service(database, analyzer);
        var second = Service(database, analyzer);
        var job = await first.SubmitAsync(Caller, Upload());
        var pending = first.RunQueuedAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using (var db = database.CreateContext())
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_job SET lease_until=clock_timestamp()-interval '1 second' WHERE id={job.Id}");
            await second.RunQueuedAsync(default);
        }
        finally { release.TrySetResult(); }
        await pending;
        var result = await first.GetAsync(Caller, job.Id);
        result.Version.Should().Be(1);
        result.Questions.Single().Question.Outcome.Should().Be("unknown");
        result.Questions.Single().ItemId.Should().BeNull();
        result.Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task CrossPageNumberAndReadingOrder_AreVersionedWithoutChangingQuestionIdentity()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer(request => new("test-model", [Question("incorrect",
            [new(request.Images[1].SourceId, .5, 0, 1, 1), new(request.Images[0].SourceId, .5, 0, 1, 1)])
            with { QuestionNumber = "12" }]));
        var service = Service(database, analyzer);
        var queued = await service.SubmitAsync(Caller, Upload(images: 2));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        job.Status.Should().Be("Succeeded", job.ErrorCode);
        var question = job.Questions.Single();
        question.Images.Select(i => i.SourceId).Should().Equal(job.Sources[1].Id, job.Sources[0].Id);
        var originalParts = new[]
        {
            (await service.ReadCropAsync(Caller, job.Id, question.Question.Id, 1, 0)).Bytes,
            (await service.ReadCropAsync(Caller, job.Id, question.Question.Id, 1, 1)).Bytes
        };
        originalParts[0].Should().NotEqual(originalParts[1]);
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync();
            item.ReviewStatus = 2; item.RootCause = "confirmed cause"; item.ReviewerId = "teacher";
            await db.SaveChangesAsync();
        }
        var renumber = new ExtractionRevisionRequest(Guid.NewGuid(), 1, "Correct printed number",
            [question.Question with { QuestionNumber = "三、2（1）" }]);
        var numbered = await service.ReviseAsync(Admin, job.Id, renumber);
        numbered.Questions.Single().Question.Id.Should().Be(question.Question.Id);
        numbered.Questions.Single().ItemId.Should().Be(question.ItemId);
        numbered.Questions.Single().Images.Select(i => i.Path).Should().Equal(question.Images.Select(i => i.Path));
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync();
            item.ReviewStatus.Should().Be(2, "a printed label correction does not change the solved question");
            item.RootCause.Should().Be("confirmed cause");
        }
        var reordered = await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 2, "Reverse reading order",
            [numbered.Questions.Single().Question with { Regions = question.Question.Regions.AsEnumerable().Reverse().ToList() }]));
        reordered.Version.Should().Be(3);
        reordered.Questions.Single().Question.QuestionNumber.Should().Be("三、2（1）");
        reordered.Questions.Single().Question.Id.Should().Be(question.Question.Id);
        reordered.Questions.Single().ItemId.Should().Be(question.ItemId);
        (await service.ReadCropAsync(Caller, job.Id, question.Question.Id, 3, 0)).Bytes.Should().Equal(originalParts[1]);
        (await service.ReadCropAsync(Caller, job.Id, question.Question.Id, 3, 1)).Bytes.Should().Equal(originalParts[0]);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.GetVersionAsync(Caller, job.Id, 1))).Status.Should().Be(410);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.ReadCropAsync(Caller, job.Id, question.Question.Id, 1, 0))).Status.Should().Be(410);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.GetVersionAsync(Caller, job.Id, 2))).Status.Should().Be(410);
        (await service.ReviseAsync(Admin, job.Id, renumber)).Version.Should().Be(2);
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync();
            item.SourceRegions.Select(r => r.SourceImagePath).Should().Equal(reordered.Questions.Single().Images.Select(i => i.Path));
            item.ReviewStatus.Should().Be(1, "changing content order requires review again");
        }
    }

    [Fact]
    public async Task ManualMergeAndSplit_KeepPrintedLabelsSeparateFromStableIdentity()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer(request => new("test-model", request.Images.Select((image, index) =>
            Question("unknown", [new(image.SourceId, .5, 0, 1, 1)]) with
            { QuestionNumber = "12", Statement = "Uncertain page " + index, StudentAnswer = null }).ToList()));
        var service = Service(database, analyzer);
        var queued = await service.SubmitAsync(Caller, Upload(images: 2));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        job.Status.Should().Be("Succeeded", job.ErrorCode);
        job.Questions.Should().HaveCount(2, "matching printed labels alone cannot merge unrelated content");
        job.Questions.Should().OnlyContain(q => q.ItemId == null);
        var target = job.Questions[0].Question;
        var joined = target with { Statement = "Complete cross-page question",
            Regions = job.Questions.SelectMany(q => q.Question.Regions).ToList() };
        var merged = await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 1, "Confirmed continuation", [joined]));
        merged.Questions.Single().Question.Id.Should().Be(target.Id);
        merged.Questions.Single().Question.QuestionNumber.Should().Be("12");
        merged.Questions.Single().Question.Regions.Select(i => i.SourceId).Should().Equal(job.Sources.Select(s => s.Id));
        merged.Questions.Single().Images.Should().BeEmpty("unknown groups do not generate formal crops");
        var splitId = Guid.NewGuid();
        var split = await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 2, "Separate unrelated second page",
            [joined with { Regions = [joined.Regions[0]] }, joined with { Id = splitId, QuestionNumber = null,
                Statement = "Separate unknown question", Regions = [joined.Regions[1]] }]));
        split.Questions.Select(q => q.Question.Id).Should().Equal(target.Id, splitId);
        split.Questions.Select(q => q.Question.QuestionNumber).Should().Equal("12", null);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.GetVersionAsync(Caller, job.Id, 2))).Status.Should().Be(410);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.GetVersionAsync(Caller, job.Id, 1))).Status.Should().Be(410);
    }

    [Fact]
    public async Task InvalidManualNumbers_DoNotPublishOrMutateHistory_AndUnknownRemainsNull()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, DefaultAnalyzer());
        var job = await Completed(service);
        var draft = job.Questions.Single().Question;
        draft.QuestionNumber.Should().BeNull();
        foreach (var value in new[] { "", " ", " 12", "12 ", "12\n（1）", "12\u007f1", new string('题', 65) })
        {
            var error = await Assert.ThrowsAsync<ExtractionException>(() => service.ReviseAsync(Admin, job.Id,
                new(Guid.NewGuid(), 1, "Invalid label", [draft with { QuestionNumber = value }])));
            error.Code.Should().Be("invalid_questions");
        }
        (await service.GetAsync(Caller, job.Id)).Version.Should().Be(1);
        var accepted = await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 1, "Known label",
            [draft with { QuestionNumber = new string('题', 64) }]));
        accepted.Questions.Single().Question.QuestionNumber.Should().Be(new string('题', 64));
        (await Assert.ThrowsAsync<ExtractionException>(() => service.GetVersionAsync(Caller, job.Id, 1))).Status.Should().Be(410);
    }

    [Fact]
    public async Task NumberlessLegacyRevisionReceipt_KeepsItsPayloadHashAndCanReplayAfterNumbering()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, DefaultAnalyzer());
        var job = await Completed(service);
        var q = job.Questions.Single().Question;
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), 1, "Pre-numbering correction", [q]);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        // This is the exact pre-extension wire shape, without a questionNumber property.
        var legacyBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.RequestKey, request.ExpectedVersion, request.Reason,
            Questions = new[] { new { q.Id, q.Subject, q.Statement, q.StudentAnswer, q.ReferenceAnswer,
                q.Explanation, q.Outcome, q.Evidence, q.Regions } }
        }, json);
        var legacyRequest = System.Text.Json.JsonSerializer.Deserialize<ExtractionRevisionRequest>(legacyBytes, json)!;
        (await service.ReviseAsync(Admin, job.Id, legacyRequest)).Version.Should().Be(2);
        await using (var db = database.CreateContext())
        {
            var receipt = await db.ExtractionRevisions.SingleAsync(r => r.JobId == job.Id && r.Version == 2);
            receipt.PayloadHash.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(legacyBytes)),
                "adding a missing/null optional number must not invalidate historical request receipts");
        }
        await service.ReviseAsync(Admin, job.Id, new(Guid.NewGuid(), 2, "Read printed label", [q with { QuestionNumber = "12" }]));
        var replay = await service.ReviseAsync(Admin, job.Id, legacyRequest);
        replay.Version.Should().Be(2);
        replay.Superseded.Should().BeTrue();
        replay.Questions.Should().BeEmpty();
        (await service.GetAsync(Caller, job.Id)).Questions.Single().Question.QuestionNumber.Should().Be("12");
    }

    private static ExtractionService Service(PostgresTestDatabase database, Analyzer analyzer, LayoutClient? layout = null) =>
        new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(database.Connection).Options,
            database.Oss.Object, analyzer, layout ?? new(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);
    private static async Task<ExtractionJobView> Completed(ExtractionService service)
    {
        var job = await service.SubmitAsync(Caller, Upload());
        await service.RunQueuedAsync(default);
        var result = await service.GetAsync(Caller, job.Id);
        result.Status.Should().Be("Succeeded", result.ErrorCode);
        return result;
    }
    private static ExtractionQuestionDraft Question(string outcome, List<ExtractionRegion> regions) => new()
    {
        Subject = 2, Statement = "1+1", StudentAnswer = "3", ReferenceAnswer = "2",
        Evidence = "Student wrote 3; the sum is 2", Explanation = "Add one and one", Outcome = outcome, Regions = regions
    };
    private static Analyzer DefaultAnalyzer() => new(request => new("test-model", [Question("incorrect", [new(request.Images[0].SourceId, 0, 0, .5, 1)])]));
    private static ExtractionUpload Upload(int images = 1) => new("student", 3, "worksheet", Enumerable.Range(0, images).Select(i =>
    {
        var ppm = System.Text.Encoding.ASCII.GetBytes("P6\n40 20\n255\n");
        var raster = new byte[ppm.Length + 40 * 20 * 3];
        ppm.CopyTo(raster, 0);
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 40; x++)
        {
            var offset = ppm.Length + (y * 40 + x) * 3;
            if (x < 20) raster[offset] = 255;
            else raster[offset + (i == 0 ? 2 : 1)] = 255;
        }
        using var image = new MagickImage(raster);
        return new ExtractionImage { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(MagickFormat.Png) };
    }).ToList());
    private sealed class Analyzer : IQuestionAnalysisClient
    {
        private readonly Func<QuestionAnalysisRequest, Task<QuestionAnalysisResult>> _run;
        public int Calls;
        internal Analyzer(Func<QuestionAnalysisRequest, QuestionAnalysisResult> run) : this(r => Task.FromResult(run(r))) { }
        internal Analyzer(Func<QuestionAnalysisRequest, Task<QuestionAnalysisResult>> run) => _run = run;
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); return _run(request); }
    }
    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public int Calls;
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            using var decoded = new MagickImage(image.Bytes);
            return Task.FromResult(new LayoutDetectionResult { RequestId = "test-layout", Width = (int)decoded.Width,
                Height = (int)decoded.Height, Regions = [new(0, "question", new(0, 0, 1, 1))] });
        }
    }
}
