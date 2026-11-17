using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Database.Repositories;
using Emenda.Domain.Models;
using Emenda.Host.Services;
using Emenda.Service.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Emenda.Service.Tests;

public sealed class MistakeAnalysisPostgresTests
{
    [Fact]
    public async Task HttpFeedbackWorkerConfirmationAndCleanupPreserveCurrentReports()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var itemId = await SeedAsync(database);
        var analyzer = new Mock<IVlAnalyzer>();
        analyzer.Setup(a => a.AnalyzeMistakeAsync(It.IsAny<StructuredAnalysisInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StructuredAnalysisInput input, CancellationToken _) =>
            {
                if (input.TeacherFeedback.LastOrDefault() == "fail") throw new InvalidOperationException("provider unavailable");
                return Result(string.Join(" / ", input.TeacherFeedback.DefaultIfEmpty("initial analysis")));
            });
        using var baseFactory = new StandaloneIntakePostgresTests.StandaloneIntakeWebApplicationFactory(database.Connection);
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IVlAnalyzer>();
            services.AddSingleton(analyzer.Object);
        }));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/mistakes/{itemId}/analysis")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", "intake-test-api-key-0123456789");
        Assert.Equal(0, (await ReadAsync(client, itemId)).Revision);
        using var worker = new ReanalyzeJobHostedService(factory.Services, NullLogger<ReanalyzeJobHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var requestId = Guid.NewGuid();
            var first = await StartAsync(client, itemId, requestId, 0, "这里是加号");
            Assert.Equal(first, await StartAsync(client, itemId, requestId, 0, "这里是加号"));
            var report = await WaitAsync(client, itemId, first);
            Assert.Single(report.TeacherFeedback);
            Assert.Contains("加号", report.DraftReport!.Summary);
            Assert.Null(report.ConfirmedReport);
            await using (var db = database.CreateContext()) Assert.Equal("legacy summary", (await db.MistakeItems.FindAsync(itemId))!.RootCause);
            var confirmation = await client.PostAsJsonAsync($"/api/mistakes/{itemId}/analysis/confirm", new { expectedRevision = report.Revision, reviewerId = "teacher" });
            confirmation.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/mistakes/{itemId}/analysis/confirm", new { expectedRevision = report.Revision, reviewerId = "teacher" })).StatusCode);
            var second = await StartAsync(client, itemId, Guid.NewGuid(), report.Revision, "学生知道分配律");
            report = await WaitAsync(client, itemId, second);
            Assert.Contains("加号", report.DraftReport!.Summary);
            Assert.Contains("分配律", report.DraftReport.Summary);
            Assert.DoesNotContain("分配律", report.ConfirmedReport!.Summary);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/mistakes/{itemId}/analysis/confirm", new { expectedRevision = 0, reviewerId = "teacher" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/mistakes/{itemId}/reanalyze", new { studentId = "student", reviewerId = "teacher" })).StatusCode);
            var failed = await StartAsync(client, itemId, Guid.NewGuid(), report.Revision, "fail");
            await WaitJobAsync(client, failed, 4);
            var retained = await ReadAsync(client, itemId);
            Assert.Equal(report.DraftReport.Summary, retained.DraftReport!.Summary);
            Assert.Equal(report.ConfirmedReport.Summary, retained.ConfirmedReport!.Summary);
            await using (var db = database.CreateContext())
                await new ReanalyzeJobRepository(db).DeleteExpiredAsync(DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(first, await StartAsync(client, itemId, requestId, 0, "这里是加号"));
            Assert.Equal(3, (await ReadAsync(client, itemId)).TeacherFeedback.Count);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UpgradeAndConcurrentFeedbackRejectStaleOrExpiredCompletions()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync(initialize: false);
        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(ProductIntakeSchema.Migration);
            await DatabaseInitializer.InitializeAsync(db, NullLoggerFactory.Instance);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var itemId = await SeedAsync(database);
        var key = Guid.NewGuid();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var repo = new MistakeAnalysisRepository(first);
        var results = await Task.WhenAll(repo.StartAsync(itemId, "teacher", "caller", key, 0, "one"),
            new MistakeAnalysisRepository(second).StartAsync(itemId, "teacher", "caller", key, 0, "one"));
        Assert.Equal(results[0], results[1]);
        await Assert.ThrowsAsync<AnalysisConflictException>(() => repo.StartAsync(itemId, "teacher", "caller", key, 0, "different"));
        var jobs = new ReanalyzeJobRepository(first);
        var claimed = Assert.Single(await jobs.ClaimAsync(1));
        await using (var update = database.CreateContext())
        {
            var item = (await update.MistakeItems.FindAsync(itemId))!;
            item.Grade = 9;
            await update.SaveChangesAsync();
        }
        Assert.False(await jobs.CompleteAnalysisAsync(claimed.Id, claimed.OwnerId!.Value, claimed.FencingVersion, Result("stale"), 1));
        Assert.Null((await repo.GetAsync(itemId))!.DraftReport);
        var next = await repo.StartAsync(itemId, "teacher", "caller", Guid.NewGuid(), 1, "two");
        claimed = Assert.Single(await jobs.ClaimAsync(1));
        await first.Database.ExecuteSqlInterpolatedAsync($"UPDATE reanalyze_job SET lease_until=clock_timestamp()-interval '1 second' WHERE id={next}");
        Assert.False(await jobs.CompleteAnalysisAsync(next, claimed.OwnerId!.Value, claimed.FencingVersion, Result("expired"), 1));
        var reclaimed = Assert.Single(await jobs.ClaimAsync(1));
        Assert.Equal(new[] { "one", "two" }, reclaimed.AnalysisInput!.TeacherFeedback);
        Assert.True(await jobs.CompleteAnalysisAsync(next, reclaimed.OwnerId!.Value, reclaimed.FencingVersion, Result("fresh"), 1));
        var report = (await repo.GetAsync(itemId))!;
        await repo.ConfirmAsync(itemId, report.Revision, "teacher");
        await using (var update = database.CreateContext())
        {
            var item = (await update.MistakeItems.FindAsync(itemId))!;
            item.SourceRegions = [new() { SourceImagePath = "mistakes/changed.png" }];
            await update.SaveChangesAsync();
        }
        Assert.True((await repo.GetAsync(itemId))!.InputChanged);
        await Assert.ThrowsAsync<AnalysisConflictException>(() => repo.ConfirmAsync(itemId, report.Revision, "teacher"));
    }

    private static StructuredAnalysisResult Result(string summary) => new()
    {
        Model = "test-model", PromptVersion = "test-v1",
        Report = new MistakeAnalysisReport { Summary = summary, KnowledgePoints = [new() { Name = "分配律" }] }
    };

    private static async Task<Guid> SeedAsync(ProductIntakePostgresTests.TestDatabase database)
    {
        await using var db = database.CreateContext();
        var item = new MistakeItemEntity
        {
            StudentId = "student", Subject = 2, Grade = 7, ReviewStatus = 1, RootCause = "legacy summary",
            SourceUploadId = "source", SourceRegions = [new() { SourceImagePath = "mistakes/question.png" }]
        };
        db.MistakeItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<Guid> StartAsync(HttpClient client, Guid itemId, Guid requestId, long revision, string feedback)
    {
        var response = await client.PostAsJsonAsync($"/api/mistakes/{itemId}/reanalyze", new
        { structuredAnalysis = true, requestId, expectedRevision = revision, teacherDescription = feedback, reviewerId = "teacher" });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("jobId").GetGuid();
    }

    private static async Task<MistakeAnalysisModel> ReadAsync(HttpClient client, Guid itemId)
    {
        var response = await client.GetAsync($"/api/mistakes/{itemId}/analysis");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Emenda.HttpContracts.MistakeHttpContract.ValidateResponse("GET", $"/api/mistakes/{itemId}/analysis", 200, json.RootElement);
        return json.RootElement.GetProperty("data").Deserialize<MistakeAnalysisModel>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<MistakeAnalysisModel> WaitAsync(HttpClient client, Guid itemId, Guid jobId)
    {
        await WaitJobAsync(client, jobId, 3);
        return await ReadAsync(client, itemId);
    }

    private static async Task WaitJobAsync(HttpClient client, Guid jobId, int expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var response = await client.PostAsJsonAsync("/api/mistakes/reanalyze/jobs", new { reviewerId = "teacher", jobIds = new[] { jobId } }, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var status = json.RootElement.GetProperty("data").GetProperty("jobs")[0].GetProperty("status").GetInt32();
            if (status >= 3) { Assert.Equal(expectedStatus, status); return; }
            await Task.Delay(100, timeout.Token);
        }
    }
}
