using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Repositories;
using Emenda.Domain.Models;
using Emenda.Host.Services.Conversation;
using Emenda.HttpContracts;

namespace Emenda.Service.Tests;

public sealed class ConversationCollectionPostgresTests
{
    [Fact]
    public async Task TextEvidenceCollectsOnceAcrossOperationsAndDeletionCannotBeReplayed()
    {
        await using var f = await Fixture.CreateAsync();
        var packet = f.Packet();
        await f.Receive(packet); await f.Receive(packet);
        await using (var db = f.Open())
        {
            Assert.Equal(1, await db.MistakeItems.CountAsync()); Assert.Equal(1, await db.ConversationMistakeReceipts.CountAsync());
            var item = await db.MistakeItems.SingleAsync(); Assert.Empty(item.SourceUploadId); Assert.Empty(item.SourceRegions); Assert.True(item.ConversationActive);
            var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
            Assert.Equal("2 + 2 = ?", snapshot.Statement); Assert.Equal("5", snapshot.StudentAttempt!.Quote);
            // The unified shape is materialized on the same write, and the history starts with one Created entry.
            Assert.Equal(MistakeSource.Conversation, item.SourceKind); Assert.NotNull(item.SourceCaller);
            using (var content = JsonDocument.Parse(item.Content)) Assert.Equal("5", content.RootElement.GetProperty("studentAnswer").GetString());
            var created = Assert.Single(await db.ProcessLogs.Where(l => l.MistakeItemId == item.Id).ToListAsync());
            Assert.Equal((int)ProcessAction.Created, created.Action); Assert.Contains("conversation_collected", created.Details);
            var repository = new MistakeItemRepository(db);
            Assert.Single((await repository.GetListAsync(f.Student, 0, 0, 4, 1, 20)).Items);
            Assert.Empty((await repository.GetListAsync(f.Student, 0, 0, 2, 1, 20)).Items);
            db.MistakeItems.Remove(item); await db.SaveChangesAsync();
        }
        await f.Receive(f.Packet(position: 2));
        await using var recheck = f.Open(); Assert.Empty(await recheck.MistakeItems.ToListAsync());
        Assert.Contains(await recheck.ConversationMistakeReceipts.ToListAsync(), r => r.Status == "deleted");
    }

    [Fact]
    public async Task MerelyAskingDoesNotCollectAndOlderWorkCannotOverwriteACorrection()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Receive(f.Packet(position: 0, outcome: "unknown", attempt: false));
        await using (var db = f.Open()) Assert.Empty(await db.MistakeItems.ToListAsync());
        await f.Receive(f.Packet(position: 2));
        await f.Receive(f.Packet(position: 4, outcome: "correct", retract: true));
        await f.Receive(f.Packet(position: 3));
        await using var recheck = f.Open(); var item = await recheck.MistakeItems.SingleAsync();
        Assert.False(item.ConversationActive); Assert.Equal(4, JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!.Position);
        Assert.Empty((await new MistakeItemRepository(recheck).GetListAsync(f.Student, 0, 0, 4, 1, 20)).Items);
    }

    [Fact]
    public async Task UnansweredPhotoCollectsNoMistakeEvenWithAValidPreparedCrop()
    {
        await using var f = await Fixture.CreateAsync();
        const string original = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";
        using var decoded = GuardedImageDecoder.LoadQuestion(Convert.FromBase64String(original.Split(',')[1]));
        var crop = decoded.CropQuestion(0, 0, 1000, 1000);
        var asset = new ConversationExportAsset(Guid.NewGuid(), 0, new(0, 0, 1000, 1000), crop.Width, crop.Height,
            "data:image/jpeg;base64," + Convert.ToBase64String(crop.Data));
        var packet = f.Packet(outcome: "unknown", attempt: false);
        var item = packet.Events[0];
        await f.Receive(packet with
        {
            Events = [item with { Question = item.Question with { AssetIds = [asset.Id], KnowledgePoints = ["Addition"] } }],
            Assets = [asset], Sources = [new(0, "Please tell me the answer", original)]
        });
        await using var db = f.Open();
        Assert.Empty(await db.MistakeItems.ToListAsync());
        Assert.Equal("skipped", (await db.ConversationMistakeReceipts.SingleAsync()).Status);
        Assert.Equal("completed", (await db.ConversationCollections.SingleAsync()).Status);
    }

    [Fact]
    public async Task RevocationAndFabricatedQuotesAreDeniedBeforeMaterialization()
    {
        await using var f = await Fixture.CreateAsync();
        var packet = f.Packet();
        var fabricated = packet with { Sources = [new(0, "I have not answered", null)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Receive(fabricated));
        f.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Receive(packet));
        await using var db = f.Open(); Assert.Empty(await db.MistakeItems.ToListAsync()); Assert.Empty(await db.ConversationMistakeReceipts.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterUnknownOrCorrectObservationsCannotSuppressAnEarlierRealMistake(bool correct)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Receive(f.Packet(position: 2, outcome: correct ? "correct" : "unknown", attempt: correct, answer: "4", attemptPosition: 2));
        await f.Receive(f.Packet());
        await using var db = f.Open(); var item = await db.MistakeItems.SingleAsync();
        Assert.True(item.ConversationActive);
        var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
        Assert.Equal("5", snapshot.StudentAttempt!.Quote);
        Assert.Contains(snapshot.Attempts, a => a.Attempt.Quote == "5" && a.Outcome == "incorrect");
        if (correct) Assert.Contains(snapshot.Attempts, a => a.Attempt.Quote == "4" && a.Outcome == "correct");
        Assert.All(await db.ConversationMistakeReceipts.ToListAsync(), r => Assert.Equal("{}", r.Observation));
    }

    [Fact]
    public async Task LaterCorrectAnswerIsRetainedAlongsideTheOriginalIncorrectAttempt()
    {
        await using var f = await Fixture.CreateAsync(); await f.Receive(f.Packet());
        await f.Receive(f.Packet(position: 2, outcome: "correct", answer: "4", attemptPosition: 2));
        await using var db = f.Open(); var item = await db.MistakeItems.SingleAsync();
        var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
        Assert.True(item.ConversationActive); Assert.Equal("5", snapshot.StudentAttempt!.Quote);
        Assert.Collection(snapshot.Attempts, a => Assert.Equal("5", a.Attempt.Quote), a => { Assert.Equal("4", a.Attempt.Quote); Assert.Equal("correct", a.Outcome); });
    }

    [Fact]
    public async Task TeacherEditsAndConfirmedDecisionsAreNotOverwrittenByLateAutomaticWork()
    {
        await using var f = await Fixture.CreateAsync(); await f.Receive(f.Packet());
        await using (var db = f.Open())
        {
            var item = await db.MistakeItems.SingleAsync(); item.RootCause = "Teacher correction"; item.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1); await db.SaveChangesAsync();
        }
        await f.Receive(f.Packet(position: 2));
        await using var recheck = f.Open(); Assert.Equal("Teacher correction", (await recheck.MistakeItems.SingleAsync()).RootCause);
        Assert.Contains(await recheck.ConversationMistakeReceipts.ToListAsync(), r => r.Status == "reviewed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetractingOneAttemptDoesNotEraseAnotherActualMistake(bool correctionArrivesFirst)
    {
        await using var f = await Fixture.CreateAsync();
        var correction = f.Packet(position: 4, outcome: "correct", retract: true, answer: "6", attemptPosition: 2);
        if (correctionArrivesFirst) await f.Receive(correction);
        await f.Receive(f.Packet()); await f.Receive(f.Packet(position: 2, answer: "6", attemptPosition: 2));
        if (!correctionArrivesFirst) await f.Receive(correction);
        await using var db = f.Open(); var item = await db.MistakeItems.SingleAsync();
        var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
        Assert.True(item.ConversationActive); Assert.Equal("5", snapshot.StudentAttempt!.Quote);
        Assert.Contains(snapshot.Attempts, a => a.RetractsMistake && a.Attempt.Quote == "6");
    }

    [Fact]
    public async Task ACorrectionBeforeFirstCollectionIsNotADeletionTombstone()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Receive(f.Packet(position: 2, outcome: "correct", retract: true));
        await f.Receive(f.Packet(position: 4, answer: "7", attemptPosition: 4));
        await using var db = f.Open(); Assert.True((await db.MistakeItems.SingleAsync()).ConversationActive);
    }

    [Fact]
    public async Task ClearedSourceCannotMaterializeAndClearDoesNotStopNewSources()
    {
        await using var f = await Fixture.CreateAsync(); f.SourceCurrent = false;
        await Assert.ThrowsAsync<ConversationSourceChangedException>(() => f.Receive(f.Packet()));
        await f.Stop();
        await using (var db = f.Open()) { Assert.Empty(await db.MistakeItems.ToListAsync()); Assert.All(await db.ConversationCollections.ToListAsync(), j => Assert.Equal("stopped", j.Status)); }
        f.SourceCurrent = true; await f.Receive(f.Packet()); await f.Stop();
        await using var recheck = f.Open(); Assert.Single(await recheck.MistakeItems.ToListAsync());
        Assert.Contains(await recheck.ConversationCollections.ToListAsync(), j => j.Status == "completed");
    }

    [Fact]
    public async Task AnalysisVersionsIncludeTeacherChangesAndAnalysisDoesNotRemoveEditProtection()
    {
        await using var f = await Fixture.CreateAsync(); await f.Receive(f.Packet());
        Guid id; string hash;
        await using (var db = f.Open())
        {
            var item = await db.MistakeItems.SingleAsync(); id = item.Id;
            var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
            hash = snapshot.EvidenceHash(item.Subject, item.ReviewStatus, item.RootCause, []);
        }
        Assert.True(await f.Analysis(id, hash));
        await using (var db = f.Open())
        {
            var item = await db.MistakeItems.SingleAsync(); item.RootCause = "Teacher correction"; item.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1); await db.SaveChangesAsync();
            var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
            var changed = snapshot.EvidenceHash(item.Subject, item.ReviewStatus, item.RootCause, []); Assert.NotEqual(hash, changed);
            Assert.False(await f.Analysis(id, hash)); hash = changed;
        }
        Assert.True(await f.Analysis(id, hash)); await f.Receive(f.Packet(position: 2, answer: "6", attemptPosition: 2));
        await using var check = f.Open(); var saved = await check.MistakeItems.SingleAsync();
        Assert.Equal("Teacher correction", saved.RootCause);
        Assert.DoesNotContain(JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(saved)!, ConversationExport.Json)!.Attempts, a => a.Attempt.Quote == "6");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuestionAndOriginalImagesAreCopiedVerifiedAndRetainedIndependently(bool correctionArrivesFirst)
    {
        await using var f = await Fixture.CreateAsync();
        if (correctionArrivesFirst) await f.Receive(f.Packet(position: 2, outcome: "correct", retract: true));
        const string original = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";
        var originalBytes = Convert.FromBase64String(original.Split(',')[1]);
        using var decoded = GuardedImageDecoder.LoadQuestion(originalBytes); var crop = decoded.CropQuestion(0, 0, 1000, 1000);
        var asset = new ConversationExportAsset(Guid.NewGuid(), 4, new(0, 0, 1000, 1000), crop.Width, crop.Height, "data:image/jpeg;base64," + Convert.ToBase64String(crop.Data));
        var packet = f.Packet(position: 4, answer: "7", attemptPosition: 4); packet = packet with { Assets = [asset], Sources = [packet.Sources[0], packet.Sources[1] with { Image = original }],
            Events = [packet.Events[0] with { Question = packet.Events[0].Question with { SourcePositions = [0, 4], AssetIds = [asset.Id] } }] };
        await f.Receive(packet); f.SourceCurrent = false; await f.Stop();
        await using var db = f.Open(); var item = await db.MistakeItems.SingleAsync();
        var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!;
        Assert.Equal(2, snapshot.Images.Length); Assert.Equal(2, item.SourceRegions.Count);
        Assert.Equal(crop.Data, await f.Image(snapshot.Images.Single(i => i.AssetId == asset.Id).Path));
        Assert.Equal(originalBytes, await f.Image(snapshot.Images.Single(i => i.AssetId == null).Path));
        Assert.Throws<InvalidDataException>(() => ConversationExport.Image("data:image/jpeg;base64," + original.Split(',')[1]));
        Assert.All(await db.ConversationCollections.ToListAsync(), j => Assert.Equal("[]", j.StagedRegions));
    }

    [Fact]
    public async Task ConcurrentAdmissionsCannotExceedTheStudentPendingLimit()
    {
        await using var f = await Fixture.CreateAsync();
        var accepted = await Task.WhenAll(Enumerable.Range(0, 25).Select(async _ =>
        {
            try { await f.Pending(); return true; } catch (InvalidOperationException) { return false; }
        }));
        Assert.Equal(20, accepted.Count(a => a));
        await f.Stop(); // These sources are new, not cleared histories.
        await using var db = f.Open(); Assert.Equal(20, await db.ConversationCollections.CountAsync(j => j.Status == "pending"));
    }

    [Fact]
    public async Task ChangedExportRetriesTheSameOperationAndKeepsOtherQuestions()
    {
        await using var f = await Fixture.CreateAsync(); var packet = f.Packet(); var question = Guid.NewGuid();
        var id = ConversationExport.Hash($"{ConversationExport.Hash($"{Fixture.Owner(f.Student)}\n{packet.ConversationId:D}\n0")}\n{question:D}");
        var other = packet.Events[0] with { Id = id, Question = packet.Events[0].Question with { Id = question, Statement = "3 + 3 = ?", Evidence = "3 + 3 = 6, not 5" } };
        packet = packet with { Events = [packet.Events[0], other], Sources = [new(0, "2 + 2 = 5; 3 + 3 = 5", null)] };
        f.FingerprintCurrent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Receive(packet));
        await using (var db = f.Open()) Assert.Empty(await db.MistakeItems.ToListAsync());
        f.FingerprintCurrent = true; await f.Retry(packet with { Events = [other] });
        await using var check = f.Open(); var item = await check.MistakeItems.SingleAsync();
        Assert.Equal(question, JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(item)!, ConversationExport.Json)!.QuestionId);
        Assert.Single(await check.ConversationCollections.ToListAsync()); Assert.Equal("completed", (await check.ConversationCollections.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TeacherConfirmationAndRejectionCannotBeReplacedByLaterAutomaticEvidence(int reviewStatus)
    {
        await using var f = await Fixture.CreateAsync(); await f.Receive(f.Packet());
        await using (var db = f.Open())
        {
            var item = await db.MistakeItems.SingleAsync(); item.ReviewStatus = reviewStatus;
            item.ReviewerId = "teacher"; item.RootCause = "Teacher retained explanation"; item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await f.Receive(f.Packet(position: 2, answer: "6", attemptPosition: 2));
        await using var check = f.Open(); var retained = await check.MistakeItems.SingleAsync();
        Assert.Equal(reviewStatus, retained.ReviewStatus); Assert.Equal("Teacher retained explanation", retained.RootCause);
        Assert.Equal("5", JsonSerializer.Deserialize<ConversationQuestionSnapshot>(MistakeSource.ConversationSnapshot(retained)!, ConversationExport.Json)!.StudentAttempt!.Quote);
        Assert.Contains(await check.ConversationMistakeReceipts.ToListAsync(), r => r.Status == "reviewed");
    }

    [Fact]
    public async Task JobScopeRejectsForeignCallerActorStudentAndImmutableAdmissionConflict()
    {
        await using var f = await Fixture.CreateAsync();
        var operation = Guid.NewGuid();
        await f.Admit(operation);
        Assert.NotNull(await f.Status(operation));
        Assert.Null(await f.Status(operation, caller: "other"));
        Assert.Null(await f.Status(operation, actor: "foreign"));
        Assert.Null(await f.Status(operation, student: "foreign"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Admit(operation, caller: "other"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Admit(operation, actor: "foreign"));
        await using var db = f.Open(); Assert.Single(await db.ConversationCollections.ToListAsync());
    }

    [Fact]
    public async Task CallerScopeSeparatesSnapshotsReceiptsAndHttpVisibility()
    {
        await using var f = await Fixture.CreateAsync(); var packet = f.Packet();
        await f.Receive(packet); f.Caller = "other"; await f.Receive(packet);
        await using var db = f.Open();
        Assert.Equal(2, await db.MistakeItems.CountAsync());
        Assert.Equal(2, await db.ConversationMistakeReceipts.CountAsync());
        db.ConversationCallerScope = "platform";
        Assert.Equal("platform", (await db.MistakeItems.SingleAsync()).SourceCaller);
        db.ChangeTracker.Clear(); db.ConversationCallerScope = "unrelated";
        Assert.Empty(await db.MistakeItems.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string admin, database, files;
        public readonly string Student = "learner-" + Guid.NewGuid().ToString("N")[..20];
        public static string Owner(string student) => "test:student:" + student;
        private readonly Guid conversation = Guid.NewGuid(), question = Guid.NewGuid();
        private readonly string actor = Guid.NewGuid().ToString("D");
        private readonly DbContextOptions<MistakeDbContext> options;
        private readonly ConversationCollectionStore store;
        private readonly ConversationAnalysisStore analysis;
        private readonly LocalFileOssService storage;
        public string Caller = "platform";
        public bool Allowed = true;
        public bool SourceCurrent = true;
        public bool FingerprintCurrent = true;
        private string fingerprint = "";
        private Guid operation, lease;
        private Fixture(string admin, string database)
        {
            this.admin = admin; this.database = database;
            files = Path.Combine(Path.GetTempPath(), database); Directory.CreateDirectory(files);
            options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString).Options;
            var students = new Mock<IConversationAuthorization>();
            students.Setup(s => s.AllowedAsync(It.IsAny<string>(), Student, Owner(Student), It.IsAny<CancellationToken>())).ReturnsAsync(() => Allowed);
            var exports = new Mock<IConversationExportReader>();
            exports.Setup(e => e.ReadAsync(It.IsAny<Guid>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ConversationCollectionResponse(SourceCurrent ? "completed" : "stopped", null, FingerprintCurrent ? fingerprint : new string('f', 64)));
            storage = new(files);
            store = new(options, students.Object, storage, exports.Object);
            analysis = new(options, students.Object);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(admin)) throw Xunit.Sdk.SkipException.ForSkip("Set an isolated PostgreSQL test admin connection.");
            var name = "conversation_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
            var f = new Fixture(admin, name);
            try { await using var db = f.Open(); await db.Database.MigrateAsync(); return f; } catch { await f.DisposeAsync(); throw; }
        }
        public MistakeDbContext Open() => new(options);
        public Task Admit(Guid id, string? caller = null, string? actor = null) => store.AdmitAsync(caller ?? Caller, actor ?? this.actor, id, Student, Owner(Student), 3, CancellationToken.None);
        public Task<Emenda.Database.Entities.ConversationCollectionEntity?> Status(Guid id, string? caller = null, string? actor = null, string? student = null) => store.StatusAsync(caller ?? Caller, actor ?? this.actor, student ?? Student, id, CancellationToken.None);
        public Task Stop() => store.StopAsync("platform", actor, Student, CancellationToken.None);
        public Task Pending() => store.AdmitAsync("platform", actor, Guid.NewGuid(), Student, Owner(Student), 3, CancellationToken.None);
        public Task<bool> Analysis(Guid id, string hash) => analysis.SaveAsync("platform", id, new(Student, actor, hash, 1, "high", "Supported model explanation"), CancellationToken.None);
        public async Task<byte[]> Image(string path) { await using var stream = await storage.DownloadAsync(path); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); return buffer.ToArray(); }
        public ConversationExport Packet(int position = 0, string outcome = "incorrect", bool attempt = true, bool retract = false, string answer = "5", int attemptPosition = 0)
        {
            var owner = Owner(Student);
            var id = ConversationExport.Hash($"{ConversationExport.Hash($"{owner}\n{conversation:D}\n{position}")}\n{question:D}");
            return new(1, conversation, [new(id, position, new(question, 2, "2 + 2 = ?", [0], [], attempt ? new(attemptPosition, answer, null) : null, outcome,
                attempt ? $"2 + 2 = 4; the supplied answer was {answer}." : null, "4", [], retract))], [],
                attemptPosition == 0 ? [new(0, "2 + 2 = " + answer, null)] : [new(0, "2 + 2 = 5", null), new(attemptPosition, "My answer is " + answer, null)]);
        }
        public async Task Receive(ConversationExport packet)
        {
            operation = Guid.NewGuid(); lease = Guid.NewGuid();
            await store.AdmitAsync(Caller, actor, operation, Student, Owner(Student), 3, CancellationToken.None);
            await using (var db = Open())
            {
                var job = await db.ConversationCollections.SingleAsync(j => j.Id == operation); job.Status = "running"; job.LeaseId = lease; job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2); await db.SaveChangesAsync();
            }
            await Retry(packet);
        }
        public Task Retry(ConversationExport packet)
        {
            fingerprint = ConversationExport.Hash(JsonSerializer.Serialize(packet, ConversationExport.Json));
            return store.ReceiveAsync(operation, lease, packet, fingerprint, CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {
            using var pool = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString); NpgsqlConnection.ClearPool(pool);
            await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
            if (Directory.Exists(files)) Directory.Delete(files, true);
        }
    }
}
