using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Moq;

namespace Emenda.Service.Tests;

public sealed class ExtractionCorrectionsWorkflowPostgresTests
{
    private static readonly ExtractionAccess Caller = new("correction-tests", "teacher");

    [Fact]
    public async Task CropsAreOnlyForMistakes_ReplaceOnePart_RetireHistory_AndRecoverFailedDeletion()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        Assert.Equal(3, job.Questions.Count);
        Assert.Equal(2, job.Questions[0].Images.Count);
        Assert.Empty(job.Questions[1].Images);
        Assert.Empty(job.Questions[2].Images);
        var old = job.Questions[0];
        db.Oss.Setup(o => o.DeleteIfExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        var correction = new ExtractionQuestionRevisionRequest(Guid.NewGuid(), job.Version, "Complete second part",
            old.Question with { Regions = [old.Question.Regions[0], old.Question.Regions[1] with { X1 = .5 }] });
        var result = await service.ReviseQuestionAsync(Caller, job.Id, old.Question.Id, correction);
        Assert.NotNull(result.Question);
        Assert.Equal(old.ItemId, result.Question.ItemId);
        Assert.Equal(old.Images[0], result.Question.Images[0]);
        Assert.NotEqual(old.Images[1].Path, result.Question.Images[1].Path);
        Assert.True(await db.Files.ObjectExistsAsync(old.Images[1].Path));
        Assert.Equal(410, (await Assert.ThrowsAsync<ExtractionException>(() => service.ReadCropAsync(Caller, job.Id, old.Question.Id, 1, 0))).Status);
        await using (var context = db.CreateContext())
        {
            Assert.Equal("{\"questions\": []}", (await context.ExtractionRevisions.SingleAsync(r => r.JobId == job.Id && r.Version == 1)).SnapshotJson);
            Assert.Null((await context.ExtractionJobs.SingleAsync()).CandidateJson);
            Assert.All(await context.ProcessLogs.ToListAsync(), log => Assert.DoesNotContain(old.Images[1].Path, log.Details ?? ""));
            var queued = await context.ExtractionObjects.SingleAsync(o => o.Path == old.Images[1].Path);
            Assert.True(queued.Pending);
            Assert.True(queued.Attempts > 0);
            queued.NextAttempt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }
        db.Oss.Setup(o => o.DeleteIfExistsAsync(It.IsAny<string>())).Returns((string path) => db.Files.DeleteIfExistsAsync(path));
        await Service(db, analyzer).RunCleanupAsync();
        Assert.False(await db.Files.ObjectExistsAsync(old.Images[1].Path));
        Assert.True(await db.Files.ObjectExistsAsync(old.Images[0].Path));
        foreach (var source in job.Sources) Assert.True(await db.Files.ObjectExistsAsync(source.Path));
        var classified = correction with { RequestKey = Guid.NewGuid(), ExpectedVersion = result.Version,
            Question = result.Question.Question with { Outcome = "correct", StudentAnswer = "2", Evidence = "Actually wrote 2" } };
        var removed = await service.ReviseQuestionAsync(Caller, job.Id, old.Question.Id, classified);
        Assert.Empty(removed.Question!.Images);
        Assert.False(await db.Files.ObjectExistsAsync(old.Images[0].Path));
        await using var check = db.CreateContext();
        Assert.Empty((await check.MistakeItems.SingleAsync()).SourceRegions);
        Assert.True((await service.ReviseQuestionAsync(Caller, job.Id, old.Question.Id, correction)).Superseded);
    }

    [Fact]
    public async Task SharedRegionsSurviveUntilLastReference_AndFailedPublishObjectsAreRecoverable()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer { MultipleMistakes = true };
        var service = Service(db, analyzer);
        var job = await Completed(service);
        Assert.Equal(job.Questions[0].Images, job.Questions[1].Images);
        var retained = job.Questions[0].Images[0].Path;
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), job.Version, "Remove one shared reference", [job.Questions[1].Question]);
        var revised = await service.ReviseAsync(Caller, job.Id, request);
        Assert.True(await db.Files.ObjectExistsAsync(retained));
        await using (var context = db.CreateContext())
            await context.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_publish_failure'; END $$; CREATE TRIGGER fail_revision BEFORE INSERT ON extraction_revision FOR EACH ROW EXECUTE FUNCTION fail_revision();");
        var changed = revised.Questions[0].Question with { Statement = "This correction must not publish",
            Regions = [revised.Questions[0].Question.Regions[0] with { X1 = .5 }] };
        await Assert.ThrowsAnyAsync<Exception>(() => service.ReviseAsync(Caller, job.Id,
            new(Guid.NewGuid(), revised.Version, "Fail after storage", [changed])));
        Assert.Equal(revised.Version, (await service.GetAsync(Caller, job.Id)).Version);
        Assert.True(await db.Files.ObjectExistsAsync(retained));
        await using (var context = db.CreateContext())
        {
            var items = await context.MistakeItems.AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.Null(MistakeSource.Analysis(items[job.Questions[0].ItemId!.Value]));
            using var retainedAnalysis = JsonDocument.Parse(MistakeSource.Analysis(items[job.Questions[1].ItemId!.Value])!);
            Assert.Equal("Other subject", retainedAnalysis.RootElement.GetProperty("statement").GetString());
            Assert.Equal(job.Version, retainedAnalysis.RootElement.GetProperty("sourceVersion").GetInt32());
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_revision ON extraction_revision; DROP FUNCTION fail_revision(); UPDATE extraction_object SET next_attempt=clock_timestamp();");
        }
        await service.RunCleanupAsync();
        await using var check = db.CreateContext();
        Assert.DoesNotContain(await check.ExtractionObjects.ToListAsync(), o => o.Pending);
    }

    [Fact]
    public async Task SupplementResumesAfterLostPut_StaysOnSameQuestion_AndClearsPendingNote()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db, new());
        var job = await Completed(service);
        var target = job.Questions[0];
        var request = new ExtractionSupplementRequest(Guid.NewGuid(), job.Version, target.ItemId!.Value, "Missing last two lines");
        var note = await service.RequestSupplementAsync(Caller, job.Id, target.Question.Id, request);
        Assert.Equal("Requested", note.Status);
        Assert.Equal(note.Id, (await service.GetAsync(Caller, job.Id)).Questions[0].Supplement!.Id);
        var append = new ExtractionSupplementUpload(Guid.NewGuid(), note.Version, target.ItemId.Value, [Image(3)]);
        db.FailAfterPut = true;
        await Assert.ThrowsAsync<IOException>(() => service.AppendSupplementAsync(Caller, job.Id, target.Question.Id, note.Id, append));
        Assert.Equal(note.Version, (await service.GetAsync(Caller, job.Id)).Version);
        db.FailAfterPut = false;
        var result = await service.AppendSupplementAsync(Caller, job.Id, target.Question.Id, note.Id, append);
        var replay = await service.AppendSupplementAsync(Caller, job.Id, target.Question.Id, note.Id, append);
        Assert.Equal(result.Id, replay.Id);
        Assert.Equal(result.Version, replay.Version);
        Assert.Equal(result.Sources.Select(s => s.Id), replay.Sources.Select(s => s.Id));
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(3, read.Sources.Count);
        Assert.Null(read.Questions[0].Supplement);
        Assert.Equal(target.ItemId, read.Questions[0].ItemId);
        Assert.Equal("Fulfilled", (await service.GetSupplementAsync(Caller, job.Id, target.Question.Id, note.Id)).Status);
        await using var check = db.CreateContext();
        Assert.Equal(1, (await check.MistakeItems.SingleAsync()).ReviewStatus);
        Assert.Equal(1, await check.MistakeItems.CountAsync());
        var foreign = await Assert.ThrowsAsync<ExtractionException>(() => service.GetSupplementAsync(new("foreign", "foreign"), job.Id, target.Question.Id, note.Id));
        Assert.Equal(404, foreign.Status);
    }

    [Fact]
    public async Task ProposalsDoNotPublishOrCreateImages_RefineThenAdoptOnce_AndPreserveOtherSubjects()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer();
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var target = job.Questions[0];
        var request = new ExtractionProposalRequest(Guid.NewGuid(), job.Version, "Complete the second page", "questions", [target.Question.Id], [], []);
        var proposal = await service.GenerateProposalAsync(Caller, job.Id, request);
        Assert.Equal("Pending", proposal.Status);
        Assert.Equal(target.Question.Id, proposal.Questions.Single().Id);
        Assert.Equal(job.Version, (await service.GetAsync(Caller, job.Id)).Version);
        await using (var context = db.CreateContext())
        {
            Assert.Equal(4, await context.ExtractionObjects.CountAsync());
            using var pending = JsonDocument.Parse(MistakeSource.Analysis((await context.MistakeItems.SingleAsync()))!);
            Assert.Equal("Mistake", pending.RootElement.GetProperty("statement").GetString());
            Assert.Equal(job.Version, pending.RootElement.GetProperty("sourceVersion").GetInt32());
        }
        var refinement = await service.GenerateProposalAsync(Caller, job.Id,
            request with { RequestKey = Guid.NewGuid(), Instruction = "Keep more of the diagram", PreviousProposalId = proposal.Id });
        Assert.Equal("Superseded", (await service.GetProposalAsync(Caller, job.Id, proposal.Id)).Status);
        Assert.Empty((await service.GetProposalAsync(Caller, job.Id, proposal.Id)).Questions);
        Assert.Equal(refinement.Id, Assert.Single(await Service(db, analyzer).ListProposalsAsync(Caller, job.Id)).Id);
        var accept = new ExtractionProposalAdoptionRequest(Guid.NewGuid(), job.Version);
        var adopted = await service.AdoptProposalAsync(Caller, job.Id, refinement.Id, accept);
        var replay = await service.AdoptProposalAsync(Caller, job.Id, refinement.Id, accept);
        Assert.Equal(adopted, replay);
        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(job.Questions.Select(q => (q.Question.Id, q.ItemId)), read.Questions.Select(q => (q.Question.Id, q.ItemId)));
        Assert.Equal(target.Images[0], read.Questions[0].Images[0]);
        Assert.NotEqual(target.Images[1].Path, read.Questions[0].Images[1].Path);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(job.Questions[1]), System.Text.Json.JsonSerializer.Serialize(read.Questions[1]));
        Assert.Equal("Accepted", (await service.GetProposalAsync(Caller, job.Id, refinement.Id)).Status);
        await using var check = db.CreateContext();
        var item = await check.MistakeItems.SingleAsync();
        Assert.Equal(1, item.ReviewStatus);
        using var analysis = JsonDocument.Parse(MistakeSource.Analysis(item)!);
        Assert.Equal("Complete mistake statement", analysis.RootElement.GetProperty("statement").GetString());
        Assert.Equal(read.Version, analysis.RootElement.GetProperty("sourceVersion").GetInt32());
    }

    [Fact]
    public async Task ProposalKeepsARequestedCollectionOnlyOnACorrectQuestion_AndAdoptionCollectsIt()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer { CollectAll = true, Unknown = true };
        var service = Service(db, analyzer);
        var job = await Completed(service);
        var (correct, unknown) = (job.Questions[1], job.Questions[2]);
        var proposal = await service.GenerateProposalAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "这两题也收录",
            "questions", [correct.Question.Id, unknown.Question.Id], [], []));

        Assert.Equal("Pending", proposal.Status);
        Assert.Equal(new[] { true, false }, proposal.Questions.Select(q => q.IncludedByRequest));
        var adopted = await service.AdoptProposalAsync(Caller, job.Id, proposal.Id, new(Guid.NewGuid(), job.Version));

        var read = await service.GetAsync(Caller, job.Id);
        Assert.Equal(adopted.Version, read.Version);
        var collected = read.Questions.Single(q => q.Question.Id == correct.Question.Id);
        Assert.Equal(("correct", true), (collected.Question.Outcome, collected.Question.IncludedByRequest));
        Assert.NotNull(collected.ItemId);
        Assert.NotEmpty(collected.Images);
        // An unknown question is collected from the start; a request on it is dropped and its item is kept.
        var other = read.Questions.Single(q => q.Question.Id == unknown.Question.Id);
        Assert.Equal(("unknown", false), (other.Question.Outcome, other.Question.IncludedByRequest));
        Assert.NotNull(unknown.ItemId);
        Assert.Equal(unknown.ItemId, other.ItemId);
    }

    [Fact]
    public async Task ZeroMistakesCanProposeMissingQuestion_DiscardAndStaleAdoptionNeverPublish()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var analyzer = new Analyzer { Empty = true };
        var service = Service(db, analyzer);
        var job = await Completed(service);
        Assert.Empty(job.Questions);
        var request = new ExtractionProposalRequest(Guid.NewGuid(), job.Version, "Missed the circled question", "regions", [], [job.Sources[0].Id], [new(job.Sources[0].Id, 0, 0, .5, 1)]);
        var candidate = await service.GenerateProposalAsync(Caller, job.Id, request);
        await using (var context = db.CreateContext()) Assert.Empty(await context.MistakeItems.ToListAsync());
        await service.DiscardProposalAsync(Caller, job.Id, candidate.Id);
        Assert.Empty((await service.GetProposalAsync(Caller, job.Id, candidate.Id)).Questions);
        var pending = await service.GenerateProposalAsync(Caller, job.Id, request with { RequestKey = Guid.NewGuid() });
        await service.ReviseAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version, "Another correction", []));
        Assert.Equal("Expired", (await service.GetProposalAsync(Caller, job.Id, pending.Id)).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ExtractionException>(() => service.AdoptProposalAsync(Caller, job.Id, pending.Id,
            new(Guid.NewGuid(), job.Version)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() => service.GetProposalAsync(new("foreign", "foreign"), job.Id, pending.Id))).Status);
    }

    [Fact]
    public async Task CanceledDeletionReceiptStillRejectsRetiredReferences_AndCleanupResumes()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new Analyzer());
        var job = await Completed(service);
        var target = job.Questions[0];
        var retired = target.Images[0].Path;
        using var cancel = new CancellationTokenSource();
        database.Oss.Setup(o => o.DeleteDerivedAsync(retired)).Returns(async () =>
        {
            var deleted = await database.Oss.Object.DeleteIfExistsAsync(retired);
            cancel.Cancel();
            return deleted;
        });
        var changed = target.Question with { Regions = [target.Question.Regions[0] with { X0 = .1 }, .. target.Question.Regions.Skip(1)] };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReviseQuestionAsync(Caller, job.Id,
            target.Question.Id, new(Guid.NewGuid(), job.Version, "Retire and cancel receipt", changed), cancel.Token));
        await using (var db = database.CreateContext())
        {
            Assert.True((await db.ExtractionObjects.SingleAsync(o => o.Path == retired)).Pending);
            var item = await db.MistakeItems.SingleAsync(i => i.Id == target.ItemId);
            item.SourceRegions = [new() { SourceImagePath = retired }];
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("extraction_object_retired", error.InnerException!.Message);
        }
        database.Oss.Setup(o => o.DeleteDerivedAsync(retired)).Returns(() => database.Oss.Object.DeleteIfExistsAsync(retired));
        await service.RunCleanupAsync();
        await using var verify = database.CreateContext();
        Assert.False(await verify.ExtractionObjects.AnyAsync(o => o.Path == retired));
        Assert.Equal(job.Version + 1, (await service.GetAsync(Caller, job.Id)).Version);
    }

    [Fact]
    public async Task IndependentQuestionProposalsCoexist_AndExplicitParentIsSupersededAcrossPages()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new Analyzer());
        var job = await Completed(service);
        var first = await service.GenerateProposalAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version,
            "Check this question", "questions", [job.Questions[0].Question.Id], [], []));
        var second = await service.GenerateProposalAsync(Caller, job.Id, new(Guid.NewGuid(), job.Version,
            "Check another subject", "questions", [job.Questions[1].Question.Id], [], []));
        Assert.Equal("Pending", (await service.GetProposalAsync(Caller, job.Id, first.Id)).Status);
        Assert.Equal("Pending", second.Status);
        var pageA = new ExtractionProposalRequest(Guid.NewGuid(), job.Version, "Find a missed question", "regions", [], [job.Sources[0].Id], []);
        var parent = await service.GenerateProposalAsync(Caller, job.Id, pageA);
        var refined = await service.GenerateProposalAsync(Caller, job.Id, pageA with { RequestKey = Guid.NewGuid(),
            Instruction = "Its answer continues on the other page", SourceIds = [job.Sources[1].Id], PreviousProposalId = parent.Id });
        Assert.Equal("Pending", refined.Status);
        Assert.Equal("Superseded", (await service.GetProposalAsync(Caller, job.Id, parent.Id)).Status);
        Assert.Equal(3, (await service.ListProposalsAsync(Caller, job.Id)).Count);
    }

    private static ExtractionService Service(PostgresTestDatabase db, Analyzer analyzer) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, analyzer,
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    private static async Task<ExtractionJobView> Completed(ExtractionService service)
    {
        var queued = await service.SubmitAsync(Caller, new("student", 4, "worksheet", [Image(1), Image(2)]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        Assert.Equal("Succeeded", job.Status);
        return job;
    }

    private static ExtractionImage Image(int n)
    {
        using var image = new ImageMagick.MagickImage(ExtractionHttpPostgresTests.TestPng());
        image.Resize(20, 10);
        image.Colorize(new ImageMagick.MagickColor((byte)(n * 40), 20, 40), new ImageMagick.Percentage(100));
        return new() { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(ImageMagick.MagickFormat.Png) };
    }

    private sealed class Analyzer : IQuestionAnalysisClient
    {
        public bool MultipleMistakes { get; init; }
        public bool Empty { get; init; }
        public bool CollectAll { get; init; }
        /// <summary>Judges the third question unknown, which collects it over the same regions as the first.</summary>
        public bool Unknown { get; init; }
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            ExtractionQuestionDraft Question(string outcome, string statement, int subject = 2) => new()
            { Subject = subject, Statement = statement, StudentAnswer = "3", ReferenceAnswer = "2", Evidence = "Visible answer 3 differs from 2",
                Outcome = outcome, Regions = request.Images.Select(i => new ExtractionRegion(i.SourceId, 0, 0, 1, 1)).ToList() };
            if (request.Revision is { } revision)
            {
                Assert.NotEmpty(revision.Instruction);
                var corrected = revision.Questions.Count > 0 ? revision.Questions.Select(q => q with
                    { Statement = "Complete mistake statement", Regions = [q.Regions[0], q.Regions[^1] with { X1 = .5 }], IncludedByRequest = CollectAll }).ToList()
                    : [Question("incorrect", "Missed question")];
                return Task.FromResult(new QuestionAnalysisResult("controlled", corrected));
            }
            return Task.FromResult(new QuestionAnalysisResult("controlled", Empty ? [] :
                [Question("incorrect", "Mistake"), Question(MultipleMistakes ? "incorrect" : "correct", "Other subject", 3), Question(Unknown ? "unknown" : "correct", "Third")]));
        }
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "controlled", Width = 20, Height = 10,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
