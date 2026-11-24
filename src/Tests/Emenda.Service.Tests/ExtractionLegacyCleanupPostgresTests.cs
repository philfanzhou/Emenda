using System.Security.Cryptography;
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

namespace Emenda.Service.Tests;

public sealed class ExtractionLegacyCleanupPostgresTests
{
    private static readonly ExtractionAccess Caller = new("synthetic-school", "synthetic-teacher");
    private static readonly ExtractionAccess Admin = new("admin", "synthetic-admin", true);
    private static readonly byte[] StoredBytes = [1, 2, 3, 4];
    private static readonly string StoredHash = Convert.ToHexStringLower(SHA256.HashData(StoredBytes));

    [Fact]
    public async Task DeletionInventoriesUntrackedPreV20CropsBeforeRemovingTheirJobAndRevision()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database);
        var job = await SubmitAsync(service);
        var orphans = new[]
        {
            LegacyCrop(job.Id),
            $"mistakes/extractions/{job.Id:N}/versions/{Guid.NewGuid():N}/{new string('b', 64)}-{new string('c', 64)}.png",
            $"mistakes/extractions/{job.Id:N}/originals/{Guid.NewGuid():N}.jpg",
            $"mistakes/extractions/{job.Id:N}/supplements/{Guid.NewGuid():N}/originals/{Guid.NewGuid():N}.png"
        };
        foreach (var path in orphans) await PutAsync(database, path);
        var inventoried = false;
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(job.Id))
            .Returns(async (Guid id) =>
            {
                await using var db = database.CreateContext();
                Assert.True(await db.ExtractionJobs.AnyAsync(j => j.Id == job.Id));
                Assert.True(await db.ExtractionRevisions.AnyAsync(r => r.JobId == job.Id));
                inventoried = true;
                return await database.Files.ListExtractionObjectsAsync(id);
            });

        var deletion = await service.DeleteAsync(Admin, job.Id);
        Assert.True(inventoried);
        Assert.Equal("Pending", deletion.Status);
        await using (var db = database.CreateContext())
        {
            foreach (var path in orphans)
            {
                var entry = await db.ExtractionObjects.SingleAsync(o => o.Path == path);
                Assert.False(entry.Pending);
                Assert.False(entry.Legacy);
            }
        }
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => database.Files.ListExtractionObjectsAsync(id));

        await service.RunCleanupAsync();

        foreach (var path in orphans) Assert.False(await database.Files.ObjectExistsAsync(path));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
    }

    [Fact]
    public async Task ExistingDeletedReceiptRediscoversOrphansInsteadOfReportingCompleted()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database);
        var job = await SubmitAsync(service);
        await service.DeleteAsync(Admin, job.Id);
        var lateOrphan = LegacyCrop(job.Id);
        await PutAsync(database, lateOrphan);
        await service.RunCleanupAsync();
        Assert.False(await database.Files.ObjectExistsAsync(lateOrphan));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
        var orphan = LegacyCrop(job.Id);
        await PutAsync(database, orphan);

        var deletion = await service.GetDeletionAsync(Admin, job.Id);

        Assert.Equal("Pending", deletion.Status);
        Assert.Equal(1, deletion.PendingObjects);
        await service.RunCleanupAsync();
        Assert.False(await database.Files.ObjectExistsAsync(orphan));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
    }

    [Fact]
    public async Task InventoryDoesNotRetireObjectsReferencedByConfirmedItemsOrPractice()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database);
        var job = await SubmitAsync(service);
        var itemId = Assert.Single(job.Questions).ItemId!.Value;
        var retainedCrop = LegacyCrop(job.Id);
        var practiceCrop = LegacyCrop(job.Id);
        await PutAsync(database, retainedCrop);
        await PutAsync(database, practiceCrop);
        await using (var db = database.CreateContext())
        {
            foreach (var path in new[] { retainedCrop, practiceCrop })
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO extraction_object(path,job_id,pending,legacy,attempts,next_attempt) VALUES ({path},{job.Id},false,false,0,clock_timestamp())");
            var item = await db.MistakeItems.SingleAsync(i => i.Id == itemId);
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            Assert.Single(item.SourceRegions).SourceImagePath = retainedCrop;
            db.MistakePractices.Add(new() { Id = Guid.NewGuid(), MistakeItemId = itemId, StudentId = "student", ImagePaths = [practiceCrop] });
            await db.SaveChangesAsync();
            // Reproduce historical references whose old snapshot/queue inventory was lost.
            await db.ExtractionObjects.Where(o => o.Path == retainedCrop || o.Path == practiceCrop).ExecuteDeleteAsync();
        }

        await service.DeleteAsync(Admin, job.Id);
        await service.RunCleanupAsync();

        Assert.True(await database.Files.ObjectExistsAsync(retainedCrop));
        Assert.True(await database.Files.ObjectExistsAsync(practiceCrop));
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
        database.Oss.Verify(o => o.DeleteDerivedAsync(retainedCrop), Times.Never);
        database.Oss.Verify(o => o.DeleteDerivedAsync(practiceCrop), Times.Never);
    }

    [Fact]
    public async Task FailedDeletionDoesNotQueueUntrackedObjectsOrEnableLegacyCleanup()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database);
        var job = await SubmitAsync(service);
        var itemId = Assert.Single(job.Questions).ItemId!.Value;
        var orphan = LegacyCrop(job.Id);
        var retiredLegacy = LegacyCrop(job.Id);
        var unsupportedReference = $"mistakes/extractions/{job.Id:N}/versions/{Guid.NewGuid():N}/unsupported.png";
        foreach (var path in new[] { orphan, retiredLegacy, unsupportedReference }) await PutAsync(database, path);
        await using (var db = database.CreateContext())
        {
            foreach (var path in new[] { retiredLegacy, unsupportedReference })
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO extraction_object(path,job_id,pending,legacy,attempts,next_attempt) VALUES ({path},{job.Id},false,true,0,clock_timestamp())");
            var item = await db.MistakeItems.SingleAsync(i => i.Id == itemId);
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            Assert.Single(item.SourceRegions).SourceImagePath = unsupportedReference;
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ExtractionException>(() => service.DeleteAsync(Admin, job.Id));

        Assert.Equal(409, error.Status);
        Assert.Equal("retained_image_metadata_unavailable", error.Code);
        var retained = await service.GetAsync(Caller, job.Id);
        Assert.Equal(job.Version, retained.Version);
        Assert.Equal("Succeeded", retained.Status);
        Assert.Equal(itemId, Assert.Single(retained.Questions).ItemId);
        await using (var db = database.CreateContext())
        {
            Assert.False(await db.ExtractionDeletions.AnyAsync(d => d.JobId == job.Id));
            Assert.True(await db.ExtractionRevisions.AnyAsync(r => r.JobId == job.Id && r.Version == job.Version));
            Assert.False(await db.ExtractionObjects.AnyAsync(o => o.Path == orphan));
            foreach (var path in new[] { retiredLegacy, unsupportedReference })
            {
                var entry = await db.ExtractionObjects.SingleAsync(o => o.Path == path);
                Assert.True(entry.Legacy);
                Assert.False(entry.Pending);
                Assert.Equal(0, entry.Attempts);
            }
            Assert.Equal(unsupportedReference, Assert.Single((await db.MistakeItems.SingleAsync(i => i.Id == itemId)).SourceRegions).SourceImagePath);
        }

        await service.RunCleanupAsync();

        foreach (var path in new[] { orphan, retiredLegacy, unsupportedReference })
        {
            Assert.True(await database.Files.ObjectExistsAsync(path));
            database.Oss.Verify(o => o.DeleteDerivedAsync(path), Times.Never);
        }
    }

    [Fact]
    public async Task FailedListingKeepsJobMetadataAndCannotCompleteAnExistingDeletion()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database);
        var job = await SubmitAsync(service);
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(It.IsAny<Guid>())).ThrowsAsync(new IOException("injected listing failure"));

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(Admin, job.Id));
        Assert.Equal("Succeeded", (await service.GetAsync(Caller, job.Id)).Status);
        await using (var db = database.CreateContext())
        {
            Assert.True(await db.ExtractionRevisions.AnyAsync(r => r.JobId == job.Id));
            Assert.False(await db.ExtractionDeletions.AnyAsync(d => d.JobId == job.Id));
        }
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => database.Files.ListExtractionObjectsAsync(id));
        await service.DeleteAsync(Admin, job.Id);
        await service.RunCleanupAsync();
        var orphan = LegacyCrop(job.Id);
        await PutAsync(database, orphan);
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(It.IsAny<Guid>())).ThrowsAsync(new IOException("injected listing failure"));

        await Assert.ThrowsAsync<IOException>(() => service.GetDeletionAsync(Admin, job.Id));
        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(Admin, job.Id));
        Assert.True(await database.Files.ObjectExistsAsync(orphan));
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => database.Files.ListExtractionObjectsAsync(id));
        Assert.Equal("Pending", (await service.GetDeletionAsync(Admin, job.Id)).Status);
        await service.RunCleanupAsync();
        Assert.False(await database.Files.ObjectExistsAsync(orphan));
    }

    [Fact]
    public async Task InventoryRejectsForeignTaskAndUnknownOrTraversalKeyShapes()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var service = Service(database);
        var job = await SubmitAsync(service);
        var orphan = LegacyCrop(job.Id);
        var prefix = $"mistakes/extractions/{job.Id:N}/";
        var foreign = LegacyCrop(Guid.NewGuid());
        var unknown = prefix + $"versions/{Guid.NewGuid():N}/attachments/keep.png";
        var traversal = prefix + $"versions/{Guid.NewGuid():N}/../0-{new string('a', 16)}.png";
        var wrongTask = prefix.TrimEnd('/') + "-other/originals/keep.png";
        await PutAsync(database, orphan);
        await PutAsync(database, foreign);
        await PutAsync(database, unknown);
        database.Oss.Setup(o => o.ListExtractionObjectsAsync(job.Id)).Returns(async () =>
        {
            var objects = await database.Files.ListExtractionObjectsAsync(job.Id);
            objects.AddRange(new[] { foreign, traversal, wrongTask }.Select(path => new OssObjectInfo { ObjectPath = path }));
            return objects;
        });

        await service.DeleteAsync(Admin, job.Id);
        await service.RunCleanupAsync();

        Assert.False(await database.Files.ObjectExistsAsync(orphan));
        Assert.True(await database.Files.ObjectExistsAsync(foreign));
        Assert.True(await database.Files.ObjectExistsAsync(unknown));
        foreach (var path in new[] { foreign, unknown, traversal, wrongTask })
        {
            database.Oss.Verify(o => o.DeleteDerivedAsync(path), Times.Never);
            database.Oss.Verify(o => o.DeleteExtractionOriginalAsync(path), Times.Never);
        }
        Assert.Equal("Completed", (await service.GetDeletionAsync(Admin, job.Id)).Status);
    }

    private static string LegacyCrop(Guid jobId) =>
        $"mistakes/extractions/{jobId:N}/versions/{Guid.NewGuid():N}/{Guid.NewGuid():N}/0-{StoredHash[..16]}.png";

    private static async Task PutAsync(PostgresTestDatabase database, string path)
    {
        var separator = path.LastIndexOf('/');
        await database.Files.UploadAsync(StoredBytes, path[(separator + 1)..], "image/png",
            OssBucket.Mistakes, path["mistakes/".Length..separator]);
    }

    private static ExtractionService Service(PostgresTestDatabase db) => new(
        new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(db.Connection).Options, db.Oss.Object, new Analyzer(),
        new LayoutClient(), Options.Create(new ExtractionOptions { Enabled = true, CleanupLegacyObjects = false }), NullLogger<ExtractionService>.Instance);

    private static async Task<ExtractionJobView> SubmitAsync(ExtractionService service)
    {
        var image = new ExtractionImage { FileName = "synthetic.png", ContentType = "image/png", Content = ExtractionHttpPostgresTests.TestPng() };
        var staged = await service.SubmitImagesAsync(Caller, "student", [image]);
        await service.RunQueuedAsync(default);
        return await service.GetAsync(Caller, staged.Id);
    }

    private sealed class Analyzer : IQuestionAnalysisClient
    {
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default) =>
            Task.FromResult(new QuestionAnalysisResult("synthetic", [new ExtractionQuestionDraft
            {
                Subject = 2, Grade = 5, Statement = "Synthetic question", StudentAnswer = "3", ReferenceAnswer = "2",
                Evidence = "Visible answer differs", Explanation = "Add one and one", Outcome = "incorrect",
                Regions = [new(request.Images[0].SourceId, 0, 0, 1, 1)]
            }]));
    }

    private sealed class LayoutClient : ILayoutDetectionClient
    {
        public Task<LayoutDetectionResult> DetectLayoutAsync(LayoutDetectionImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LayoutDetectionResult { RequestId = "synthetic", Width = 2, Height = 1,
                Regions = [new(0, "question", new(0, 0, 1, 1))] });
    }
}
