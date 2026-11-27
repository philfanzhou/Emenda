using System.Security.Cryptography;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Host.Services.Extraction;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace Emenda.Service.Tests;

public sealed class ExtractionDeletionPostgresTests
{
    private static readonly ExtractionAccess Caller = new("synthetic-school", "synthetic-teacher");
    private static readonly ExtractionAccess Admin = new("admin", "synthetic-admin", true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DeletesJobAndUnconfirmedResultsButKeepsConfirmedAnalysisAndSharedReferences(bool allConfirmed, bool rejected)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new(2));
        var upload = new ExtractionUpload("student", 4, "worksheet", [Image(1)]);
        var staged = await service.SubmitAsync(Caller, upload);
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, staged.Id);
        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(2, job.Questions.Count);
        var first = job.Questions[0];
        var second = job.Questions[1];
        var firstId = first.ItemId!.Value;
        var secondId = second.ItemId!.Value;
        var original = Assert.Single(job.Sources).Path;
        var supplement = await service.RequestSupplementAsync(Caller, job.Id, first.Question.Id,
            new(Guid.NewGuid(), job.Version, firstId, "Continue with a synthetic page"));
        var addition = await service.AppendSupplementAsync(Caller, job.Id, first.Question.Id, supplement.Id,
            new(Guid.NewGuid(), supplement.Version, firstId, [Image(2), Image(3)]));
        var supplementPath = addition.Sources[0].Path;
        var orphanSupplement = addition.Sources[1].Path;
        var practiceId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync(i => i.Id == firstId);
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            item.ReviewerId = "synthetic-reviewer";
            item.RootCause = "Retained explanation";
            MistakeSource.SetAnalysis(item, null); // simulate pre-backfill analysis still available in the current revision
            if (allConfirmed || rejected)
                (await db.MistakeItems.SingleAsync(i => i.Id == secondId)).ReviewStatus =
                    (int)(allConfirmed ? ReviewStatus.Confirmed : ReviewStatus.Rejected);
            db.MistakePractices.Add(new() { Id = practiceId, MistakeItemId = firstId, StudentId = "student", ImagePaths = [supplementPath] });
            await db.SaveChangesAsync();
        }
        var confirmedPath = Assert.Single(first.Images).Path;
        var otherPath = Assert.Single(second.Images).Path;
        database.Oss.Setup(o => o.DeleteExtractionOriginalAsync(original)).ReturnsAsync(false);
        Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() => service.DeleteAsync(Caller, job.Id))).Status);
        var receipt = await service.DeleteAsync(Admin, job.Id);
        Assert.Equal(allConfirmed ? 0 : 1, receipt.DeletedMistakes);
        Assert.Equal(allConfirmed ? 2 : 1, receipt.RetainedConfirmed);
        Assert.Equal(allConfirmed ? 2 : 3, receipt.PendingObjects);
        Assert.Equal("Pending", receipt.Status);
        Assert.Equal(receipt, await service.DeleteAsync(Admin, job.Id));
        Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() => service.GetAsync(Admin, job.Id))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() => service.ReviseAsync(Admin, job.Id,
            new(Guid.NewGuid(), job.Version, "Late correction", [first.Question])))).Status);
        Assert.Empty(await service.ListAsync(Admin, "student"));

        await service.RunCleanupAsync();
        var failed = await service.GetDeletionAsync(Admin, job.Id);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(1, failed.PendingObjects);
        Assert.True(await database.Files.ObjectExistsAsync(confirmedPath));
        Assert.True(await database.Files.ObjectExistsAsync(supplementPath));
        Assert.False(await database.Files.ObjectExistsAsync(orphanSupplement));
        Assert.Equal(allConfirmed, await database.Files.ObjectExistsAsync(otherPath));
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync(i => i.Id == firstId);
            Assert.Equal("synthetic-reviewer", item.ReviewerId);
            Assert.Equal("Retained explanation", item.RootCause);
            Assert.Equal(confirmedPath, Assert.Single(item.SourceRegions).SourceImagePath);
            using var analysis = JsonDocument.Parse(MistakeSource.Analysis(item)!);
            Assert.Equal("Synthetic question 1", analysis.RootElement.GetProperty("statement").GetString());
            Assert.Equal("3", analysis.RootElement.GetProperty("studentAnswer").GetString());
            Assert.Equal("2", analysis.RootElement.GetProperty("referenceAnswer").GetString());
            Assert.True(await db.ProcessLogs.AnyAsync(i => i.MistakeItemId == firstId));
            Assert.Equal(allConfirmed, await db.ProcessLogs.AnyAsync(i => i.MistakeItemId == secondId));
            Assert.Equal(practiceId, (await db.MistakePractices.SingleAsync()).Id);
            Assert.Empty(await db.ExtractionQuestions.ToListAsync());
            Assert.Empty(await db.ExtractionRevisions.ToListAsync());
            Assert.Empty(await db.ExtractionSupplements.ToListAsync());
        }
        database.Oss.Setup(o => o.DeleteExtractionOriginalAsync(original)).Returns(() => database.Oss.Object.DeleteIfExistsAsync(original));
        var requeued = await service.DeleteAsync(Admin, job.Id);
        Assert.Equal(receipt.DeletedAt, requeued.DeletedAt);
        Assert.Equal("Pending", requeued.Status);
        await service.RunCleanupAsync();
        Assert.False(await database.Files.ObjectExistsAsync(original));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
        Assert.Equal("job_deleted", (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.SubmitAsync(Caller, upload with { SourceKey = "changed-key", Grade = 7 }))).Code);
        Assert.Equal("source_conflict", (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.SubmitAsync(Caller, upload with { Images = [Image(9)] }))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAndZeroMistakeJobsCanBePurged(bool fail)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new(fail ? -1 : 0));
        var job = await service.SubmitImagesAsync(Caller, "student", [Image(1)]);
        await service.RunQueuedAsync(default);
        Assert.Equal(fail ? "Failed" : "Succeeded", (await service.GetAsync(Caller, job.Id)).Status);
        var receipt = await service.DeleteAsync(Admin, job.Id);
        Assert.Equal(0, receipt.DeletedMistakes);
        Assert.Equal(0, receipt.RetainedConfirmed);
        await service.RunCleanupAsync();
        Assert.False(await database.Files.ObjectExistsAsync(Assert.Single(job.Sources).Path));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
    }

    [Fact]
    public async Task LostStagingPutResponseDoesNotLeaveOriginalsOutsideCleanup()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        database.FailAfterPut = true;
        var service = Service(database, new(0));
        var upload = new ExtractionUpload("student", 4, "interrupted", [Image(1)]);
        await Assert.ThrowsAsync<IOException>(() => service.SubmitAsync(Caller, upload));
        var staged = Assert.Single(await service.ListAsync(Caller, "student"));
        Assert.Equal("Staging", staged.Status);
        var path = Assert.Single(staged.Sources).Path;
        Assert.True(await database.Files.ObjectExistsAsync(path));
        var receipt = await service.DeleteAsync(Admin, staged.Id);
        Assert.Equal(1, receipt.PendingObjects);
        await service.RunCleanupAsync();
        Assert.False(await database.Files.ObjectExistsAsync(path));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, staged.Id)).Status);
        Assert.Equal("job_deleted", (await Assert.ThrowsAsync<ExtractionException>(() => service.SubmitAsync(Caller, upload))).Code);
    }

    [Fact]
    public async Task ExactV21LegacyDigestSurvivesManifestDedupAndIsFencedAfterDeletion()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(WorksheetAnalysisSchema.Migration);
        var id = Guid.NewGuid();
        var image = Image(1);
        var sha = Convert.ToHexStringLower(SHA256.HashData(image.Content));
        var identity = new { image.ContentType, Hash = sha };
        var legacyHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { StudentId = "student", Grade = 6, Images = new[] { identity, identity } }, Json)));
        var sourceId = Guid.NewGuid();
        var path = $"mistakes/extractions/{id:N}/originals/{sourceId:N}.png";
        var manifest = JsonSerializer.Serialize(new[] { new ExtractionSource(sourceId, path, "image/png", image.Content.Length, sha, 20, 10, null) }, Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_job (id, caller, student_id, source_key, grade, payload_hash, status, sources_json, created_at, updated_at)
            VALUES ({id},'caller:synthetic-school','student','legacy',6,{legacyHash},'Failed',{manifest}::jsonb,now(),now())
            """);
        var upgrade = await DatabaseInitializer.UpgradeToTargetAsync(db);
        Assert.Equal(21, upgrade.FromVersion);
        Assert.Equal(28, upgrade.ToVersion);
        Assert.False(db.Database.HasPendingModelChanges());
        var service = Service(database, new(0));
        Assert.Equal(id, (await service.SubmitAsync(Caller, new("student", 6, "legacy", [image, image]))).Id);
        Assert.Equal(id, (await service.SubmitImagesAsync(Caller, "student", [image, image])).Id);
        await service.DeleteAsync(Admin, id);
        Assert.Equal("job_deleted", (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.SubmitImagesAsync(new("other", "other"), "student", [image, image]))).Code);
        Assert.NotEqual(id, (await service.SubmitImagesAsync(Caller, "student", [image])).Id);
        var other = await service.SubmitImagesAsync(Caller, "different-student", [image, image]);
        Assert.Null((await db.ExtractionJobs.SingleAsync(j => j.Id == other.Id)).Grade);
        var guard = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM extraction_job WHERE id={other.Id}"));
        Assert.Equal(PostgresErrorCodes.RaiseException, guard.SqlState);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE extraction_deletion"));
        Assert.Equal(PostgresErrorCodes.RaiseException, truncate.SqlState);
    }

    [Fact]
    public async Task OldWorkerCannotPublishAfterJobDisappears()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = Service(database, new(1, async () => { started.SetResult(); await release.Task.WaitAsync(stop.Token); }));
        var job = await service.SubmitImagesAsync(Caller, "student", [Image(1)], stop.Token);
        var worker = service.RunQueuedAsync(stop.Token);
        try
        {
            await started.Task.WaitAsync(stop.Token);
            await service.DeleteAsync(Admin, job.Id, stop.Token);
            await service.RunCleanupAsync(stop.Token);
        }
        finally { release.TrySetResult(); await worker.WaitAsync(stop.Token); }
        await using var db = database.CreateContext();
        Assert.False(await db.MistakeItems.AnyAsync(stop.Token));
        Assert.False(await db.ExtractionRevisions.AnyAsync(stop.Token));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id, stop.Token)).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetainedCropAndPracticeAttachmentRemainReadableWithOwnerAuthorization(bool legacyCrop, bool oldReceipt)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new(1));
        var staged = await service.SubmitAsync(Caller, new("student", 4, "archive-files", [Image(1)]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, staged.Id);
        var question = Assert.Single(job.Questions);
        var itemId = question.ItemId!.Value;
        var crop = Assert.Single(question.Images);
        var expectedCrop = (await service.ReadCropAsync(Caller, job.Id, question.Question.Id, job.Version, 0)).Bytes;
        var request = await service.RequestSupplementAsync(Caller, job.Id, question.Question.Id,
            new(Guid.NewGuid(), job.Version, itemId, "Synthetic practice attachment"));
        var attachment = Image(2);
        var supplement = await service.AppendSupplementAsync(Caller, job.Id, question.Question.Id, request.Id,
            new(Guid.NewGuid(), request.Version, itemId, [attachment]));
        job = await service.GetAsync(Caller, job.Id);
        var practiceId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync(i => i.Id == itemId);
            if (legacyCrop)
            {
                var path = $"mistakes/extractions/{job.Id:N}/versions/{Guid.NewGuid():N}/{question.Question.Id:N}/0-{crop.Sha256[..16]}.png";
                await database.Files.UploadAsync(expectedCrop, path.Split('/')[^1], "image/png", OssBucket.Mistakes,
                    path["mistakes/".Length..path.LastIndexOf('/')]);
                db.ExtractionObjects.Add(new() { Path = path, JobId = job.Id, Pending = false });
                await db.SaveChangesAsync();
                var current = await db.ExtractionRevisions.SingleAsync(r => r.JobId == job.Id && r.Version == job.Version);
                db.ExtractionRevisions.Add(new() { JobId = job.Id, Version = job.Version + 1, RequestKey = Guid.NewGuid(),
                    PayloadHash = current.PayloadHash, SnapshotJson = current.SnapshotJson.Replace(crop.Path, path),
                    Actor = "legacy-fixture", Reason = "Historical crop format", Model = current.Model, CreatedAt = DateTimeOffset.UtcNow });
                (await db.ExtractionJobs.SingleAsync(j => j.Id == job.Id)).Version++;
                item.SourceRegions.Single().SourceImagePath = path;
            }
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            db.MistakePractices.Add(new() { Id = practiceId, MistakeItemId = itemId, StudentId = "student",
                ImagePaths = [Assert.Single(supplement.Sources).Path] });
            await db.SaveChangesAsync();
        }
        await service.DeleteAsync(Admin, job.Id);
        await service.RunCleanupAsync();
        if (oldReceipt)
        {
            await using var db = database.CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mistake_item SET source_detail=source_detail-'retainedFiles' WHERE id={itemId}");
        }
        Assert.Equal(expectedCrop, await service.ReadRetainedCropAsync(Caller, itemId, 0, default));
        Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.ReadRetainedCropAsync(new("other", "other"), itemId, 0, default))).Status);
        using var root = new ExtractionHostFixture(database.Connection, callerName: Caller.Caller);
        using var factory = root.WithWebHostBuilder(builder => Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder,
            services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IOssService>(services, database.Oss.Object)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        var route = $"/api/intake/extractions/retained/{itemId}/practices/{practiceId}/images/0";
        using var response = await client.GetAsync(route);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(attachment.Content, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await client.GetAsync(route.Replace(practiceId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.ReadRetainedPracticeImageAsync(new("other", "other"), itemId, practiceId, 0, default))).Status);
        await using (var db = database.CreateContext())
        {
            var path = (await db.MistakeItems.SingleAsync(i => i.Id == itemId)).SourceRegions.Single().SourceImagePath;
            await database.Files.UploadAsync(Image(9).Content, path.Split('/')[^1], "image/png", OssBucket.Mistakes,
                path["mistakes/".Length..path.LastIndexOf('/')]);
        }
        Assert.Equal(503, (await Assert.ThrowsAsync<ExtractionException>(() =>
            service.ReadRetainedCropAsync(Caller, itemId, 0, default))).Status);
    }

    [Fact]
    public async Task PracticesStagedBeforeArchivalAreRevalidatedAndUnreadableAttachmentsReturnConflict()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database, new(2));
        var staged = await service.SubmitAsync(Caller, new("student", 4, "late-practice", [Image(1)]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(Caller, staged.Id);
        var first = job.Questions[0];
        var second = job.Questions[1];
        await using (var db = database.CreateContext())
        {
            foreach (var item in await db.MistakeItems.ToListAsync()) item.ReviewStatus = (int)ReviewStatus.Confirmed;
            await db.SaveChangesAsync();
        }
        var expected = (await service.ReadCropAsync(Caller, job.Id, first.Question.Id, job.Version, 0)).Bytes;
        await using var lateWriter = database.CreateContext();
        await lateWriter.MistakeItems.SingleAsync(i => i.Id == first.ItemId); // tracked before archival
        lateWriter.MistakePractices.Add(new() { Id = Guid.NewGuid(), MistakeItemId = first.ItemId!.Value,
            StudentId = "student", ImagePaths = [Assert.Single(second.Images).Path] });
        await service.DeleteAsync(Admin, job.Id);
        var conflict = await Assert.ThrowsAsync<Emenda.Domain.Services.MistakeConflictException>(() =>
            new Emenda.Database.Repositories.UnitOfWork(lateWriter).SaveChangesAsync());
        Assert.Equal("retained_image_not_recorded", conflict.Code);

        using var root = new ExtractionHostFixture(database.Connection, callerName: Caller.Caller);
        using var factory = root.WithWebHostBuilder(builder => Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder,
            services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IOssService>(services, database.Oss.Object)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        var route = $"/api/mistakes/{first.ItemId}/practices";
        using var denied = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, route,
            new { studentId = "student", imagePaths = new[] { second.Images[0].Path } });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, denied.StatusCode);
        using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("retained_image_not_recorded", error.RootElement.GetProperty("errorKind").GetString());
        Emenda.HttpContracts.MistakeHttpContract.ValidateResponse("POST", route, (int)denied.StatusCode, error.RootElement);
        using var accepted = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, route,
            new { studentId = "student", imagePaths = new[] { first.Images[0].Path } });
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
        using var result = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var practiceId = result.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var image = await client.GetByteArrayAsync($"/api/intake/extractions/retained/{first.ItemId}/practices/{practiceId}/images/0");
        Assert.Equal(expected, image);
        await using var stored = database.CreateContext();
        Assert.Single(await stored.MistakePractices.ToListAsync());
    }

    private static ExtractionService Service(PostgresTestDatabase db, Analyzer analyzer) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, analyzer,
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true }), NullLogger<ExtractionService>.Instance);

    private static ExtractionImage Image(int n)
    {
        using var image = new ImageMagick.MagickImage(ExtractionHttpPostgresTests.TestPng());
        image.Resize(20, 10);
        image.Colorize(new ImageMagick.MagickColor((byte)(n * 23), 40, 80), new ImageMagick.Percentage(100));
        return new() { FileName = "synthetic.png", ContentType = "image/png", Content = image.ToByteArray(ImageMagick.MagickFormat.Png) };
    }

    private sealed class Analyzer(int count, Func<Task>? before = null) : IQuestionAnalysisClient
    {
        public async Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            if (before is not null) await before();
            if (count == -1) throw new ExtractionException("synthetic_failure");
            return new("synthetic", Enumerable.Range(0, count).Select(index => new ExtractionQuestionDraft
            {
                Subject = 2, Grade = 5, Statement = $"Synthetic question {index + 1}", StudentAnswer = "3", ReferenceAnswer = "2",
                Evidence = "Visible answer 3 differs from 2", Explanation = "Add one and one", Outcome = "incorrect",
                Regions = [new(request.Images[0].SourceId, index / 2.0, 0, (index + 1) / 2.0, 1)]
            }).ToList());
        }
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "synthetic", Width = 20, Height = 10,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
