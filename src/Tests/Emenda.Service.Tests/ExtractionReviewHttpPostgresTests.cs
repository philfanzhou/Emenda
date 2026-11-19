using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Service.Tests;

public sealed class ExtractionReviewHttpPostgresTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReviewOfInspectedVersionCannotConfirmRevisedContentOrRepeatTerminalTransition(int reviewStatus)
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var upload = Upload("versioned-review", [ExtractionHttpPostgresTests.TestPng()]);
        var job = await Data<ExtractionJobView>(await caller.PostAsync("/api/intake/extractions", upload));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().RunQueuedAsync(default);
        var initial = await Data<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"));
        var question = initial.Questions.Single();
        var request = new ExtractionReviewRequest(initial.Version, question.ItemId!.Value, reviewStatus, "teacher", "Inspected version one",
            reviewStatus == 3 ? "Retained rejection reason" : null);
        var route = $"/api/intake/extractions/{job.Id}/questions/{question.Question.Id}/review";
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        var session = await factory.SeedSessionAsync(admin);
        await factory.AddCsrfAsync(admin, session);
        var revision = new ExtractionRevisionRequest(Guid.NewGuid(), initial.Version, "Correct question content",
            [question.Question with { Statement = "Revised question requiring a fresh review" }]);
        var current = await Data<ExtractionJobView>(await admin.PutAsJsonAsync($"/admin/api/extractions/{job.Id}/questions", revision));

        using var stale = await caller.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var json = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()))
            Assert.Equal("version_conflict", json.RootElement.GetProperty("errorCode").GetString());
        using var legacy = await caller.PostAsJsonAsync($"/api/mistakes/{question.ItemId}/review",
            new { reviewStatus, reviewerId = "teacher", rootCause = "Unversioned decision", returnReason = request.ReturnReason });
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        Assert.Contains("extraction_review_endpoint_required", await legacy.Content.ReadAsStringAsync());
        using var invalidReviewer = await caller.PostAsJsonAsync(route,
            request with { ExpectedVersion = current.Version, ReviewerId = new string('r', 37) });
        Assert.Equal(HttpStatusCode.BadRequest, invalidReviewer.StatusCode);
        var pending = await Data<JsonElement>(await caller.GetAsync($"/api/mistakes/{question.ItemId}"));
        Assert.Equal(1, pending.GetProperty("reviewStatus").GetInt32());
        using var confirmed = await caller.PostAsJsonAsync(route, request with { ExpectedVersion = current.Version });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var receipt = await Data<JsonElement>(confirmed);
        Assert.True(receipt.GetProperty("success").GetBoolean());
        Assert.Equal("", receipt.GetProperty("errorMessage").GetString());
        Assert.Empty(receipt.GetProperty("removedImagePaths").EnumerateArray());
        var item = await Data<JsonElement>(await caller.GetAsync($"/api/mistakes/{question.ItemId}"));
        Assert.Equal(reviewStatus, item.GetProperty("reviewStatus").GetInt32());
        Assert.Equal("teacher", item.GetProperty("reviewerId").GetString());
        Assert.Equal("Inspected version one", item.GetProperty("rootCause").GetString());
        if (reviewStatus == 3)
        {
            using var logs = await caller.GetAsync($"/api/mistakes/{question.ItemId}/process-logs");
            Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
            Assert.Contains("Retained rejection reason", await logs.Content.ReadAsStringAsync());
        }
        using var repeated = await caller.PostAsJsonAsync(route,
            request with { ExpectedVersion = current.Version, ReviewStatus = 3, ReturnReason = "Cannot reject a completed review" });
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
    }

    [Theory]
    [InlineData(30, HttpStatusCode.Accepted)]
    [InlineData(31, HttpStatusCode.BadRequest)]
    public async Task FullExamPreservesPageOrderThroughRecognitionReviewAndReplay(int count, HttpStatusCode expected)
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        var pages = Enumerable.Range(0, count).Select(index =>
        {
            byte[] pixels = [.. "P6\n2 1\n255\n"u8.ToArray(), (byte)index, 0, 0, 0, 0, 255];
            using var page = new ImageMagick.MagickImage(pixels);
            return page.ToByteArray(ImageMagick.MagickFormat.Png);
        }).ToArray();
        using var upload = Upload("image-count", pages);
        using var response = await caller.PostAsync("/api/intake/extractions", upload);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Accepted)
        {
            var job = await Data<ExtractionJobView>(response);
            Assert.Equal(pages.Select(page => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(page))),
                job.Sources.Select(s => s.Sha256));
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ExtractionService>().RunQueuedAsync(default);
            var result = await Data<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"));
            Assert.Equal("Succeeded", result.Status);
            var question = Assert.Single(result.Questions);
            Assert.Equal(job.Sources.Select(s => s.Id), question.Question.Regions.Select(r => r.SourceId));
            using var review = await caller.PostAsJsonAsync($"/api/intake/extractions/{job.Id}/questions/{question.Question.Id}/review",
                new ExtractionReviewRequest(result.Version, question.ItemId!.Value, 2, "teacher", "Verified all pages", null));
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
            using var retry = Upload("image-count", pages);
            var replay = await Data<ExtractionJobView>(await caller.PostAsync("/api/intake/extractions", retry));
            Assert.Equal(job.Id, replay.Id);
            Assert.Equal(result.Version, replay.Version);
            Assert.Equal(question.ItemId, Assert.Single(replay.Questions).ItemId);
            Assert.Single(await Data<List<ExtractionJobView>>(await caller.GetAsync("/api/intake/extractions")));
        }
        else
            Assert.Empty(await Data<List<ExtractionJobView>>(await caller.GetAsync("/api/intake/extractions")));
    }

    private static MultipartFormDataContent Upload(string key, byte[][] pages)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-review"), "studentId");
        form.Add(new StringContent("4"), "grade");
        form.Add(new StringContent(key), "sourceKey");
        for (var index = 0; index < pages.Length; index++)
        {
            var image = new ByteArrayContent(pages[index]);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "images", $"page-{index}.png");
        }
        return form;
    }

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
