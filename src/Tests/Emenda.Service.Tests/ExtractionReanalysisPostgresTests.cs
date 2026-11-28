using System.Text.Json;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Emenda.Service.Tests;

/// <summary>
/// Two pages. Question 12 (incorrect, math) is the top half of page 1, question 14 (correct, math) the third quarter of
/// page 1, question 13 (incorrect, math) the top half of page 2. The bottom of both pages is free.
/// </summary>
public sealed class ExtractionReanalysisPostgresTests
{
    private static readonly ExtractionAccess Caller = new("reanalysis-tests", "learner");
    private static readonly List<int> Math = [2];

    [Fact]
    public async Task Continuation_UpdatesOnlyTheAffectedQuestion_InPlace_AndReplaysWithoutAnotherModelCall()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var (q12, q14, q13) = (job.Questions[0], job.Questions[1], job.Questions[2]);
        var page2 = job.Sources[1].Id;
        // The model returns only question 12, continued at the bottom of page 2; the omitted questions stay.
        analyzer.Revise = context => [context[0] with { StudentAnswer = "3, then 5", Regions = [context[0].Regions[0], new(page2, 0, .6, 1, 1)] }];
        var request = new ExtractionReanalysisRequest(Guid.NewGuid(), job.Version, "  第二页是第一页的续答。  ", [2, 2]);

        var receipt = await service.ReanalyzeAsync(Caller, job.Id, request);

        Assert.Equal(new ExtractionReanalysisView(job.Id, job.StudentId, request.RequestKey, job.Version + 1), receipt);
        var sent = Assert.Single(analyzer.Requests);
        Assert.Equal("all", sent.Revision!.Scope);
        Assert.Equal(ExtractionService.ReanalysisGuidance + "第二页是第一页的续答。", sent.Revision.Instruction);
        Assert.Equal(job.Sources.Select(s => s.Id), sent.Images.Select(i => i.SourceId));
        Assert.Equal(job.Questions.Select(q => q.Question.Id), sent.Revision.Questions.Select(q => q.Id));
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(new[] { q12.Question.Id, q14.Question.Id, q13.Question.Id }, read.Questions.Select(q => q.Question.Id));
        var updated = read.Questions[0];
        Assert.Equal((q12.ItemId, "3, then 5"), (updated.ItemId, updated.Question.StudentAnswer));
        Assert.Equal(new[] { job.Sources[0].Id, page2 }, updated.Images.Select(i => i.SourceId));
        Assert.Equal(JsonSerializer.Serialize(new[] { q14, q13 }), JsonSerializer.Serialize(read.Questions.Skip(1)));
        foreach (var source in job.Sources) Assert.True(await db.Files.ObjectExistsAsync(source.Path));
        await using (var context = db.CreateContext())
        {
            var item = await context.MistakeItems.SingleAsync(i => i.Id == q12.ItemId);
            Assert.Equal((int)ReviewStatus.PendingReview, item.ReviewStatus);
            Assert.Equal(updated.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
            Assert.Equal(2, await context.MistakeItems.CountAsync());
        }

        Assert.Equal(receipt, await service.ReanalyzeAsync(Caller, job.Id, request with { Instruction = "第二页是第一页的续答。" }));
        Assert.Single(analyzer.Requests);
        Assert.Equal(409, (await Assert.ThrowsAsync<ExtractionException>(() => service.ReanalyzeAsync(Caller, job.Id,
            request with { Instruction = "第二页不是续答" }))).Status);
    }

    [Fact]
    public async Task MissedMistake_CreatesOnePendingItem_AndAnExistingQuestionIsReusedByItsId()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var q14 = job.Questions[1];
        var page1 = job.Sources[0].Id;
        analyzer.Revise = context => [Question("15", page1, .8, 1) with { Id = Guid.Empty }];

        var added = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "第 15 题也是错题", Math));

        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(new[] { "12", "14", "15", "13" }, read.Questions.Select(q => q.Question.QuestionNumber));
        var q15 = read.Questions[2];
        Assert.DoesNotContain(q15.Question.Id, job.Questions.Select(q => q.Question.Id));
        Assert.NotNull(q15.ItemId);
        Assert.Single(q15.Images);

        // Question 14 already exists as correct: the model names it by its ID, and it becomes a pending mistake in place.
        analyzer.Revise = context => [context[1] with { Outcome = "incorrect", Evidence = "The sign is wrong" }];
        var reused = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), added.Version, "第 14 题也是错题", Math));

        Assert.Equal(added.Version + 1, reused.Version);
        read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(4, read.Questions.Count);
        var becameMistake = read.Questions.Single(q => q.Question.Id == q14.Question.Id);
        Assert.NotNull(becameMistake.ItemId);
        await using var context = db.CreateContext();
        Assert.Equal(4, await context.MistakeItems.CountAsync(i => i.ReviewStatus == (int)ReviewStatus.PendingReview));
    }

    [Fact]
    public async Task RemovedQuestion_IsFoundAgainAfterItsFilesWereReclaimed_AsOneNewPendingMistake()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var q13 = job.Questions[2];
        var removed = await service.RemoveMistakeAsync(Caller, job.Id, q13.Question.Id,
            new(job.Version, q13.ItemId!.Value, (int)ReviewStatus.PendingReview));
        Assert.False(await db.Files.ObjectExistsAsync(q13.Images[0].Path));
        analyzer.Revise = context => [q13.Question with { Id = Guid.Empty }];

        var receipt = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), removed.Version, "刚才误点了，第 13 题实际是错题", Math));

        Assert.Equal(removed.Version + 1, receipt.Version);
        Assert.Equal(new[] { "12", "14" }, analyzer.Requests.Single().Revision!.Questions.Select(q => q.QuestionNumber));
        var again = (await service.GetAsync(Caller, job.Id)).Questions.Single(q => q.Question.QuestionNumber == "13");
        Assert.NotEqual(q13.Question.Id, again.Question.Id);
        Assert.NotEqual(q13.ItemId, again.ItemId);
        Assert.Equal(q13.Question.Regions, again.Question.Regions);
        Assert.True(await db.Files.ObjectExistsAsync(Assert.Single(again.Images).Path), "recropped from the retained original");
        await using var context = db.CreateContext();
        var item = await context.MistakeItems.SingleAsync(i => i.QuestionId == again.Question.Id.ToString("D"));
        Assert.Equal((int)ReviewStatus.PendingReview, item.ReviewStatus);
        Assert.Equal(2, await context.MistakeItems.CountAsync());
    }

    [Fact]
    public async Task ConfirmedAndOtherSubjectQuestions_NeverChange_AndANoOpKeepsTheVersionAndReplays()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var q12 = job.Questions[0];
        await Confirm(db, q12.ItemId!.Value);
        var page1 = job.Sources[0].Id;
        // The model also restates the confirmed question with a moved box and new text: that part is ignored.
        analyzer.Revise = context => [context[0] with { Statement = "Rewritten", Regions = [new(page1, 0, 0, 1, .45)] },
            Question("15", page1, .8, 1) with { Id = Guid.Empty }];

        var receipt = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "第二页是续答，还有第 15 题", Math));

        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(job.Version + 1, receipt.Version);
        Assert.Equal(JsonSerializer.Serialize(q12), JsonSerializer.Serialize(read.Questions[0]));
        Assert.Contains(read.Questions, q => q.Question.QuestionNumber == "15");
        await using (var context = db.CreateContext())
        {
            var item = await context.MistakeItems.SingleAsync(i => i.Id == q12.ItemId);
            Assert.Equal((ReviewStatus.Confirmed, "teacher cause"), ((ReviewStatus)item.ReviewStatus, item.RootCause));
        }
        Assert.True(await db.Files.ObjectExistsAsync(q12.Images[0].Path));

        // Only English may be edited: every math question is context, so the result changes nothing.
        analyzer.Revise = context => [context[3] with { Statement = "Question 13 rewritten" }];
        var english = new ExtractionReanalysisRequest(Guid.NewGuid(), receipt.Version, "第 13 题答案看错了", [3]);
        var unchanged = await service.ReanalyzeAsync(Caller, job.Id, english);

        Assert.Equal(new ExtractionReanalysisView(job.Id, job.StudentId, english.RequestKey, receipt.Version), unchanged);
        Assert.Equal(JsonSerializer.Serialize(read.Questions), JsonSerializer.Serialize((await service.GetAsync(Caller, job.Id)).Questions));
        Assert.Equal(unchanged, await service.ReanalyzeAsync(Caller, job.Id, english));
        Assert.Equal(2, analyzer.Requests.Count);
        Assert.Equal(409, (await Assert.ThrowsAsync<ExtractionException>(() => service.ReanalyzeAsync(Caller, job.Id,
            english with { AllowedSubjects = [2, 3] }))).Status);
        Assert.Empty(await service.ListProposalsAsync(Caller, job.Id));
    }

    public static TheoryData<string, int, string> RejectedOutputs => new()
    {
        { "withdraw", 422, "reanalysis_out_of_scope" },
        { "subject", 422, "reanalysis_out_of_scope" },
        { "new-subject", 422, "reanalysis_out_of_scope" },
        { "duplicate", 422, "clarification_required" },
        { "same-content", 422, "clarification_required" },
        { "encroach", 422, "clarification_required" },
        { "foreign", 502, "invalid_analysis_response" },
        { "repeated", 502, "invalid_analysis_response" },
        { "invalid", 502, "invalid_analysis_response" },
        { "model-failure", 502, "invalid_analysis_response" },
    };

    [Theory]
    [MemberData(nameof(RejectedOutputs))]
    public async Task OutputTheRulesReject_LeavesTheCurrentResultIntact(string output, int status, string code)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var (page1, page2) = (job.Sources[0].Id, job.Sources[1].Id);
        analyzer.Revise = context => output switch
        {
            // "第 12 题其实是对的" withdraws a pending mistake: that is "不是错题", not reanalysis.
            "withdraw" => [context[0] with { Outcome = "correct" }],
            "subject" => [context[0] with { Subject = 3 }],
            "new-subject" => [Question("15", page1, .8, 1) with { Id = Guid.Empty, Subject = 3 }],
            // Question 14 found again as a new question over most of its region.
            "duplicate" => [Question("14b", page1, .52, .76) with { Id = Guid.Empty }],
            "same-content" => [context[2] with { Id = Guid.Empty }],
            // Question 12 continued onto page 2 reaches 40% into question 13.
            "encroach" => [context[0] with { Regions = [context[0].Regions[0], new(page2, 0, .3, 1, 1)] }],
            "foreign" => [context[0] with { Id = Guid.NewGuid() }],
            "repeated" => [context[0], context[0] with { StudentAnswer = "4" }],
            "invalid" => [context[0] with { Regions = [new(page1, .5, 0, .4, 1)] }],
            _ => throw new ExtractionException("invalid_analysis_response", 502),
        };

        var key = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<ExtractionException>(() =>
            service.ReanalyzeAsync(Caller, job.Id, new(key, job.Version, "请重新看一下", Math)));

        Assert.Equal((status, code), (error.Status, error.Code));
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(job.Version, read.Version);
        Assert.Equal(JsonSerializer.Serialize(job.Questions), JsonSerializer.Serialize(read.Questions));
        await using var context = db.CreateContext();
        Assert.Equal(2, await context.MistakeItems.CountAsync(i => i.ReviewStatus == (int)ReviewStatus.PendingReview));
        Assert.False(await context.ExtractionObjects.AnyAsync(o => o.Path.Contains(key.ToString("N"))));
    }

    [Fact]
    public async Task QuestionTheHintMakesUnknown_IsCollectedAsAnOrdinaryMistake_AndARequestOnItIsDropped()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        Assert.Null(job.Questions[1].ItemId);
        analyzer.Revise = context => [context[1] with { Outcome = "unknown", StudentAnswer = null, Evidence = "", IncludedByRequest = true }];

        var receipt = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "第 14 题看不清，也收进来", Math));

        Assert.Equal(job.Version + 1, receipt.Version);
        var view = (await service.GetAsync(Caller, job.Id)).Questions[1];
        Assert.Equal(("unknown", false), (view.Question.Outcome, view.Question.IncludedByRequest));
        Assert.NotNull(view.ItemId);
        Assert.True(await db.Files.ObjectExistsAsync(Assert.Single(view.Images).Path));
        await using var context = db.CreateContext();
        Assert.Equal((int)ReviewStatus.PendingReview, (await context.MistakeItems.SingleAsync(i => i.Id == view.ItemId)).ReviewStatus);
    }

    [Fact]
    public async Task CorrectQuestionCollectedOnRequest_KeepsItsJudgment_GetsCropsAndAPendingItem_AndStaysCollected()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var q14 = job.Questions[1];
        Assert.Null(q14.ItemId);
        analyzer.Revise = context => [context[1] with { IncludedByRequest = true }];

        var collected = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "第 14 题做对了，我也想收录", Math));

        Assert.Equal(job.Version + 1, collected.Version);
        var read = await service.GetAsync(Caller, job.Id);
        var view = read.Questions[1];
        Assert.Equal((q14.Question.Id, "correct", true), (view.Question.Id, view.Question.Outcome, view.Question.IncludedByRequest));
        Assert.Equal(q14.Question.Evidence, view.Question.Evidence);
        Assert.NotNull(view.ItemId);
        Assert.True(await db.Files.ObjectExistsAsync(Assert.Single(view.Images).Path));
        var json = JsonSerializer.Serialize(read.Questions, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"includedByRequest\":true"));
        Assert.DoesNotContain("\"includedByRequest\":false", json);
        await using (var context = db.CreateContext())
        {
            var item = await context.MistakeItems.SingleAsync(i => i.Id == view.ItemId);
            Assert.Equal((int)ReviewStatus.PendingReview, item.ReviewStatus);
            Assert.Equal(view.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
            using var analysis = JsonDocument.Parse(MistakeSource.Analysis(item)!);
            Assert.Equal(q14.Question.Evidence, analysis.RootElement.GetProperty("evidence").GetString());
        }

        // A later hint that restates question 14 without the request keeps it collected.
        analyzer.Revise = context => [context[1] with { IncludedByRequest = false, Explanation = "Restated" }];
        var restated = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), collected.Version, "第 14 题的解析再写清楚一点", Math));
        var kept = (await service.GetAsync(Caller, job.Id)).Questions[1];
        Assert.Equal((restated.Version, true, view.ItemId, "Restated"),
            (collected.Version + 1, kept.Question.IncludedByRequest, kept.ItemId, kept.Question.Explanation));

        // An adjustment keeps its judgment and the request; "不是错题" does not apply to a correct question.
        analyzer.Revise = context => [context[0] with { IncludedByRequest = false, Regions = [context[0].Regions[0] with { Y1 = .74 }] }];
        var adjusted = await service.AdjustQuestionAsync(Caller, job.Id, q14.Question.Id,
            new(Guid.NewGuid(), restated.Version, view.ItemId!.Value, "裁小一点"));
        Assert.True(adjusted.Question!.Question.IncludedByRequest);
        analyzer.Revise = context => [context[0] with { Outcome = "incorrect" }];
        Assert.Equal((422, "adjustment_out_of_scope"), await Code(() => service.AdjustQuestionAsync(Caller, job.Id, q14.Question.Id,
            new(Guid.NewGuid(), adjusted.Version, view.ItemId!.Value, "判错了"))));
        Assert.Equal((409, "review_conflict"), await Code(() => service.RemoveMistakeAsync(Caller, job.Id, q14.Question.Id,
            new(adjusted.Version, view.ItemId!.Value, (int)ReviewStatus.PendingReview))));
    }

    [Fact]
    public async Task QuestionUncollectedByAPut_IsCollectedAgainWithItsOwnItem_AndARequestOnAnIncorrectOneIsDropped()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var q12 = job.Questions[0];
        // Marking question 12 correct rejects its item without a reviewer.
        var corrected = await service.ReviseQuestionAsync(Caller, job.Id, q12.Question.Id, new(Guid.NewGuid(), job.Version, "判对了",
            q12.Question with { Outcome = "correct", StudentAnswer = "2", Evidence = "Visible answer 2 matches" }));
        Assert.Empty(corrected.Question!.Images);
        var page1 = job.Sources[0].Id;
        analyzer.Revise = context => [context[0] with { IncludedByRequest = true },
            Question("15", page1, .8, 1) with { Id = Guid.Empty, IncludedByRequest = true }];

        var receipt = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), corrected.Version, "第 12 题也收录，还有第 15 题", Math));

        Assert.Equal(corrected.Version + 1, receipt.Version);
        var read = await service.GetAsync(Caller, job.Id);
        var again = read.Questions[0];
        Assert.Equal((q12.ItemId, "correct", true), (again.ItemId, again.Question.Outcome, again.Question.IncludedByRequest));
        Assert.Single(again.Images);
        var q15 = read.Questions.Single(q => q.Question.QuestionNumber == "15");
        Assert.Equal(("incorrect", false), (q15.Question.Outcome, q15.Question.IncludedByRequest));
        await using var context = db.CreateContext();
        Assert.Equal((int)ReviewStatus.PendingReview, (await context.MistakeItems.SingleAsync(i => i.Id == q12.ItemId)).ReviewStatus);

        // A reviewer's rejection is a decision this command keeps.
        await using (var reject = db.CreateContext())
        {
            var item = await reject.MistakeItems.SingleAsync(i => i.Id == q12.ItemId);
            item.ReviewStatus = (int)ReviewStatus.Rejected;
            item.ReviewedAt = DateTimeOffset.UtcNow;
            await reject.SaveChangesAsync();
        }
        analyzer.Revise = context => [context[0] with { Explanation = "Teacher rejected; model restates" }];
        Assert.Equal(receipt.Version, (await service.ReanalyzeAsync(Caller, job.Id,
            new(Guid.NewGuid(), receipt.Version, "第 12 题再看看", Math))).Version);
    }

    private static async Task<(int, string)> Code(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<ExtractionException>(call);
        return (error.Status, error.Code);
    }

    [Theory]
    [InlineData("confirmed", "review_conflict")]
    [InlineData("removed", "version_conflict")]
    public async Task ConfirmationOrRemovalWhileTheModelRuns_RejectsWithoutWriting(string change, string code)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var target = job.Questions[0];
        analyzer.Revise = context => [context[0] with { Regions = [context[0].Regions[0] with { Y1 = .45 }] }];
        analyzer.BeforeRevision = change == "confirmed" ? () => Confirm(db, target.ItemId!.Value)
            : () => service.RemoveMistakeAsync(Caller, job.Id, job.Questions[2].Question.Id,
                new(job.Version, job.Questions[2].ItemId!.Value, (int)ReviewStatus.PendingReview));

        var key = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<ExtractionException>(() =>
            service.ReanalyzeAsync(Caller, job.Id, new(key, job.Version, "第 12 题裁小一点", Math)));

        Assert.Equal((409, code), (error.Status, error.Code));
        await using var context = db.CreateContext();
        Assert.False(await context.ExtractionRevisions.AnyAsync(r => r.RequestKey == key));
        Assert.False(await context.ExtractionObjects.AnyAsync(o => o.Path.Contains(key.ToString("N"))),
            "crops stored for the rejected publication are reclaimed at once");
        var item = await context.MistakeItems.SingleAsync(i => i.Id == target.ItemId);
        Assert.Equal(target.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
        if (change == "confirmed") Assert.Equal((int)ReviewStatus.Confirmed, item.ReviewStatus);
    }

    [Fact]
    public async Task UploadWithNoMistakes_CanGainOne()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer { Initial = page => [Question("14", page[0], .5, .75) with { Outcome = "correct" },
            Question("16", page[1], 0, .5) with { Outcome = "correct" }] };
        var service = Service(db, analyzer);
        var job = await Completed(service, expected: 2);
        analyzer.Revise = _ => [Question("15", job.Sources[0].Id, .8, 1) with { Id = Guid.Empty }];

        var receipt = await service.ReanalyzeAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "第 15 题也是错题", Math));

        Assert.Equal(job.Version + 1, receipt.Version);
        await using var context = db.CreateContext();
        Assert.Equal(1, await context.MistakeItems.CountAsync(i => i.ReviewStatus == (int)ReviewStatus.PendingReview));
    }

    [Fact]
    public async Task InvalidStaleOrForeignRequests_AreRejectedBeforeInference()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var valid = new ExtractionReanalysisRequest(Guid.NewGuid(), job.Version, "第 15 题也是错题", Math);
        async Task<(int, string)> Reject(ExtractionReanalysisRequest request, ExtractionAccess? access = null)
        {
            var error = await Assert.ThrowsAsync<ExtractionException>(() => service.ReanalyzeAsync(access ?? Caller, job.Id, request));
            return (error.Status, error.Code);
        }

        Assert.Equal((400, "invalid_request"), await Reject(valid with { Instruction = "  " }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { Instruction = new string('题', 2001) }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { RequestKey = Guid.Empty }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { ExpectedVersion = 0 }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { AllowedSubjects = [] }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { AllowedSubjects = [10] }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { AllowedSubjects = null! }));
        Assert.Equal((409, "version_conflict"), await Reject(valid with { ExpectedVersion = job.Version + 1 }));
        Assert.Equal((404, "not_found"), await Reject(valid, new("foreign", "foreign")));
        Assert.Empty(analyzer.Requests);
    }

    private static async Task Confirm(PostgresTestDatabase db, Guid itemId)
    {
        await using var context = db.CreateContext();
        var item = await context.MistakeItems.SingleAsync(i => i.Id == itemId);
        item.ReviewStatus = (int)ReviewStatus.Confirmed;
        item.ReviewerId = "teacher";
        item.RootCause = "teacher cause";
        await context.SaveChangesAsync();
    }

    private static ExtractionService Service(PostgresTestDatabase db, Analyzer analyzer) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, analyzer,
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    private static async Task<ExtractionJobView> Completed(ExtractionService service, int expected = 3)
    {
        var queued = await service.SubmitAsync(Caller, new("student", 4, "two-pages", [Image(1), Image(2)]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        Assert.Equal(("Succeeded", null), (job.Status, job.ErrorCode));
        Assert.Equal(expected, job.Questions.Count);
        return job;
    }

    private static ExtractionImage Image(int n)
    {
        using var image = new ImageMagick.MagickImage(ExtractionHttpPostgresTests.TestPng());
        image.Resize(20, 10);
        image.Colorize(new ImageMagick.MagickColor((byte)(n * 40), 20, 40), new ImageMagick.Percentage(100));
        return new() { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(ImageMagick.MagickFormat.Png) };
    }

    private static ExtractionQuestionDraft Question(string number, Guid source, double y0, double y1) => new()
    {
        QuestionNumber = number, Subject = 2, Grade = 4, Statement = "Question " + number, StudentAnswer = "3", ReferenceAnswer = "2",
        Explanation = "2 is correct", Evidence = "Visible answer 3 differs from 2", Outcome = "incorrect",
        Regions = [new(source, 0, y0, 1, y1)]
    };

    private sealed class Analyzer : IQuestionAnalysisClient
    {
        public Func<List<Guid>, List<ExtractionQuestionDraft>> Initial { get; init; } = pages =>
            [Question("12", pages[0], 0, .5), Question("14", pages[0], .5, .75) with { Outcome = "correct" }, Question("13", pages[1], 0, .5)];
        public Func<List<ExtractionQuestionDraft>, List<ExtractionQuestionDraft>> Revise { get; set; } = _ => [];
        public Func<Task>? BeforeRevision { get; set; }
        public List<QuestionAnalysisRequest> Requests { get; } = [];

        public async Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            if (request.Revision is not { } revision)
                return new("controlled", Initial(request.Images.Select(i => i.SourceId).ToList()));
            Requests.Add(request);
            if (BeforeRevision is not null) await BeforeRevision();
            return new("controlled", Revise(revision.Questions));
        }
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "controlled", Width = 20, Height = 10,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
