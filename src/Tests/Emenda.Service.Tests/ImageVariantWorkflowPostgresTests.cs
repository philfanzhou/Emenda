using Emenda.Common.Oss;
using Emenda.TestFixtures;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Images;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Emenda.Service.Tests;

public sealed class ImageVariantWorkflowPostgresTests
{
    private static readonly ExtractionAccess Caller = new("variant-tests", "teacher");
    private static readonly ExtractionAccess Admin = new("admin", "variant-admin", true);

    [Fact]
    public async Task NewJobsAreQueued_BacklogStoresEveryVariant_AndReadsServeSizes()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        var crop = job.Questions.Single(q => q.Images.Count > 0);
        var parents = job.Sources.Select(s => s.Path).Concat(crop.Images.Select(i => i.Path)).ToHashSet();
        Assert.Equal(parents, (await QueuedAsync(db)).ToHashSet());

        Assert.Equal(parents.Count, await Backlog(db).RunOnceAsync(64, default));
        Assert.Empty(await QueuedAsync(db));
        foreach (var parent in parents)
        foreach (var (size, _) in ImageVariants.Sizes)
            Assert.True(await db.Files.ObjectExistsAsync(ImageVariants.PathFor(parent, size)), parent + " " + size);

        var thumbnail = await service.ReadSourceAsync(Caller, job.Id, job.Sources[0].Id, "thumbnail");
        Assert.Equal("image/jpeg", thumbnail.ContentType);
        using (var decoded = new MagickImage(thumbnail.Bytes)) Assert.True(Math.Max(decoded.Width, decoded.Height) <= 200);
        var small = await service.ReadCropAsync(Caller, job.Id, crop.Question.Id, job.Version, 0, "small");
        Assert.Equal("image/jpeg", small.ContentType);
        var full = await service.ReadCropAsync(Caller, job.Id, crop.Question.Id, job.Version, 0, "large");
        Assert.Equal("image/png", full.ContentType);
        var invalid = await Assert.ThrowsAsync<ExtractionException>(() => service.ReadSourceAsync(Caller, job.Id, job.Sources[0].Id, "huge"));
        Assert.Equal(("invalid_size", 400), (invalid.Code, invalid.Status));

        // A missing variant (e.g. a pre-v21 object the backlog has not reached) is rendered on read and kept.
        var medium = ImageVariants.PathFor(job.Sources[1].Path, "medium");
        await db.Files.DeleteIfExistsAsync(medium);
        Assert.Equal("image/jpeg", (await service.ReadSourceAsync(Caller, job.Id, job.Sources[1].Id, "medium")).ContentType);
        Assert.True(await db.Files.ObjectExistsAsync(medium));
    }

    [Fact]
    public async Task FailedParentLookupStaysQueuedUntilStorageRecovers()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        await using var context = db.CreateContext();
        using var image = RasterFixture.Create(100, 40);
        var path = await db.Files.UploadAsync(image.ToByteArray(MagickFormat.Png), "photo.png", "image/png", OssBucket.Mistakes, "legacy");
        await ImageVariantSchema.EnqueueAsync(context, [path]);
        db.Oss.Setup(o => o.ObjectExistsOrThrowAsync(path, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));

        var worker = Backlog(db);
        Assert.Equal(1, await worker.RunOnceAsync(1, default));
        Assert.Equal(new[] { path }, await QueuedAsync(db));
        Assert.Equal(0, await worker.RunOnceAsync(1, default));

        db.Oss.Setup(o => o.ObjectExistsOrThrowAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE image_variant_task SET next_attempt=clock_timestamp() WHERE path={path}");
        Assert.Equal(1, await worker.RunOnceAsync(1, default));
        Assert.Empty(await QueuedAsync(db));
        Assert.True(await ImageVariants.AllExistAsync(db.Files, path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredClaimCannotCompleteOrRescheduleAnotherWorkersLease(bool storageFailed)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        await using var context = db.CreateContext();
        const string path = "mistakes/legacy/expired.png";
        await ImageVariantSchema.EnqueueAsync(context, [path]);
        db.Oss.Setup(o => o.ObjectExistsOrThrowAsync(path, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // A second worker acquired the expired lease while the first was waiting on S3.
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE image_variant_task SET next_attempt=clock_timestamp()+interval '10 minutes', attempts=attempts+1 WHERE path={path}");
                if (storageFailed) throw new IOException("Storage unavailable");
                return false;
            });
        Assert.Equal(1, await Backlog(db).RunOnceAsync(1, default));
        Assert.True(await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM image_variant_task WHERE path={path} AND attempts=2
                AND next_attempt > clock_timestamp()+interval '9 minutes') AS "Value"
            """).SingleAsync());
    }

    [Fact]
    public async Task RetiredCropsLoseTheirVariants_AndReplacementCropsAreQueued()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        await Backlog(db).RunOnceAsync(64, default);
        var old = job.Questions.Single(q => q.Images.Count > 0);
        var oldCrop = old.Images[0].Path;
        var revised = await service.ReviseQuestionAsync(Caller, job.Id, old.Question.Id, new(Guid.NewGuid(), job.Version, "Narrow",
            old.Question with { Regions = [old.Question.Regions[0] with { X1 = .5 }, .. old.Question.Regions.Skip(1)] }));
        var replacement = revised.Question!.Images[0].Path;
        Assert.NotEqual(oldCrop, replacement);
        Assert.False(await db.Files.ObjectExistsAsync(oldCrop));
        foreach (var (size, _) in ImageVariants.Sizes) Assert.False(await db.Files.ObjectExistsAsync(ImageVariants.PathFor(oldCrop, size)));
        Assert.Contains(replacement, await QueuedAsync(db));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdministratorDeletion_KeepsRetainedThumbnailsAndRemovesEveryOtherVariant(bool confirmed)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var service = Service(db);
        var job = await Completed(service);
        await Backlog(db).RunOnceAsync(64, default);
        var mistake = job.Questions.Single(q => q.Images.Count > 0);
        var crop = mistake.Images[0].Path;
        if (confirmed)
        {
            await using var context = db.CreateContext();
            (await context.MistakeItems.SingleAsync()).ReviewStatus = (int)ReviewStatus.Confirmed;
            await context.SaveChangesAsync();
        }
        await ImageVariantSchema.EnqueueAsync(db.CreateContext(), [job.Sources[0].Path]);

        var receipt = await service.DeleteAsync(Admin, job.Id);
        Assert.Equal(confirmed ? 1 : 0, receipt.RetainedConfirmed);
        for (var round = 0; round < 3; round++)
        {
            await using (var context = db.CreateContext())
                await context.ExtractionObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttempt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await service.RunCleanupAsync();
        }
        foreach (var source in job.Sources)
        foreach (var path in Family(source.Path))
            Assert.False(await db.Files.ObjectExistsAsync(path), path);
        foreach (var path in Family(crop))
            Assert.Equal(confirmed, await db.Files.ObjectExistsAsync(path));
        // A queued original of a deleted task is dropped without rendering anything.
        await Backlog(db).RunOnceAsync(64, default);
        Assert.Empty(await QueuedAsync(db));
        Assert.False(await db.Files.ObjectExistsAsync(ImageVariants.PathFor(job.Sources[0].Path, "small")));
        if (!confirmed)
        {
            Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() =>
                service.ReadRetainedCropAsync(Caller, mistake.ItemId!.Value, 0, "small", default))).Status);
            return;
        }
        // The retained confirmed crop keeps serving every size from the archive after its task is gone.
        var small = await service.ReadRetainedCropAsync(Caller, mistake.ItemId!.Value, 0, "small", default);
        Assert.Equal("image/jpeg", small.ContentType);
        Assert.Equal("image/png", (await service.ReadRetainedCropAsync(Caller, mistake.ItemId!.Value, 0, null, default)).ContentType);
        Assert.Equal(400, (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.ReadRetainedCropAsync(Caller, mistake.ItemId!.Value, 0, "huge", default))).Status);
    }

    private static IEnumerable<string> Family(string path) => ImageVariants.Sizes.Select(s => ImageVariants.PathFor(path, s.Size)).Prepend(path);

    private static async Task<List<string>> QueuedAsync(PostgresTestDatabase db)
    {
        await using var context = db.CreateContext();
        return await context.Database.SqlQuery<string>($"SELECT path AS \"Value\" FROM image_variant_task ORDER BY path").ToListAsync();
    }

    private static ImageVariantBacklog Backlog(PostgresTestDatabase db) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object,
        NullLogger<ImageVariantBacklog>.Instance);

    private static ExtractionService Service(PostgresTestDatabase db) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, new Analyzer(),
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    // Stable bytes: a replay must present the same payload as the original submission.
    private static readonly ExtractionImage[] Pages = [Image(1), Image(2)];
    private static ExtractionUpload Upload() => new("student", 4, "variants", [.. Pages]);

    private static async Task<ExtractionJobView> Completed(ExtractionService service)
    {
        var queued = await service.SubmitAsync(Caller, Upload());
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, queued.Id);
        Assert.Equal("Succeeded", job.Status);
        return job;
    }

    private static ExtractionImage Image(int n)
    {
        using var image = RasterFixture.Create(1200, 900, red: (byte)(n * 60));
        return new() { FileName = "page.png", ContentType = "image/png", Content = image.ToByteArray(MagickFormat.Png) };
    }

    private sealed class Analyzer : IQuestionAnalysisClient
    {
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            ExtractionQuestionDraft Question(string outcome) => new()
            { Subject = 2, Statement = "Problem " + outcome, StudentAnswer = "3", ReferenceAnswer = "2", Evidence = "Visible answer 3 differs from 2",
                Outcome = outcome, Regions = request.Images.Select(i => new ExtractionRegion(i.SourceId, 0, 0, 1, 1)).ToList() };
            return Task.FromResult(new QuestionAnalysisResult("controlled", [Question("incorrect"), Question("correct")]));
        }
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "controlled", Width = 1200, Height = 900,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
