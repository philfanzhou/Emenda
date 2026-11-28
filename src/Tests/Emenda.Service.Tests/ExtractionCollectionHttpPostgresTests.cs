using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Emenda.Service.Tests;

public sealed partial class ExtractionQuestionRevisionHttpPostgresTests
{
    [Fact]
    public async Task SixQuestions_RemovalRestorationCollectionAndBothConfirmationRaces()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var host = new ExtractionHostFixture(database.Connection, analyzer: new SixQuestionAnalyzer());
        using var student = Caller(host);
        using var teacher = Caller(host);
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExtractionService>();
        var files = scope.ServiceProvider.GetRequiredService<IOssService>();
        var queued = await service.SubmitAsync(new("http-tests", "student"), new("student", 4, "six-questions",
            [new() { FileName = "page.png", ContentType = "image/png", Content = ExtractionHttpPostgresTests.TestPng() }]));
        await service.RunQueuedAsync(default);
        var root = $"/api/intake/extractions/{queued.Id}";
        async Task<ExtractionJobView> Check(int total, params string[] questionNumbers)
        {
            var summary = await DataAsync<JsonElement>(await student.GetAsync(root + "/summary"));
            var group = Assert.Single(summary.GetProperty("groups").EnumerateArray());
            var counts = group.GetProperty("summary");
            Assert.Equal(total, counts.GetProperty("total").GetInt32());
            Assert.Equal(questionNumbers.Length, counts.GetProperty("mistakeCount").GetInt32());
            Assert.Equal(questionNumbers, group.GetProperty("mistakes").EnumerateArray()
                .Select(q => q.GetProperty("questionNumber").GetString()).ToArray());
            return await DataAsync<ExtractionJobView>(await student.GetAsync(root));
        }
        async Task Hint(int version, string text) =>
            await DataAsync<ExtractionReanalysisView>(await student.PostAsJsonAsync(root + "/reanalyze",
                new ExtractionReanalysisRequest(Guid.NewGuid(), version, text, [2])));
        static ExtractionQuestionView Q(ExtractionJobView j, string number) =>
            j.Questions.Single(q => q.Question.QuestionNumber == number);
        Task<HttpResponseMessage> Withdraw(ExtractionQuestionView q, int version) => student.PostAsJsonAsync(
            $"{root}/questions/{q.Question.Id}/remove-mistake", new ExtractionMistakeRemovalRequest(version, q.ItemId!.Value, 1));
        Task<HttpResponseMessage> Confirm(ExtractionQuestionView q, int version) => teacher.PostAsJsonAsync(
            $"{root}/questions/{q.Question.Id}/review", new ExtractionReviewRequest(version, q.ItemId!.Value, 2, "teacher", null, null));

        var job = await Check(6, "2", "5");
        var q5 = Q(job, "5");
        var neighbor = Q(job, "2");
        var source = Assert.Single(job.Sources);
        (await Withdraw(q5, job.Version)).EnsureSuccessStatusCode();
        Assert.False(await files.ObjectExistsAsync(Assert.Single(q5.Images).Path));
        job = await Check(6, "2");
        await Hint(job.Version, "第5题也是错题");
        job = await Check(6, "2", "5");
        var restored = Q(job, "5");
        Assert.Equal(q5.Question.Id, restored.Question.Id);
        Assert.NotEqual(q5.ItemId, restored.ItemId);
        Assert.True(await files.ObjectExistsAsync(Assert.Single(restored.Images).Path));

        const string collect = "第6题做对了，我也想收录";
        await Hint(job.Version, collect);
        job = await Check(6, "2", "5", "6");
        var q6 = Q(job, "6");
        Assert.Equal(("correct", true), (q6.Question.Outcome, q6.Question.IncludedByRequest));
        await Hint(job.Version, collect);
        Assert.Equal(job.Version, (await Check(6, "2", "5", "6")).Version);

        // Both requests inspect the same pending item. Queue them behind the real PostgreSQL review lock.
        await RaceAsync(database.Connection, job.Id, () => Withdraw(q6, job.Version), () => Confirm(q6, job.Version),
            "version_conflict");
        var cancelled = await Check(6, "2", "5");
        var correct = Q(cancelled, "6");
        Assert.Equal(q6.Question.Id, correct.Question.Id);
        Assert.Equal("correct", correct.Question.Outcome);
        Assert.False(correct.Question.IncludedByRequest);
        Assert.Null(correct.ItemId);
        Assert.Empty(correct.Images);
        Assert.False(await files.ObjectExistsAsync(Assert.Single(q6.Images).Path));
        await Hint(cancelled.Version, collect);
        var recollected = await Check(6, "2", "5", "6");
        var current = Q(recollected, "6");
        Assert.NotEqual(q6.ItemId, current.ItemId);
        (await Withdraw(q6, job.Version)).EnsureSuccessStatusCode(); // replay the lost cancellation receipt
        await RaceAsync(database.Connection, job.Id, () => Confirm(current, recollected.Version),
            () => Withdraw(current, recollected.Version), "review_conflict");
        job = await Check(6, "2", "5", "6");
        using var oldRoute = await student.PutAsJsonAsync($"{root}/questions/{current.Question.Id}",
            new ExtractionQuestionRevisionRequest(Guid.NewGuid(), job.Version, "取消收录",
                current.Question with { IncludedByRequest = false }));
        Assert.Equal(HttpStatusCode.Conflict, oldRoute.StatusCode);
        Assert.True(await files.ObjectExistsAsync(Assert.Single(current.Images).Path));
        Assert.True(await files.ObjectExistsAsync(source.Path));
        Assert.Equal(neighbor.ItemId, Q(job, "2").ItemId);
        await using var db = database.CreateContext();
        var confirmed = await db.MistakeItems.AsNoTracking().SingleAsync(i => i.Id == current.ItemId);
        Assert.Equal((2, "teacher"), (confirmed.ReviewStatus, confirmed.ReviewerId));
        Assert.False(await db.MistakeItems.AnyAsync(i => i.Id == q5.ItemId || i.Id == q6.ItemId));
        await Hint(job.Version, "还漏了第7题");
        await Check(7, "2", "5", "6", "7");
    }

    private static async Task RaceAsync(string connection, Guid job, Func<Task<HttpResponseMessage>> first,
        Func<Task<HttpResponseMessage>> second, string expectedConflict)
    {
        await using var gate = new NpgsqlConnection(connection);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", gate, tx);
        command.Parameters.AddWithValue("key", BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(job.ToString())), 0));
        await command.ExecuteNonQueryAsync();
        async Task WaitForWaiters(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var query = new NpgsqlCommand("""
                SELECT count(*) FROM pg_locks WHERE NOT granted AND locktype='advisory'
                    AND database=(SELECT oid FROM pg_database WHERE datname=current_database())
                """, gate, tx);
            while ((long)(await query.ExecuteScalarAsync(timeout.Token))! < count)
                await Task.Delay(10, timeout.Token);
        }
        var winner = first();
        await WaitForWaiters(1);
        var loser = second();
        await WaitForWaiters(2);
        await tx.CommitAsync();
        using var success = await winner;
        success.EnsureSuccessStatusCode();
        using var conflict = await loser;
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedConflict, body.GetProperty("errorCode").GetString());
    }

    private sealed class SixQuestionAnalyzer : IQuestionAnalysisClient
    {
        public Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
        {
            ExtractionQuestionDraft Question(int n) => new()
            {
                QuestionNumber = n.ToString(), Subject = 2, Grade = 4, Statement = $"Question {n}",
                Outcome = n == 5 ? "unknown" : n is 2 or 7 ? "incorrect" : "correct",
                StudentAnswer = n == 5 ? null : n is 2 or 7 ? "3" : "2", ReferenceAnswer = "2", Evidence = "Written on the page",
                Regions = [new(request.Images[0].SourceId, 0, (n-1)/7d, 1, n/7d)]
            };
            var revision = request.Revision;
            List<ExtractionQuestionDraft> questions = revision switch
            {
                null => Enumerable.Range(1, 6).Select(Question).ToList(),
                _ when revision.Instruction.EndsWith("第5题也是错题") => [revision.Questions.Single(q => q.QuestionNumber == "5")],
                _ when revision.Instruction.EndsWith("第6题做对了，我也想收录") =>
                    [revision.Questions.Single(q => q.QuestionNumber == "6") with { IncludedByRequest = true }],
                _ when revision.Instruction.EndsWith("还漏了第7题") => [Question(7)],
                _ => throw new InvalidOperationException(revision.Instruction)
            };
            return Task.FromResult(new QuestionAnalysisResult("controlled", questions));
        }
    }
}
