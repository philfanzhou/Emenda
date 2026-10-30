using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class PendingReviewCountsPostgresTests
{
    [Fact]
    public async Task GroupedPagingFiltersSubjectsWithoutTrackingEveryPendingItemAndHonorsCancellation()
    {
        var admin = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL admin.");
        var name = "pending_counts_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection)) await create.ExecuteNonQueryAsync();
        var target = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(target).Options;
        try
        {
            await using (var seed = new MistakeDbContext(options))
            {
                await DatabaseInitializer.UpgradeToTargetAsync(seed);
                seed.MistakeItems.AddRange(Enumerable.Range(0, 70).Select(i => new MistakeItemEntity
                {
                    StudentId = "fixture-student", Subject = i < 50 ? 1 : 2, Grade = 7,
                    SourceUploadId = i < 50 ? "first-upload" : "second-upload",
                    SourceRegions = [new() { SourceImagePath = "uploads/fixture.png" }],
                    ReviewStatus = (int)ReviewStatus.PendingReview,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-1).AddSeconds(i),
                }));
                await seed.SaveChangesAsync();
            }
            await using var context = new MistakeDbContext(options);
            var repository = new MistakeItemRepository(context);
            var page = await repository.GetPendingReviewUploadsAsync(1, 1, [1, 2]);
            Assert.Equal(2, page.TotalCount);
            Assert.Equal(50, Assert.Single(page.Uploads).PendingCount);
            Assert.Empty(context.ChangeTracker.Entries());
            var filtered = await repository.GetPendingReviewUploadsAsync(1, 1, [2]);
            Assert.Equal(1, filtered.TotalCount);
            Assert.Equal(20, Assert.Single(filtered.Uploads).PendingCount);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetPendingReviewUploadsAsync(1, 1, [1], cancellation.Token));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
