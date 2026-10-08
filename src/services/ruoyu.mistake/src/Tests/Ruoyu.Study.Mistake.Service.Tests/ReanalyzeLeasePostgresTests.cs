using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Service.Services;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class ReanalyzeLeasePostgresTests
{
    [Fact]
    public async Task LostCommitAcknowledgementCanBeReadBackAsOneCompleteTransactionAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL 15 admin connection.");
        var name = "mistake_lease_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            var itemId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            await using (var setup = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(setup);
                setup.MistakeItems.Add(new MistakeItemEntity
                {
                    Id = itemId, StudentId = "student-1", Subject = 1, Grade = 7,
                    SourceUploadId = "upload-1", SourceRegions = [new() { SourceImagePath = "uploads/a.png" }],
                    ReviewStatus = (int)ReviewStatus.PendingReview, RootCause = "initial"
                });
                setup.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                {
                    Id = jobId, ItemId = itemId, ReviewerId = "teacher-1", StudentId = "student-1", Status = 1
                });
                await setup.SaveChangesAsync();
            }
            await using var claimContext = new MistakeDbContext(options);
            var claimed = Assert.Single(await new ReanalyzeJobRepository(claimContext).ClaimAsync(1));
            var lostOptions = new DbContextOptionsBuilder<MistakeDbContext>()
                .UseNpgsql(connectionString).AddInterceptors(new LostCommitAcknowledgement()).Options;
            await using (var lost = new MistakeDbContext(lostOptions))
                await Assert.ThrowsAsync<IOException>(() => new ReanalyzeJobRepository(lost)
                    .CompleteAsync(jobId, claimed.OwnerId!.Value, claimed.FencingVersion, "committed", 2));
            await using var verify = new MistakeDbContext(options);
            Assert.Equal(3, (await verify.ReanalyzeJobs.SingleAsync()).Status);
            Assert.Equal("committed", (await verify.MistakeItems.SingleAsync()).RootCause);
            Assert.Equal(1, await verify.ProcessLogs.CountAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated lost COMMIT acknowledgement.");
    }

    [Fact]
    public async Task TwoIndependentWorkersStopWithoutFailingEachOthersRunningJobsAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL 15 admin connection.");
        var name = "mistake_lease_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            await using (var setup = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(setup);
                var now = DateTimeOffset.UtcNow;
                for (var n = 0; n < 5; n++)
                {
                    var itemId = Guid.NewGuid();
                    setup.MistakeItems.Add(new MistakeItemEntity
                    {
                        Id = itemId, StudentId = "student-1", Subject = 1, Grade = 7,
                        SourceUploadId = "upload-1", SourceRegions = [new() { SourceImagePath = "uploads/a.png" }],
                        ReviewStatus = (int)ReviewStatus.PendingReview, RootCause = "initial"
                    });
                    setup.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                    {
                        Id = Guid.NewGuid(), ItemId = itemId, ReviewerId = "teacher-1",
                        StudentId = "student-1", Status = 1, CreatedAt = now.AddSeconds(n)
                    });
                }
                await setup.SaveChangesAsync();
            }
            var analyzer = new HoldingAnalyzer();
            using var provider1 = WorkerProvider(options, analyzer);
            using var provider2 = WorkerProvider(options, analyzer);
            var worker1 = provider1.GetRequiredService<ReanalyzeJobHostedService>();
            var worker2 = provider2.GetRequiredService<ReanalyzeJobHostedService>();
            await worker1.StartAsync(CancellationToken.None);
            await worker2.StartAsync(CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (analyzer.Started < 2) await Task.Delay(100, timeout.Token);
                await using var verify = new MistakeDbContext(options);
                Assert.Equal(2, await verify.ReanalyzeJobs.CountAsync(j => j.Status == 2));
                Assert.Equal(3, await verify.ReanalyzeJobs.CountAsync(j => j.Status == 1));
            }
            finally
            {
                await worker1.StopAsync(CancellationToken.None);
                await worker2.StopAsync(CancellationToken.None);
            }
            await using (var verify = new MistakeDbContext(options))
            {
                Assert.Equal(2, await verify.ReanalyzeJobs.CountAsync(j => j.Status == 2));
                Assert.Equal(0, await verify.ReanalyzeJobs.CountAsync(j => j.Status == 4));
                await verify.ReanalyzeJobs.Where(j => j.Status == 2).ExecuteUpdateAsync(s =>
                    s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
            }
            using var recoveryProvider1 = WorkerProvider(options, new ThrowingAnalyzer());
            using var recoveryProvider2 = WorkerProvider(options, new ThrowingAnalyzer());
            var recovery1 = recoveryProvider1.GetRequiredService<ReanalyzeJobHostedService>();
            var recovery2 = recoveryProvider2.GetRequiredService<ReanalyzeJobHostedService>();
            await recovery1.StartAsync(CancellationToken.None);
            await recovery2.StartAsync(CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    await using var check = new MistakeDbContext(options);
                    if (await check.ReanalyzeJobs.CountAsync(j => j.Status == 4) == 5) break;
                    await Task.Delay(100, timeout.Token);
                }
            }
            finally
            {
                await recovery1.StopAsync(CancellationToken.None);
                await recovery2.StopAsync(CancellationToken.None);
            }
            await using (var verify = new MistakeDbContext(options))
            {
                Assert.Equal(5, await verify.ReanalyzeJobs.CountAsync(j => j.Status == 4 && j.ErrorMessage == "vl_failed"));
                Assert.Equal(5, await verify.ProcessLogs.CountAsync());
                Assert.Equal(5, await verify.MistakeItems.CountAsync(i => i.RootCause == "initial"));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    private static ServiceProvider WorkerProvider(DbContextOptions<MistakeDbContext> options, IVlAnalyzer analyzer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new MistakeDbContext(options));
        services.AddScoped<IReanalyzeJobRepository, ReanalyzeJobRepository>();
        services.AddScoped<IMistakeItemRepository, MistakeItemRepository>();
        services.AddScoped<IProcessLogRepository, ProcessLogRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IReanalyzeJobService, ReanalyzeJobService>();
        services.AddSingleton(analyzer);
        services.AddSingleton<ReanalyzeJobHostedService>();
        return services.BuildServiceProvider();
    }

    private sealed class HoldingAnalyzer : IVlAnalyzer
    {
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public Task<VlAnalysisResult> AnalyzeAsync(string studentId, List<string> imagePaths,
            CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<VlReanalysisResult> ReAnalyzeAsync(string studentId, List<string> imagePaths,
            string existingRootCause, string? teacherDescription, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _started);
            await Task.Delay(Timeout.Infinite, ct);
            return new VlReanalysisResult();
        }
    }

    private sealed class ThrowingAnalyzer : IVlAnalyzer
    {
        private int _calls;
        public Task<VlAnalysisResult> AnalyzeAsync(string studentId, List<string> imagePaths,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VlReanalysisResult> ReAnalyzeAsync(string studentId, List<string> imagePaths,
            string existingRootCause, string? teacherDescription, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) % 2 == 0) throw new TimeoutException("private timeout detail");
            throw new HttpRequestException("private network detail");
        }
    }

    [Theory]
    [InlineData("root_cause")]
    [InlineData("source_regions")]
    public async Task ExpiredClaimWithChangedInputFailsWithoutOverwritingManualEditAsync(string change)
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL 15 admin connection.");
        var name = "mistake_lease_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            var itemId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            await using (var setup = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(setup);
                setup.MistakeItems.Add(new MistakeItemEntity
                {
                    Id = itemId, StudentId = "student-1", Subject = 1, Grade = 7,
                    SourceUploadId = "upload-1", SourceRegions = [new() { SourceImagePath = "uploads/a.png" }],
                    ReviewStatus = (int)ReviewStatus.PendingReview, RootCause = "initial"
                });
                setup.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                {
                    Id = jobId, ItemId = itemId, ReviewerId = "teacher-1", StudentId = "student-1", Status = 1
                });
                await setup.SaveChangesAsync();
            }
            await using var firstContext = new MistakeDbContext(options);
            var first = Assert.Single(await new ReanalyzeJobRepository(firstContext).ClaimAsync(1));
            await using (var manual = new MistakeDbContext(options))
            {
                if (change == "root_cause")
                    await manual.MistakeItems.Where(i => i.Id == itemId)
                        .ExecuteUpdateAsync(s => s.SetProperty(i => i.RootCause, "manual"));
                else
                    await manual.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE mistake_item SET source_regions='[{{\"sourceImagePath\":\"uploads/b.png\"}}]'::jsonb WHERE id={itemId}");
                await manual.ReanalyzeJobs.Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
            }
            await using var secondContext = new MistakeDbContext(options);
            Assert.Empty(await new ReanalyzeJobRepository(secondContext).ClaimAsync(1));
            Assert.False(await new ReanalyzeJobRepository(firstContext).CompleteAsync(
                jobId, first.OwnerId!.Value, first.FencingVersion, "late-result", 1));
            await using var verify = new MistakeDbContext(options);
            var job = await verify.ReanalyzeJobs.SingleAsync();
            var item = await verify.MistakeItems.SingleAsync();
            var log = await verify.ProcessLogs.SingleAsync();
            Assert.Equal(4, job.Status);
            Assert.Equal("input_changed", job.ErrorMessage);
            Assert.Equal(1, job.Attempt);
            Assert.Equal(first.FencingVersion, job.FencingVersion);
            Assert.Null(job.OwnerId);
            Assert.Null(job.LeaseUntil);
            Assert.Equal(change == "root_cause" ? "manual" : "initial", item.RootCause);
            Assert.Equal(change == "source_regions" ? "uploads/b.png" : "uploads/a.png",
                Assert.Single(item.SourceRegions).SourceImagePath);
            Assert.Equal(itemId, log.MistakeItemId);
            Assert.Contains(jobId.ToString(), log.Details);
            Assert.Contains("FAILED: input_changed", log.Details);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("rejected")]
    [InlineData("image_changed")]
    [InlineData("student_changed")]
    public async Task ChangedOrMissingItemCannotBeOverwrittenAsync(string change)
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL 15 admin connection.");
        var name = "mistake_lease_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            var itemId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            await using (var setup = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(setup);
                setup.MistakeItems.Add(new MistakeItemEntity
                {
                    Id = itemId, StudentId = "student-1", Subject = 1, Grade = 7,
                    SourceUploadId = "upload-1", SourceRegions = [new() { SourceImagePath = "uploads/a.png" }],
                    ReviewStatus = (int)ReviewStatus.PendingReview, RootCause = "initial"
                });
                setup.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                {
                    Id = jobId, ItemId = itemId, ReviewerId = "teacher-1", StudentId = "student-1", Status = 1
                });
                await setup.SaveChangesAsync();
            }
            await using var claimContext = new MistakeDbContext(options);
            var claimed = Assert.Single(await new ReanalyzeJobRepository(claimContext).ClaimAsync(1));
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                var sql = change switch
                {
                    "deleted" => $"DELETE FROM mistake_item WHERE id='{itemId}'",
                    "rejected" => $"UPDATE mistake_item SET review_status=3 WHERE id='{itemId}'",
                    "image_changed" => $"UPDATE mistake_item SET source_regions='[{{\"sourceImagePath\":\"uploads/b.png\"}}]'::jsonb WHERE id='{itemId}'",
                    _ => $"UPDATE mistake_item SET student_id='student-2' WHERE id='{itemId}'"
                };
                await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync();
            }
            await using var terminal = new MistakeDbContext(options);
            Assert.False(await new ReanalyzeJobRepository(terminal).CompleteAsync(jobId, claimed.OwnerId!.Value,
                claimed.FencingVersion, "late-result", 1));
            await using var verify = new MistakeDbContext(options);
            var job = await verify.ReanalyzeJobs.SingleAsync();
            Assert.Equal(4, job.Status);
            Assert.Equal(change == "deleted" ? "item_deleted" : change == "rejected" ? "item_rejected" : "input_changed",
                job.ErrorMessage);
            if (change != "deleted") Assert.Equal("initial", (await verify.MistakeItems.SingleAsync()).RootCause);
            Assert.Equal(change == "deleted" ? 0 : 1, await verify.ProcessLogs.CountAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task TerminalTransactionRollsBackOnSaveFailureAndAttemptLimitFailsJobAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL 15 admin connection.");
        var name = "mistake_lease_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            var itemId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            await using (var setup = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(setup);
                setup.MistakeItems.Add(new MistakeItemEntity
                {
                    Id = itemId, StudentId = "student-1", Subject = 1, Grade = 7,
                    SourceUploadId = "upload-1", SourceRegions = [new() { SourceImagePath = "uploads/a.png" }],
                    ReviewStatus = (int)ReviewStatus.PendingReview, RootCause = "initial"
                });
                setup.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                {
                    Id = jobId, ItemId = itemId, ReviewerId = "teacher-1", StudentId = "student-1", Status = 1
                });
                await setup.SaveChangesAsync();
            }
            await using var claimContext = new MistakeDbContext(options);
            var claimed = Assert.Single(await new ReanalyzeJobRepository(claimContext).ClaimAsync(1));
            var faultOptions = new DbContextOptionsBuilder<MistakeDbContext>()
                .UseNpgsql(connectionString).AddInterceptors(new ThrowOnSave()).Options;
            await using (var fault = new MistakeDbContext(faultOptions))
                await Assert.ThrowsAsync<InvalidOperationException>(() => new ReanalyzeJobRepository(fault)
                    .CompleteAsync(jobId, claimed.OwnerId!.Value, claimed.FencingVersion, "wrong", 2));
            await using (var verify = new MistakeDbContext(options))
            {
                Assert.Equal(2, (await verify.ReanalyzeJobs.SingleAsync()).Status);
                Assert.Equal("initial", (await verify.MistakeItems.SingleAsync()).RootCause);
                Assert.Empty(await verify.ProcessLogs.ToListAsync());
                Assert.True(await new ReanalyzeJobRepository(verify).FailAsync(jobId, claimed.OwnerId!.Value,
                    claimed.FencingVersion, "sensitive exception body"));
            }
            await using (var verify = new MistakeDbContext(options))
            {
                Assert.Equal("worker_failed", (await verify.ReanalyzeJobs.SingleAsync()).ErrorMessage);
                Assert.Equal(1, await verify.ProcessLogs.CountAsync());
                var retryId = Guid.NewGuid();
                verify.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                {
                    Id = retryId, ItemId = itemId, ReviewerId = "teacher-1", StudentId = "student-1", Status = 1
                });
                await verify.SaveChangesAsync();
            }
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await using var context = new MistakeDbContext(options);
                var job = Assert.Single(await new ReanalyzeJobRepository(context).ClaimAsync(1));
                Assert.Equal(attempt, job.Attempt);
                await context.ReanalyzeJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s =>
                    s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
            }
            await using (var context = new MistakeDbContext(options))
            {
                Assert.Empty(await new ReanalyzeJobRepository(context).ClaimAsync(1));
                Assert.Equal("attempt_limit", (await context.ReanalyzeJobs.SingleAsync(j => j.Status == 4 && j.Id != jobId)).ErrorMessage);
                Assert.Equal(2, await context.ProcessLogs.CountAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected before terminal commit.");
    }

    [Fact]
    public async Task TwoContextsClaimInGlobalOrderAndFenceStaleOwnersAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL 15 admin connection.");
        var name = "mistake_lease_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        try
        {
            var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
            await using (var setup = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(setup);
                var items = Enumerable.Range(0, 4).Select(_ => new MistakeItemEntity
                {
                    Id = Guid.NewGuid(), StudentId = "student-1", Subject = 1, Grade = 7,
                    SourceUploadId = "upload-1", SourceRegions = [new() { SourceImagePath = "uploads/a.png" }],
                    ReviewStatus = (int)ReviewStatus.PendingReview, RootCause = "initial",
                    CreatedAt = DateTimeOffset.UtcNow
                }).ToArray();
                setup.MistakeItems.AddRange(items);
                var now = DateTimeOffset.UtcNow;
                for (var i = 0; i < 5; i++)
                    setup.ReanalyzeJobs.Add(new ReanalyzeJobEntity
                    {
                        Id = Guid.NewGuid(), ItemId = items[i == 1 ? 0 : Math.Max(0, i - 1)].Id,
                        ReviewerId = "teacher-1", StudentId = "student-1", PreviousRootCause = "initial",
                        CreatedAt = now.AddSeconds(i), Status = 1
                    });
                await setup.SaveChangesAsync();
            }
            await using var first = new MistakeDbContext(options);
            await using var second = new MistakeDbContext(options);
            var firstClaim = new ReanalyzeJobRepository(first).ClaimAsync(1);
            var secondClaim = new ReanalyzeJobRepository(second).ClaimAsync(1);
            var claimed = (await Task.WhenAll(firstClaim, secondClaim)).SelectMany(j => j).ToArray();
            Assert.Equal(2, claimed.Length);
            Assert.Equal(2, claimed.Select(j => j.ItemId).Distinct().Count());
            Assert.All(claimed, j => { Assert.Equal(1, j.Attempt); Assert.Equal(1, j.FencingVersion); Assert.NotNull(j.OwnerId); });
            await using var verify = new MistakeDbContext(options);
            Assert.Equal(2, await verify.ReanalyzeJobs.CountAsync(j => j.Status == 2));
            Assert.Empty(await new ReanalyzeJobRepository(verify).ClaimAsync(1));
            var firstJob = claimed.Single(j => j.CreatedAt == claimed.Min(x => x.CreatedAt));
            Assert.Equal("initial", firstJob.ExecutionRootCause);
            Assert.True(await new ReanalyzeJobRepository(first).CompleteAsync(firstJob.Id, firstJob.OwnerId!.Value,
                firstJob.FencingVersion, "from-first", 42));
            await using var third = new MistakeDbContext(options);
            var next = Assert.Single(await new ReanalyzeJobRepository(third).ClaimAsync(1));
            Assert.Equal(firstJob.ItemId, next.ItemId);
            Assert.Equal("from-first", next.ExecutionRootCause);
            // A manual edit after claim must fail rather than overwrite the new value.
            await using (var manual = new MistakeDbContext(options))
            {
                await manual.MistakeItems.Where(i => i.Id == next.ItemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.RootCause, "manual"));
            }
            Assert.False(await new ReanalyzeJobRepository(third).CompleteAsync(next.Id, next.OwnerId!.Value,
                next.FencingVersion, "late-result", 3));
            await using var afterManual = new MistakeDbContext(options);
            Assert.Equal("manual", (await afterManual.MistakeItems.SingleAsync(i => i.Id == next.ItemId)).RootCause);
            Assert.Equal(4, (await afterManual.ReanalyzeJobs.SingleAsync(j => j.Id == next.Id)).Status);
            var other = claimed.Single(j => j.Id != firstJob.Id);
            await afterManual.ReanalyzeJobs.Where(j => j.Id == other.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
            await using var recovery = new MistakeDbContext(options);
            var reclaimed = Assert.Single(await new ReanalyzeJobRepository(recovery).ClaimAsync(1));
            Assert.Equal(other.Id, reclaimed.Id);
            Assert.Equal(2, reclaimed.Attempt);
            Assert.Equal(2, reclaimed.FencingVersion);
            Assert.NotEqual(other.OwnerId, reclaimed.OwnerId);
            Assert.False(await new ReanalyzeJobRepository(second).RenewAsync(other.Id, other.OwnerId!.Value, other.FencingVersion));
            Assert.False(await new ReanalyzeJobRepository(second).CompleteAsync(other.Id, other.OwnerId!.Value,
                other.FencingVersion, "stale", 1));
            Assert.False(await new ReanalyzeJobRepository(second).FailAsync(other.Id, other.OwnerId!.Value,
                other.FencingVersion, "vl_failed"));
            Assert.True(await new ReanalyzeJobRepository(recovery).CompleteAsync(reclaimed.Id, reclaimed.OwnerId!.Value,
                reclaimed.FencingVersion, "fresh", 7));
            Assert.False(await new ReanalyzeJobRepository(second).CompleteAsync(other.Id, other.OwnerId!.Value,
                other.FencingVersion, "stale-after-completion", 1));
            await using var final = new MistakeDbContext(options);
            Assert.Equal("fresh", (await final.MistakeItems.SingleAsync(i => i.Id == other.ItemId)).RootCause);
            Assert.Equal(1, await final.ProcessLogs.CountAsync(l => l.MistakeItemId == other.ItemId));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }
}
