using Emenda.Database;
using Emenda.Database.Repositories;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Emenda.TestFixtures;
using FluentAssertions;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Emenda.Service.Tests;

public sealed class ExtractionAnalysisPostgresTests
{
    private static readonly ExtractionAccess Caller = new("school", "school");
    private static readonly ExtractionAccess Admin = new("admin", "reviewer", true);

    [Fact]
    public async Task ContentOnlyCorrection_InvalidatesRunningInputAndDraft_ButRetainsEvidenceForReanalysis()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var extraction = Service(database);
        var job = await CompletedAsync(extraction);
        var question = Assert.Single(job.Questions);
        var itemId = question.ItemId!.Value;
        var originalCrop = await extraction.ReadCropAsync(Admin, job.Id, question.Question.Id, job.Version, 0);
        await using var analysisDb = database.CreateContext();
        await using var workerDb = database.CreateContext();
        var analysis = new MistakeAnalysisRepository(analysisDb);
        var worker = new ReanalyzeJobRepository(workerDb);
        var first = await StartAndClaimAsync(analysis, worker, itemId, "The student wrote three");
        Assert.True(await CompleteAsync(worker, first, "Confirmed original report"));
        var original = (await analysis.GetAsync(itemId))!;
        await analysis.ConfirmAsync(itemId, original.Revision, "teacher");
        var second = await StartAndClaimAsync(analysis, worker, itemId, "Check the operation sign");
        Assert.True(await CompleteAsync(worker, second, "Unconfirmed original draft"));
        var stale = await StartAndClaimAsync(analysis, worker, itemId, "Keep the teacher's observations");
        var before = (await analysis.GetAsync(itemId))!;
        var originalItem = await analysisDb.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), job.Version, "Correct recognized statement",
            [question.Question with { Statement = "1 + 1 = ?" }]);

        var corrected = await extraction.ReviseAsync(Admin, job.Id, request);

        var correctedQuestion = Assert.Single(corrected.Questions);
        Assert.Equal(question.Question.Id, correctedQuestion.Question.Id);
        Assert.Equal(itemId, correctedQuestion.ItemId);
        correctedQuestion.Question.Regions.Should().Equal(question.Question.Regions);
        correctedQuestion.Images.Should().Equal(question.Images);
        Assert.Equal(originalCrop.Bytes,
            (await extraction.ReadCropAsync(Admin, job.Id, question.Question.Id, corrected.Version, 0)).Bytes);
        var invalidated = (await analysis.GetAsync(itemId))!;
        Assert.Equal(before.Revision + 1, invalidated.Revision);
        Assert.True(invalidated.InputChanged);
        invalidated.Should().BeEquivalentTo(before, options => options
            .Excluding(a => a.Revision).Excluding(a => a.InputChanged));
        var correctedItem = await analysisDb.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        correctedItem.SourceRegions.Should().BeEquivalentTo(originalItem.SourceRegions);
        Assert.Equal(originalItem.QuestionId, correctedItem.QuestionId);
        Assert.Equal((int)ReviewStatus.PendingReview, correctedItem.ReviewStatus);
        Assert.Null(correctedItem.RootCause);
        Assert.Null(correctedItem.ReviewerId);
        Assert.Null(correctedItem.ReviewedAt);
        Assert.Equal("analysis_changed", (await Assert.ThrowsAsync<AnalysisConflictException>(() =>
            analysis.ConfirmAsync(itemId, before.Revision, "teacher"))).Message);
        Assert.Equal("input_changed", (await Assert.ThrowsAsync<AnalysisConflictException>(() =>
            analysis.ConfirmAsync(itemId, invalidated.Revision, "teacher"))).Message);
        Assert.False(await CompleteAsync(worker, stale, "Stale worker report"));
        var failed = (await worker.GetByIdAsync(stale.Id))!;
        Assert.Equal(ReanalyzeJobState.Failed, failed.Status);
        Assert.Equal("input_changed", failed.ErrorMessage);
        (await analysis.GetAsync(itemId)).Should().BeEquivalentTo(invalidated);
        await extraction.ReviseAsync(Admin, job.Id, request);
        (await analysis.GetAsync(itemId)).Should().BeEquivalentTo(invalidated);

        var fresh = await StartAndClaimAsync(analysis, worker, itemId, "Analyze the corrected statement");
        Assert.Equal(stale.ExecutionInputDigest, fresh.ExecutionInputDigest);
        Assert.Equal(invalidated.Revision + 1, fresh.AnalysisInput!.Revision);
        Assert.Equal(new[] { "The student wrote three", "Check the operation sign",
            "Keep the teacher's observations", "Analyze the corrected statement" }, fresh.AnalysisInput.TeacherFeedback);
        fresh.AnalysisInput.PreviousReport.Should().BeEquivalentTo(before.DraftReport);
        Assert.True(await CompleteAsync(worker, fresh, "Corrected report"));
        var completed = (await analysis.GetAsync(itemId))!;
        Assert.False(completed.InputChanged);
        Assert.Equal("Corrected report", completed.DraftReport!.Summary);
        completed.ConfirmedReport.Should().BeEquivalentTo(before.ConfirmedReport);
        var confirmed = await analysis.ConfirmAsync(itemId, completed.Revision, "teacher");
        Assert.Equal("Corrected report", confirmed.ConfirmedReport!.Summary);
        Assert.Equal(fresh.Id, confirmed.ConfirmedJobId);
    }

    [Fact]
    public async Task NumberOnlyCorrection_PreservesAnalysisAndReview_AndAllowsDraftConfirmationAndWorkerCompletion()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var extraction = Service(database);
        var job = await CompletedAsync(extraction);
        var question = Assert.Single(job.Questions);
        var itemId = question.ItemId!.Value;
        await using var analysisDb = database.CreateContext();
        await using var workerDb = database.CreateContext();
        var analysis = new MistakeAnalysisRepository(analysisDb);
        var worker = new ReanalyzeJobRepository(workerDb);
        var first = await StartAndClaimAsync(analysis, worker, itemId, "Original feedback");
        Assert.True(await CompleteAsync(worker, first, "Confirmed report"));
        await analysis.ConfirmAsync(itemId, (await analysis.GetAsync(itemId))!.Revision, "teacher");
        var second = await StartAndClaimAsync(analysis, worker, itemId, "Additional feedback");
        Assert.True(await CompleteAsync(worker, second, "Ready draft"));
        var before = (await analysis.GetAsync(itemId))!;
        var originalItem = await analysisDb.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == itemId);

        var numbered = await extraction.ReviseAsync(Admin, job.Id,
            new(Guid.NewGuid(), job.Version, "Correct printed number", [question.Question with { QuestionNumber = "12" }]));

        var numberedQuestion = Assert.Single(numbered.Questions);
        Assert.Equal(question.Question.Id, numberedQuestion.Question.Id);
        Assert.Equal(itemId, numberedQuestion.ItemId);
        numberedQuestion.Images.Should().Equal(question.Images);
        (await analysis.GetAsync(itemId)).Should().BeEquivalentTo(before);
        (await analysisDb.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == itemId))
            .Should().BeEquivalentTo(originalItem);
        var confirmed = await analysis.ConfirmAsync(itemId, before.Revision, "teacher");
        Assert.Equal("Ready draft", confirmed.ConfirmedReport!.Summary);

        var running = await StartAndClaimAsync(analysis, worker, itemId, "One more observation");
        before = (await analysis.GetAsync(itemId))!;
        await extraction.ReviseAsync(Admin, job.Id,
            new(Guid.NewGuid(), numbered.Version, "Correct printed sub-number",
                [numberedQuestion.Question with { QuestionNumber = "12（1）" }]));
        (await analysis.GetAsync(itemId)).Should().BeEquivalentTo(before);
        Assert.True(await CompleteAsync(worker, running, "Report after renumbering"));
        var completed = (await analysis.GetAsync(itemId))!;
        Assert.False(completed.InputChanged);
        Assert.Equal("Report after renumbering", completed.DraftReport!.Summary);
        await analysis.ConfirmAsync(itemId, completed.Revision, "teacher");
    }

    [Fact]
    public async Task RetractedGroup_InvalidatesAnalysis_WithoutDeletingReportsOrFeedback()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var extraction = Service(database);
        var job = await CompletedAsync(extraction);
        var question = Assert.Single(job.Questions);
        var itemId = question.ItemId!.Value;
        await using var analysisDb = database.CreateContext();
        await using var workerDb = database.CreateContext();
        var analysis = new MistakeAnalysisRepository(analysisDb);
        var worker = new ReanalyzeJobRepository(workerDb);
        var first = await StartAndClaimAsync(analysis, worker, itemId, "Retained feedback");
        Assert.True(await CompleteAsync(worker, first, "Retained report"));
        await analysis.ConfirmAsync(itemId, (await analysis.GetAsync(itemId))!.Revision, "teacher");
        var stale = await StartAndClaimAsync(analysis, worker, itemId, "Retained pending feedback");
        var before = (await analysis.GetAsync(itemId))!;

        var retracted = await extraction.ReviseAsync(Admin, job.Id,
            new(Guid.NewGuid(), job.Version, "Retract the question", []));

        Assert.Empty(retracted.Questions);
        var invalidated = (await analysis.GetAsync(itemId))!;
        Assert.Equal(before.Revision + 1, invalidated.Revision);
        Assert.True(invalidated.InputChanged);
        invalidated.Should().BeEquivalentTo(before, options => options
            .Excluding(a => a.Revision).Excluding(a => a.InputChanged));
        var item = await analysisDb.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        Assert.Equal((int)ReviewStatus.Rejected, item.ReviewStatus);
        Assert.Null(item.RootCause);
        Assert.False(await CompleteAsync(worker, stale, "Retracted worker report"));
        Assert.Equal("item_rejected", (await worker.GetByIdAsync(stale.Id))!.ErrorMessage);
        (await analysis.GetAsync(itemId)).Should().BeEquivalentTo(invalidated);

        var restored = await extraction.ReviseAsync(Admin, job.Id,
            new(Guid.NewGuid(), retracted.Version, "Restore retained question", [question.Question]));
        Assert.Equal(itemId, Assert.Single(restored.Questions).ItemId);
        var restoredAnalysis = (await analysis.GetAsync(itemId))!;
        Assert.Equal(invalidated.Revision + 1, restoredAnalysis.Revision);
        Assert.True(restoredAnalysis.InputChanged);
        restoredAnalysis.Should().BeEquivalentTo(invalidated, options => options.Excluding(a => a.Revision));
        Assert.Equal("input_changed", (await Assert.ThrowsAsync<AnalysisConflictException>(() =>
            analysis.ConfirmAsync(itemId, restoredAnalysis.Revision, "teacher"))).Message);
    }

    private static ExtractionService Service(ProductIntakePostgresTests.TestDatabase database) =>
        new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(database.Connection).Options,
            database.Oss.Object, new Analyzer(), new LayoutClient(),
            Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    private static async Task<ExtractionJobView> CompletedAsync(ExtractionService extraction)
    {
        using var image = RasterFixture.Create(40, 20);
        var queued = await extraction.SubmitAsync(Caller, new("student", 3, "worksheet",
            [new ProductIntakeImage { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(MagickFormat.Png) }]));
        await extraction.RunQueuedAsync(default);
        var job = await extraction.GetAsync(Caller, queued.Id);
        Assert.Equal("Succeeded", job.Status);
        return job;
    }

    private static async Task<ReanalyzeJobModel> StartAndClaimAsync(MistakeAnalysisRepository analysis,
        ReanalyzeJobRepository worker, Guid itemId, string feedback)
    {
        var revision = (await analysis.GetAsync(itemId))?.Revision ?? 0;
        var id = await analysis.StartAsync(itemId, "teacher", "school", Guid.NewGuid(), revision, feedback);
        var claimed = Assert.Single(await worker.ClaimAsync(1));
        Assert.Equal(id, claimed.Id);
        return claimed;
    }

    private static Task<bool> CompleteAsync(ReanalyzeJobRepository worker, ReanalyzeJobModel job, string summary) =>
        worker.CompleteAnalysisAsync(job.Id, job.OwnerId!.Value, job.FencingVersion,
            new() { Model = "test-model", PromptVersion = "test-v1", Report = new() { Summary = summary } }, 1);

    private sealed class Analyzer : IQuestionAnalysisClient
    {
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default) =>
            Task.FromResult(new QuestionAnalysisResult("test-model", [new()
            {
                Subject = 2, Statement = "1+1", StudentAnswer = "3", ReferenceAnswer = "2",
                Evidence = "Student wrote three instead of two", Explanation = "Add one and one", Outcome = "incorrect",
                Regions = [new(request.Images[0].SourceId, 0, 0, .5, 1)]
            }]));
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default)
        {
            using var decoded = new MagickImage(image.Bytes);
            return Task.FromResult(new LayoutDetectionResult
            {
                RequestId = "test-layout", Width = (int)decoded.Width, Height = (int)decoded.Height,
                Regions = [new(0, "question", new(0, 0, 1, 1))]
            });
        }
    }
}
