using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Database.Repositories;
using Emenda.Host.Security;
using Emenda.Host.Services.Conversation;
using Emenda.HttpContracts;
using Emenda.TestFixtures;
using ImageMagick;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Emenda.Service.Tests;

public sealed class ConversationIsolationHttpPostgresTests
{
    private const string Caller = "intake-tests";
    private const string CallerKey = "intake-test-api-key-0123456789";
    private const string OtherCaller = "conversation-other";
    private const string OtherKey = "conversation-other-test-api-key";
    private const string Student = "opaque-conversation-learner";

    [Fact]
    public async Task WorkerCommittedConversationListsAndChildReadsRespectProductCallerWithoutUserClaims()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var db = f.Database.CreateContext())
        {
            db.MistakePractices.Add(new() { MistakeItemId = f.ItemId, StudentId = Student, StudentAnswer = "private attempt" });
            db.ProcessLogs.Add(new() { MistakeItemId = f.ItemId, StudentId = Student, Action = 1, Details = "private history" });
            await db.SaveChangesAsync();
        }
        using var own = f.Client(CallerKey); using var foreign = f.Client(OtherKey);
        var listPath = $"/api/mistakes?studentId={Student}&subject=0&grade=0&reviewStatus=4&page=1&size=20";
        var ownList = await Data(own, listPath);
        Assert.Single(ownList.GetProperty("items").EnumerateArray());
        Assert.Equal(1, ownList.GetProperty("pageMeta").GetProperty("totalCount").GetInt32());
        var foreignList = await Data(foreign, listPath);
        Assert.Empty(foreignList.GetProperty("items").EnumerateArray());
        Assert.Equal(0, foreignList.GetProperty("pageMeta").GetProperty("totalCount").GetInt32());
        using var ownDetail = await own.GetAsync($"/api/mistakes/{f.ItemId:D}");
        Assert.Equal(HttpStatusCode.OK, ownDetail.StatusCode);
        using var foreignDetail = await foreign.GetAsync($"/api/mistakes/{f.ItemId:D}");
        Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);
        foreach (var (route, field) in new[] { ("practices", "practices"), ("process-logs", "logs") })
        {
            Assert.Single((await Data(own, $"/api/mistakes/{f.ItemId:D}/{route}")).GetProperty(field).EnumerateArray());
            Assert.Empty((await Data(foreign, $"/api/mistakes/{f.ItemId:D}/{route}")).GetProperty(field).EnumerateArray());
        }
        using var foreignAnalysis = await foreign.GetAsync($"/api/mistakes/{f.ItemId:D}/analysis");
        Assert.Equal(HttpStatusCode.NotFound, foreignAnalysis.StatusCode);

        // Null scope is the trusted background path; nonconversation rows retain legacy visibility.
        await using var legacyDb = f.Database.CreateContext();
        var legacy = new MistakeItemEntity { StudentId = Student, Subject = 2, Grade = 3, ReviewStatus = 1, CreatedAt = DateTimeOffset.UtcNow };
        legacyDb.MistakeItems.Add(legacy);
        legacyDb.MistakePractices.Add(new() { MistakeItemId = legacy.Id, StudentId = Student });
        legacyDb.ProcessLogs.Add(new() { MistakeItemId = legacy.Id, StudentId = Student, Action = 1 });
        await legacyDb.SaveChangesAsync();
        Assert.Equal(2, await legacyDb.MistakePractices.CountAsync());
        legacyDb.ChangeTracker.Clear(); legacyDb.ConversationCallerScope = OtherCaller;
        Assert.Single(await legacyDb.MistakePractices.Include(p => p.MistakeItem).ToListAsync());
        Assert.Single(await legacyDb.ProcessLogs.Include(p => p.MistakeItem).ToListAsync());
    }

    [Fact]
    public async Task ReanalysisReadsHideForeignAndDeletedConversationProvenanceButRetainDeletedLegacyHistory()
    {
        await using var f = await Fixture.CreateAsync();
        var conversationJob = new ReanalyzeJobEntity { ItemId = f.ItemId, StudentId = Student, ReviewerId = "teacher", NewRootCause = "private conversation report" };
        var legacyJob = new ReanalyzeJobEntity { ItemId = Guid.NewGuid(), StudentId = Student, ReviewerId = "teacher", NewRootCause = "retained legacy report" };
        await using (var db = f.Database.CreateContext())
        {
            db.ReanalyzeJobs.AddRange(conversationJob, legacyJob); await db.SaveChangesAsync();
            db.ConversationCallerScope = OtherCaller;
            Assert.Null(await new ReanalyzeJobRepository(db).GetByIdAsync(conversationJob.Id));
            Assert.NotNull(await new ReanalyzeJobRepository(db).GetByIdAsync(legacyJob.Id));
        }
        using var own = f.Client(CallerKey); using var foreign = f.Client(OtherKey);
        var jobsPath = "/api/mistakes/reanalyze/jobs";
        var request = new { reviewerId = "teacher", jobIds = new[] { conversationJob.Id.ToString("D"), legacyJob.Id.ToString("D") } };
        Assert.Equal(2, (await PostData(own, jobsPath, request)).GetProperty("jobs").GetArrayLength());
        var foreignJobs = (await PostData(foreign, jobsPath, request)).GetProperty("jobs");
        Assert.Single(foreignJobs.EnumerateArray());
        Assert.Equal(legacyJob.Id.ToString("D"), foreignJobs[0].GetProperty("jobId").GetString());
        Assert.Single((await Data(foreign, "/api/mistakes/reanalyze/active?reviewerId=teacher")).GetProperty("jobs").EnumerateArray());
        await using (var db = f.Database.CreateContext())
        {
            db.MistakeItems.Remove(await db.MistakeItems.SingleAsync()); await db.SaveChangesAsync();
            Assert.NotNull(await new ReanalyzeJobRepository(db).GetByIdAsync(conversationJob.Id));
            db.ConversationCallerScope = Caller;
            Assert.Null(await new ReanalyzeJobRepository(db).GetByIdAsync(conversationJob.Id));
            Assert.NotNull(await new ReanalyzeJobRepository(db).GetByIdAsync(legacyJob.Id));
        }
        Assert.Single((await PostData(own, jobsPath, request)).GetProperty("jobs").EnumerateArray());
        Assert.Single((await Data(own, "/api/mistakes/reanalyze/active?reviewerId=teacher")).GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task PresigningRequiresCanonicalLiveCallerOwnedOriginalOrCropReference()
    {
        await using var f = await Fixture.CreateAsync();
        using var own = f.Client(CallerKey); using var foreign = f.Client(OtherKey);
        Assert.Equal(2, f.Images.Length);
        foreach (var path in f.Images)
        {
            using var accepted = await own.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = path });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            using var denied = await foreign.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = path });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Equal(2, f.Signed.Count);
        var original = f.Images[0];
        var unreferenced = original[..(original.LastIndexOf('/') + 1)] + new string('e', 64) + ".png";
        await f.Database.Files.UploadAsync([1, 2, 3], new string('e', 64) + ".png", "image/png", OssBucket.Mistakes,
            unreferenced["mistakes/".Length..unreferenced.LastIndexOf('/')]);
        await using (var db = f.Database.CreateContext())
        {
            db.ConversationCollections.Add(new() { Id = Guid.NewGuid(), CallerName = Caller, ActorId = "account", StudentId = Student, OwnerId = "owner", Grade = 3,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
                StagedRegions = JsonSerializer.Serialize(new[] { new SourceRegionEntry { SourceImagePath = unreferenced } }, ConversationExport.Json) });
            await db.SaveChangesAsync();
        }
        var variants = new[] { unreferenced, "/" + original, original.Replace('/', '\\'), original.Replace("/conversation/", "/./conversation/"),
            original.Replace("/conversation/", "/extra/../conversation/"), original.Replace("/conversation/", "/%63onversation/"),
            original.Replace("mistakes/", "MISTAKES/"), original.Replace("/conversation/", "/conversation/../conversation/") };
        foreach (var path in variants)
        {
            using var denied = await own.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = path });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var thumbnail = await own.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = original, size = "small" });
        Assert.Equal(HttpStatusCode.BadRequest, thumbnail.StatusCode);
        await using (var db = f.Database.CreateContext()) { db.MistakeItems.Remove(await db.MistakeItems.SingleAsync()); await db.SaveChangesAsync(); }
        using var deleted = await own.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = original });
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        Assert.Equal(2, f.Signed.Count);
    }

    private static async Task<JsonElement> Data(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("data").Clone();
    }
    private static async Task<JsonElement> PostData(HttpClient client, string path, object request)
    {
        using var response = await client.PostAsJsonAsync(path, request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("data").Clone();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ProductIntakePostgresTests.TestDatabase Database { get; init; }
        private StandaloneIntakePostgresTests.StandaloneIntakeWebApplicationFactory BaseFactory { get; init; } = null!;
        private WebApplicationFactory<Program> Factory { get; init; } = null!;
        public ConcurrentBag<string> Signed { get; } = [];
        public Guid ItemId { get; private set; }
        public string[] Images { get; private set; } = [];
        public HttpClient Client(string key)
        {
            var client = Factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", key); return client;
        }
        public static async Task<Fixture> CreateAsync()
        {
            var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
            var baseFactory = new StandaloneIntakePostgresTests.StandaloneIntakeWebApplicationFactory(database.Connection);
            var authorization = new Mock<IConversationAuthorization>();
            authorization.Setup(a => a.AllowedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var owner = "portal-owned-context"; var conversation = Guid.NewGuid(); var question = Guid.NewGuid(); var assetId = Guid.NewGuid();
            using var raster = RasterFixture.Create(8, 6); var original = raster.ToByteArray(MagickFormat.Png);
            using var decoded = GuardedImageDecoder.LoadQuestion(original); var crop = decoded.CropQuestion(0, 0, 500, 1000);
            var eventId = ConversationExport.Hash($"{ConversationExport.Hash($"{owner}\n{conversation:D}\n0")}\n{question:D}");
            var packet = new ConversationExport(1, conversation,
                [new(eventId, 0, new(question, 2, "2 + 2 = ?", [0], [assetId], new ConversationStudentAttempt(0, "5", null), "incorrect", "The answer is 4, not 5", "4", []))],
                [new(assetId, 0, new(0, 0, 500, 1000), crop.Width, crop.Height, "data:image/jpeg;base64," + Convert.ToBase64String(crop.Data))],
                [new(0, "2 + 2 = 5", "data:image/png;base64," + Convert.ToBase64String(original))]);
            var fingerprint = ConversationExport.Hash(JsonSerializer.Serialize(packet, ConversationExport.Json));
            var exports = new Mock<IConversationExportReader>();
            exports.Setup(e => e.ReadAsync(It.IsAny<Guid>(), owner, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string _, bool checkOnly, CancellationToken _) => new ConversationCollectionResponse("completed", checkOnly ? null : packet, fingerprint));
            var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.Configure<ProductCallerAuthenticationOptions>(options => options.Callers.Add(new() { Name = OtherCaller, ApiKey = OtherKey }));
                services.RemoveAll<IOssService>(); services.AddSingleton(database.Oss.Object);
                services.RemoveAll<IConversationAuthorization>(); services.AddSingleton(authorization.Object);
                services.RemoveAll<IConversationExportReader>(); services.AddSingleton(exports.Object);
            }));
            var fixture = new Fixture { Database = database, BaseFactory = baseFactory, Factory = factory };
            database.Oss.Setup(o => o.GetPresignedUrlAsync(It.IsAny<string>(), It.IsAny<int>())).Returns((string path, int _) =>
            { fixture.Signed.Add(path); return Task.FromResult("https://oss.example/authorized-read"); });
            try
            {
                await using (var scope = factory.Services.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<ConversationCollectionStore>().AdmitAsync(Caller, "account", Guid.NewGuid(), Student, owner, 3, CancellationToken.None);
                var worker = new ConversationCollectionWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConversationCollectionWorker>.Instance, new() { CallerName = Caller });
                await worker.RunOneAsync(CancellationToken.None);
                await using var db = database.CreateContext();
                Assert.Equal("completed", (await db.ConversationCollections.SingleAsync()).Status);
                var item = await db.MistakeItems.SingleAsync(); fixture.ItemId = item.Id;
                fixture.Images = item.SourceRegions.Select(region => region.SourceImagePath).ToArray();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync(); await BaseFactory.DisposeAsync(); await Database.DisposeAsync();
        }
    }
}
