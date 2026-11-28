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

public sealed class ExtractionAdjustmentPostgresTests
{
    private static readonly ExtractionAccess Caller = new("adjustment-tests", "learner");

    [Fact]
    public async Task ExtendsSelectedQuestionOntoAnotherPage_InPlace_AndReplaysWithoutResettingConfirmation()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0], job.Questions[1]);
        var page2 = job.Sources[1].Id;
        analyzer.Revise = q => [q with { Statement = "Question 12 with check", Regions = [q.Regions[0] with { Y1 = .45 }, new(page2, 0, .5, 1, 1)] }];
        var request = new ExtractionAdjustmentRequest(Guid.NewGuid(), job.Version, q12.ItemId!.Value, "  请把第二页最后两行验算也裁进来  ");

        var result = await service.AdjustQuestionAsync(Caller, job.Id, q12.Question.Id, request);

        Assert.Equal(job.Version + 1, result.Version);
        var adjusted = Assert.IsType<ExtractionQuestionView>(result.Question);
        Assert.Equal(q12.Question.Id, adjusted.Question.Id);
        Assert.Equal(q12.ItemId, adjusted.ItemId);
        Assert.Equal("12", adjusted.Question.QuestionNumber);
        Assert.Equal(new[] { job.Sources[0].Id, page2 }, adjusted.Images.Select(i => i.SourceId));
        Assert.Equal("请把第二页最后两行验算也裁进来", analyzer.Instructions.Single());
        Assert.Equal(job.Sources.Count, analyzer.RevisionImageCount);
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(JsonSerializer.Serialize(q13), JsonSerializer.Serialize(read.Questions[1]));
        Assert.False(await db.Files.ObjectExistsAsync(q12.Images[0].Path), "the superseded crop has no remaining owner");
        foreach (var crop in q13.Images.Concat(adjusted.Images)) Assert.True(await db.Files.ObjectExistsAsync(crop.Path));
        foreach (var source in job.Sources) Assert.True(await db.Files.ObjectExistsAsync(source.Path));
        await using (var context = db.CreateContext())
        {
            var item = await context.MistakeItems.SingleAsync(i => i.Id == q12.ItemId);
            Assert.Equal((int)ReviewStatus.PendingReview, item.ReviewStatus);
            Assert.Equal(adjusted.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
            using var analysis = JsonDocument.Parse(MistakeSource.Analysis(item)!);
            Assert.Equal("Question 12 with check", analysis.RootElement.GetProperty("statement").GetString());
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            item.ReviewerId = "teacher";
            item.RootCause = "confirmed after adjustment";
            await context.SaveChangesAsync();
        }

        var replay = await service.AdjustQuestionAsync(Caller, job.Id, q12.Question.Id, request);
        Assert.Equal(result.Version, replay.Version);
        Assert.Equal(1, analyzer.Revisions);
        await using (var context = db.CreateContext())
        {
            Assert.Equal((int)ReviewStatus.Confirmed, (await context.MistakeItems.SingleAsync(i => i.Id == q12.ItemId)).ReviewStatus);
            Assert.Equal(result.Version, (await context.ExtractionJobs.SingleAsync()).Version);
        }
        Assert.Equal(409, (await Assert.ThrowsAsync<ExtractionException>(() => service.AdjustQuestionAsync(Caller, job.Id,
            q12.Question.Id, request with { Instruction = "different" }))).Status);
    }

    [Theory]
    [InlineData("confirmed", "review_conflict")]
    [InlineData("removed", "item_binding_conflict")]
    public async Task TargetConfirmedOrRemovedWhileModelRuns_RejectsWithoutWriting(string change, string code)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var target = job.Questions[0];
        analyzer.Revise = q => [q with { Regions = [q.Regions[0] with { Y1 = .45 }] }];
        analyzer.BeforeRevision = async () =>
        {
            await using var context = db.CreateContext();
            var item = await context.MistakeItems.SingleAsync(i => i.Id == target.ItemId);
            if (change == "removed") context.MistakeItems.Remove(item);
            else
            {
                item.ReviewStatus = (int)ReviewStatus.Confirmed;
                item.ReviewerId = "teacher";
                item.RootCause = "teacher cause";
            }
            await context.SaveChangesAsync();
        };

        var error = await Assert.ThrowsAsync<ExtractionException>(() => service.AdjustQuestionAsync(Caller, job.Id, target.Question.Id,
            new(Guid.NewGuid(), job.Version, target.ItemId!.Value, "裁大一点")));

        Assert.Equal((409, code), (error.Status, error.Code));
        await using var check = db.CreateContext();
        Assert.Equal(job.Version, (await check.ExtractionJobs.SingleAsync()).Version);
        Assert.Equal(job.Version, await check.ExtractionRevisions.CountAsync());
        if (change == "confirmed")
        {
            var item = await check.MistakeItems.SingleAsync(i => i.Id == target.ItemId);
            Assert.Equal((int)ReviewStatus.Confirmed, item.ReviewStatus);
            Assert.Equal("teacher cause", item.RootCause);
            Assert.Equal(target.Images.Select(i => i.Path), item.SourceRegions.Select(r => r.SourceImagePath));
            Assert.True(await db.Files.ObjectExistsAsync(target.Images[0].Path));
        }
    }

    public static TheoryData<string, string> OutOfScopeOutputs => new()
    {
        { "both", "adjustment_out_of_scope" },
        { "split", "adjustment_out_of_scope" },
        { "subject", "adjustment_out_of_scope" },
        { "outcome", "adjustment_out_of_scope" },
        { "merge", "adjustment_out_of_scope" },
        { "encroach", "clarification_required" },
    };

    [Theory]
    [MemberData(nameof(OutOfScopeOutputs))]
    public async Task OutputBeyondTheSelectedQuestion_PublishesNothing(string output, string code)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var (q12, q13) = (job.Questions[0].Question, job.Questions[1].Question);
        var page2 = job.Sources[1].Id;
        analyzer.Revise = q => output switch
        {
            "both" => [q with { Regions = [q.Regions[0], new(page2, 0, .5, 1, 1)] }, q13 with { Regions = [new(page2, 0, 0, 1, .4)] }],
            "split" => [q, q with { Id = Guid.Empty, Statement = "Second half", Regions = [new(page2, 0, .5, 1, 1)] }],
            "subject" => [q with { Subject = 3 }],
            "outcome" => [q with { Outcome = "correct" }],
            "merge" => [q13 with { Id = q.Id }],
            // Reaches 40% into question 13's region on page 2: "裁到第 13 题题干之前" was not honoured.
            _ => [q with { Regions = [q.Regions[0], new(page2, 0, .3, 1, 1)] }],
        };

        var error = await Assert.ThrowsAsync<ExtractionException>(() => service.AdjustQuestionAsync(Caller, job.Id, q12.Id,
            new(Guid.NewGuid(), job.Version, job.Questions[0].ItemId!.Value, "请把第二页最后两行也裁进来")));

        Assert.Equal((422, code), (error.Status, error.Code));
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(job.Version, read.Version);
        Assert.Equal(JsonSerializer.Serialize(job.Questions), JsonSerializer.Serialize(read.Questions));
        await using var check = db.CreateContext();
        Assert.Equal(2, await check.MistakeItems.CountAsync(i => i.ReviewStatus == (int)ReviewStatus.PendingReview));
    }

    [Fact]
    public async Task InvalidStaleForeignOrNonPendingTargets_AreRejectedBeforeInference()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var target = job.Questions[0];
        var valid = new ExtractionAdjustmentRequest(Guid.NewGuid(), job.Version, target.ItemId!.Value, "裁大一点");
        async Task<(int, string)> Reject(ExtractionAdjustmentRequest request, ExtractionAccess? access = null, Guid? questionId = null)
        {
            var error = await Assert.ThrowsAsync<ExtractionException>(() =>
                service.AdjustQuestionAsync(access ?? Caller, job.Id, questionId ?? target.Question.Id, request));
            return (error.Status, error.Code);
        }

        Assert.Equal((400, "invalid_request"), await Reject(valid with { Instruction = "   " }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { Instruction = new string('题', 2001) }));
        Assert.Equal((400, "invalid_request"), await Reject(valid with { RequestKey = Guid.Empty }));
        Assert.Equal((409, "version_conflict"), await Reject(valid with { ExpectedVersion = job.Version + 1 }));
        Assert.Equal((404, "not_found"), await Reject(valid, new("foreign", "foreign")));
        Assert.Equal((404, "not_found"), await Reject(valid, questionId: Guid.NewGuid()));
        Assert.Equal((409, "item_binding_conflict"), await Reject(valid with { ItemId = job.Questions[1].ItemId!.Value }));
        await using (var context = db.CreateContext())
        {
            var item = await context.MistakeItems.SingleAsync(i => i.Id == target.ItemId);
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            await context.SaveChangesAsync();
        }
        Assert.Equal((409, "review_conflict"), await Reject(valid));
        Assert.Equal(0, analyzer.Revisions);
        Assert.Equal(job.Version, (await service.GetAsync(Caller, job.Id)).Version);
    }

    private static ExtractionService Service(PostgresTestDatabase db, Analyzer analyzer) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, analyzer,
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    private static async Task<ExtractionJobView> Completed(ExtractionService service)
    {
        var queued = await service.SubmitAsync(Caller, new("student", 4, "two-pages", [Image(1), Image(2)]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(new[] { "12", "13" }, job.Questions.Select(q => q.Question.QuestionNumber));
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
        public Func<ExtractionQuestionDraft, List<ExtractionQuestionDraft>> Revise { get; set; } = q => [q];
        public Func<Task>? BeforeRevision { get; set; }
        public int Revisions { get; private set; }
        public int RevisionImageCount { get; private set; }
        public List<string> Instructions { get; } = [];

        public async Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            if (request.Revision is { } revision)
            {
                Revisions++;
                RevisionImageCount = request.Images.Count;
                Instructions.Add(revision.Instruction);
                if (BeforeRevision is not null) await BeforeRevision();
                return new("controlled", Revise(Assert.Single(revision.Questions)));
            }
            ExtractionQuestionDraft Question(string number, int page) => new()
            { QuestionNumber = number, Subject = 2, Statement = "Question " + number, StudentAnswer = "3", ReferenceAnswer = "2",
                Explanation = "2 is correct", Evidence = "Visible answer 3 differs from 2", Outcome = "incorrect",
                Regions = [new(request.Images[page].SourceId, 0, 0, 1, .5)] };
            return new("controlled", [Question("12", 0), Question("13", 1)]);
        }
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "controlled", Width = 20, Height = 10,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
