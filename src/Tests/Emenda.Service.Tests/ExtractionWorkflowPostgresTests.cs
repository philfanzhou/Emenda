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
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var analyzer = new Analyzer(request => new("test-model", [
            Question("incorrect", [new(request.Images[0].SourceId, 0, 0, .5, 1), new(request.Images[1].SourceId, 0, 0, 1, 1)]),
            Question("correct", [new(request.Images[0].SourceId, .5, 0, 1, 1)]) with { Statement = "2+2", StudentAnswer = "4", ReferenceAnswer = "4" },
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
        item.RootCause.Should().BeNull("a comparison explanation is not a proven root cause");
        await Assert.ThrowsAsync<ExtractionException>(() => service.GetAsync(new("other", "other"), result.Id));
    }

    [Fact]
    public async Task ConcurrentHostsAndReplay_DoNotDuplicateJobsQuestionsOrItems()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var analyzer = DefaultAnalyzer();
        var first = Service(database, analyzer);
        var second = Service(database, analyzer);
        var receipts = await Task.WhenAll(first.SubmitAsync(Caller, Upload()), second.SubmitAsync(Caller, Upload()));
        receipts[0].Id.Should().Be(receipts[1].Id);
        await Task.WhenAll(first.RunQueuedAsync(default), second.RunQueuedAsync(default));
        var replay = await second.SubmitAsync(Caller, Upload());
        replay.Version.Should().Be(1);
        analyzer.Calls.Should().Be(1);
        await using var db = database.CreateContext();
        (await db.ExtractionJobs.CountAsync()).Should().Be(1);
        (await db.ExtractionRevisions.CountAsync()).Should().Be(1);
        (await db.MistakeItems.CountAsync()).Should().Be(1);
        var conflict = await Assert.ThrowsAsync<ExtractionException>(() => first.SubmitAsync(Caller, Upload() with { StudentId = "different" }));
        conflict.Code.Should().Be("source_conflict");
    }

    [Fact]
    public async Task LostOriginalPut_RetainsBindingAndUnchangedRetryRecovers()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var service = Service(database, DefaultAnalyzer());
        database.FailAfterPut = true;
        await Assert.ThrowsAsync<IOException>(() => service.SubmitAsync(Caller, Upload()));
        (await service.ListAsync(Caller, null)).Single().Status.Should().Be("Staging");
        var conflict = await Assert.ThrowsAsync<ExtractionException>(() => service.SubmitAsync(Caller, Upload() with { Grade = 9 }));
        conflict.Code.Should().Be("source_conflict");
        database.FailAfterPut = false;
        var queued = await service.SubmitAsync(Caller, Upload());
        await service.RunQueuedAsync(default);
        (await service.GetAsync(Caller, queued.Id)).Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task Correction_IsAtomicVersionedAdminOnly_AndPreservesPracticeAndOldImages()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
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
        (await Assert.ThrowsAsync<ExtractionException>(() => service.ReviseAsync(Caller, job.Id, request))).Status.Should().Be(403);
        var corrected = await service.ReviseAsync(Admin, job.Id, request);
        corrected.Version.Should().Be(2);
        corrected.Questions.Single().ItemId.Should().Be(itemId);
        (await service.ReviseAsync(Admin, job.Id, request)).Version.Should().Be(2);
        (await service.ReadCropAsync(Admin, job.Id, q.Question.Id, 1, 0)).Bytes.Should().Equal(oldBytes);
        (await service.ReadCropAsync(Admin, job.Id, q.Question.Id, 2, 0)).Bytes.Should().NotEqual(oldBytes);
        (await Assert.ThrowsAsync<ExtractionException>(() => service.ReviseAsync(Admin, job.Id, request with { RequestKey = Guid.NewGuid() }))).Code.Should().Be("version_conflict");
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
        (await service.GetVersionAsync(Admin, job.Id, 1)).Questions.Single().Question.Regions[0].X0.Should().Be(0);
        (await service.ReviseAsync(Admin, job.Id, request)).Version.Should().Be(2, "exact request replay returns its accepted version, not latest state");
    }

    [Fact]
    public async Task DeletedItemIsNotRecreatedByCorrectionOrHistoricalRestore()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
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
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
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
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
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

    private static ExtractionService Service(ProductIntakePostgresTests.TestDatabase database, Analyzer analyzer, LayoutClient? layout = null) =>
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
        return new ProductIntakeImage { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(MagickFormat.Png) };
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
