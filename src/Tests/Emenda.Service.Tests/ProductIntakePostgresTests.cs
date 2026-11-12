using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Repositories;
using Emenda.Domain.Services;
using Emenda.Host.Services.Intake;
using Emenda.Host.Services.VlImageAnalysis;
using Emenda.Service.Intake;
using Emenda.Service.Services;

namespace Emenda.Service.Tests;

public sealed class ProductIntakePostgresTests
{
    [Fact]
    public async Task StudentIdentifierBoundaryPersists36AndRejects37BeforeStorage()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var run = database.Run();
        var accepted = await run.Service.CreateAsync(Request(student: new string('s', 36)));
        accepted.Success.Should().BeTrue();
        var rejected = await run.Service.CreateAsync(Request(key: "other", student: new string('s', 37)));
        rejected.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        (await run.Db.MistakeItems.SingleAsync()).StudentId.Should().Be(new string('s', 36));
        (await database.Files.ListObjectsAsync()).Select(o => o.ObjectPath)
            .Should().BeEquivalentTo(accepted.Items.SelectMany(i => i.ImagePaths));
    }

    [Fact]
    public async Task ReplayBindsStudentContentAndImageOrderButSeparatesCallers()
    {
        await using var database = await TestDatabase.CreateAsync();
        ProductIntakeResult first;
        using (var run = database.Run()) first = await run.Service.CreateAsync(Request(images: 2));
        first.Success.Should().BeTrue();
        using (var run = database.Run())
        {
            var replay = await run.Service.CreateAsync(Request(images: 2));
            replay.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
            var normalized = Request(images: 2);
            normalized.Images[0].ContentType = "IMAGE/PNG; charset=binary";
            normalized.Images[0].FileName = "renamed.png";
            (await run.Service.CreateAsync(normalized)).Should().BeEquivalentTo(first);
            (await run.Service.CreateAsync(Request(student: "other", images: 2))).ErrorCode
                .Should().Be(ProductIntakeErrorCodes.Conflict);
            var changed = Request(images: 2);
            changed.Images.Reverse();
            (await run.Service.CreateAsync(changed)).ErrorCode.Should().Be(ProductIntakeErrorCodes.Conflict);
            var changedType = Request(images: 2);
            changedType.Images[0].ContentType = "image/jpeg";
            (await run.Service.CreateAsync(changedType)).ErrorCode.Should().Be(ProductIntakeErrorCodes.Conflict);
        }
        using (var run = database.Run())
        {
            var other = await run.Service.CreateAsync(Request(caller: "other-caller", images: 2));
            other.Success.Should().BeTrue();
            other.SourceId.Should().NotBe(first.SourceId);
            other.Items.Select(i => i.Id).Should().NotIntersectWith(first.Items.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task FailedUploadRetainsBindingAndRetriesTheSameObjectsWithoutPartialItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.BeforeUpload = attempt => attempt == 2
            ? Task.FromException(new IOException("injected object failure")) : Task.CompletedTask;
        using (var run = database.Run())
        {
            var failed = await run.Service.CreateAsync(Request(images: 2));
            failed.ErrorCode.Should().Be(ProductIntakeErrorCodes.ProcessingFailed);
            (await run.Db.MistakeItems.CountAsync()).Should().Be(0);
            (await run.Db.ProductIntakeRequests.SingleAsync()).Response.Should().BeNull();
        }
        database.BeforeUpload = _ => Task.CompletedTask;
        using (var run = database.Run())
        {
            var changed = Request(images: 2);
            changed.Images[1].Content = [44];
            (await run.Service.CreateAsync(changed)).ErrorCode.Should().Be(ProductIntakeErrorCodes.Conflict);
            var restored = await run.Service.CreateAsync(Request(images: 2));
            restored.Success.Should().BeTrue();
            (await database.Files.ListObjectsAsync()).Select(o => o.ObjectPath)
                .Should().BeEquivalentTo(restored.Items.SelectMany(i => i.ImagePaths));
            foreach (var path in restored.Items.SelectMany(i => i.ImagePaths))
                (await ReadAsync(database.Files, path)).Should().Equal(path.EndsWith("00.png") ? [1, 2, 3] : [4, 5, 6]);
        }
    }

    [Fact]
    public async Task LostPutResponseIsRecoveredAtTheOriginalObjectKey()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.FailAfterPut = true;
        using (var run = database.Run())
            (await run.Service.CreateAsync(Request())).Success.Should().BeFalse();
        var original = (await database.Files.ListObjectsAsync()).Single().ObjectPath;
        database.FailAfterPut = false;
        using (var run = database.Run())
        {
            var recovered = await run.Service.CreateAsync(Request());
            recovered.Success.Should().BeTrue();
            recovered.Items.Single().ImagePaths.Should().Equal(original);
            (await database.Files.ListObjectsAsync()).Select(o => o.ObjectPath).Should().Equal(original);
            (await ReadAsync(database.Files, original)).Should().Equal([1, 2, 3]);
        }
    }

    [Fact]
    public async Task LaterGroupFailureRollsBackEveryItemAndCreatedLogThenRetryCommitsOneBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_product_group() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.subject=3 THEN RAISE EXCEPTION 'injected_group_failure'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER fail_product_group BEFORE INSERT ON mistake_item FOR EACH ROW EXECUTE FUNCTION fail_product_group();
                """);
        var analyzer = Analyzer([new() { ImageIndices = [0], Subject = 2, Grade = 8 },
                                 new() { ImageIndices = [1], Subject = 3, Grade = 8 }]);
        using (var run = database.Run(analyzer.Object))
            (await run.Service.CreateAsync(Request(images: 2))).Success.Should().BeFalse();
        await using (var check = database.CreateContext())
        {
            (await check.MistakeItems.CountAsync()).Should().Be(0);
            (await check.ProcessLogs.CountAsync()).Should().Be(0);
            (await check.ProductIntakeRequests.SingleAsync()).Response.Should().BeNull();
            await check.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_product_group ON mistake_item; DROP FUNCTION fail_product_group()");
        }
        using (var run = database.Run(analyzer.Object))
        {
            var recovered = await run.Service.CreateAsync(Request(images: 2));
            recovered.Success.Should().BeTrue();
            recovered.Items.Select(i => i.Subject).Should().Equal(2, 3);
            (await run.Db.MistakeItems.Select(i => i.Id).ToListAsync())
                .Should().BeEquivalentTo(recovered.Items.Select(i => i.Id));
            (await run.Db.ProcessLogs.Select(l => l.MistakeItemId).ToListAsync())
                .Should().BeEquivalentTo(recovered.Items.Select(i => i.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrIncompleteAnalysisCannotCreateAnyItems(bool overlap)
    {
        await using var database = await TestDatabase.CreateAsync();
        var groups = overlap
            ? new List<VlImageGroup> { new() { ImageIndices = [0, 1], Subject = 1, Grade = 7 }, new() { ImageIndices = [1], Subject = 2, Grade = 8 } }
            : new List<VlImageGroup> { new() { ImageIndices = [0], Subject = 1, Grade = 7 } };
        using var run = database.Run(Analyzer(groups).Object);
        (await run.Service.CreateAsync(Request(images: 2))).ErrorCode.Should().Be(ProductIntakeErrorCodes.VlFailed);
        (await run.Db.MistakeItems.CountAsync()).Should().Be(0);
        (await run.Db.ProductIntakeRequests.SingleAsync()).Response.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentInstancesReturnInProgressThenReplayTheSingleCommittedBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        database.BeforeUpload = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); };
        using var first = database.Run();
        using var second = database.Run();
        var pending = first.Service.CreateAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            (await second.Service.CreateAsync(Request())).ErrorCode.Should().Be(ProductIntakeErrorCodes.InProgress);
        }
        finally { release.TrySetResult(); }
        var completed = await pending;
        completed.Success.Should().BeTrue();
        using var restarted = database.Run();
        var replay = await restarted.Service.CreateAsync(Request());
        replay.Should().BeEquivalentTo(completed);
        (await restarted.Db.MistakeItems.Select(i => i.Id).ToListAsync())
            .Should().Equal(completed.Items.Single().Id);
    }

    [Fact]
    public async Task CancellationRollsBackItemsButKeepsRequestBindingForRecovery()
    {
        await using var database = await TestDatabase.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new Mock<IVlAnalyzer>();
        analyzer.Setup(a => a.AnalyzeVerifiedAsync(It.IsAny<string>(), It.IsAny<List<string>>(),
                It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, List<string> paths, IReadOnlyList<byte[]> bytes, CancellationToken ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new VlAnalysisResult();
            });
        using var cancel = new CancellationTokenSource();
        using (var run = database.Run(analyzer.Object))
        {
            var pending = run.Service.CreateAsync(Request(), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        using var retry = database.Run();
        var completed = await retry.Service.CreateAsync(Request());
        completed.Success.Should().BeTrue();
        (await retry.Db.MistakeItems.Select(i => i.Id).ToListAsync()).Should().Equal(completed.Items.Single().Id);
    }

    [Fact]
    public async Task Version14UpgradesPreservingRowsAndProtectsNonemptyReceiptsFromDowngrade()
    {
        await using var database = await TestDatabase.CreateAsync(initialize: false);
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(StorageReferenceRemovalSchema.Migration);
        var item = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ({item},'existing',1,7,'old-source','[]'::jsonb,1,now())");
        await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
        (await db.MistakeItems.SingleAsync()).Id.Should().Be(item);
        using (var run = database.Run())
            (await run.Service.CreateAsync(Request())).Success.Should().BeTrue();
        await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(StorageReferenceRemovalSchema.Migration));
        await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
        (await db.MistakeItems.CountAsync()).Should().Be(2);
        (await db.ProductIntakeRequests.SingleAsync()).Response.Should().NotBeNull();
    }

    private static Mock<IVlAnalyzer> Analyzer(List<VlImageGroup> groups)
    {
        var mock = new Mock<IVlAnalyzer>();
        mock.Setup(a => a.AnalyzeVerifiedAsync(It.IsAny<string>(), It.IsAny<List<string>>(),
                It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlAnalysisResult { Groups = groups });
        return mock;
    }

    private static ProductIntakeRequest Request(string caller = "caller", string key = "batch", string student = "student", int images = 1) => new()
    {
        CallerName = caller, SourceKey = key, StudentId = student,
        Images = Enumerable.Range(0, images).Select(index => new ProductIntakeImage
        { FileName = "original.png", ContentType = "image/png", Content = index == 0 ? [1, 2, 3] : [4, 5, 6] }).ToList()
    };

    private static async Task<byte[]> ReadAsync(IOssService oss, string path)
    {
        await using var stream = await oss.DownloadAsync(path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    internal sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "emenda-product-" + Guid.NewGuid().ToString("N"));
        public string Connection { get; }
        public LocalFileOssService Files { get; }
        public Mock<IOssService> Oss { get; } = new(MockBehavior.Strict);
        public Func<int, Task> BeforeUpload { get; set; } = _ => Task.CompletedTask;
        public bool FailAfterPut { get; set; }
        private int _uploadAttempts;

        private TestDatabase(string admin, string name)
        {
            _admin = admin; _name = name;
            Connection = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
            Files = new LocalFileOssService(_root);
            Oss.Setup(o => o.ObjectExistsAsync(It.IsAny<string>())).Returns((string path) => Files.ObjectExistsAsync(path));
            Oss.Setup(o => o.DownloadAsync(It.IsAny<string>())).Returns((string path) => Files.DownloadAsync(path));
            Oss.Setup(o => o.UploadAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OssBucket>(), It.IsAny<string?>()))
                .Returns(async (byte[] bytes, string name, string type, OssBucket bucket, string? folder) =>
                {
                    await BeforeUpload(Interlocked.Increment(ref _uploadAttempts));
                    var path = await Files.UploadAsync(bytes, name, type, bucket, folder);
                    if (FailAfterPut) throw new IOException("injected lost PUT response");
                    return path;
                });
        }

        internal static async Task<TestDatabase> CreateAsync(bool initialize = true)
        {
            var admin = Environment.GetEnvironmentVariable("MISTAKE_MIGRATION_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(admin))
                throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_MIGRATION_TEST_CONNECTION to a dedicated PostgreSQL test admin connection.");
            var name = "emenda_product_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            var database = new TestDatabase(admin, name);
            try
            {
                if (initialize)
                {
                    await using var db = database.CreateContext();
                    await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
                }
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        public MistakeDbContext CreateContext() => new(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(Connection).Options);
        public IntakeRun Run(IVlAnalyzer? analyzer = null) => new(CreateContext(), Oss.Object, analyzer);
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    internal sealed class IntakeRun : IDisposable
    {
        public MistakeDbContext Db { get; }
        public ProductIntakeService Service { get; }
        public IntakeRun(MistakeDbContext db, IOssService oss, IVlAnalyzer? analyzer)
        {
            Db = db;
            var domain = new MistakeDomainService(new MistakeItemRepository(db), new MistakePracticeRepository(db),
                new ProcessLogRepository(db), new UnitOfWork(db), NullLogger<MistakeDomainService>.Instance);
            Service = new ProductIntakeService(oss, domain, db, analyzer ?? Mock.Of<IVlAnalyzer>(),
                Options.Create(new VlAnalyzerOptions { ApiKey = analyzer is null ? "" : "test-provider" }),
                NullLogger<ProductIntakeService>.Instance);
        }
        public void Dispose() => Db.Dispose();
    }
}
