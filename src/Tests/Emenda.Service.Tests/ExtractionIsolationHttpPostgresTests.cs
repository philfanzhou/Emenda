using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Service.Tests;

public sealed class ExtractionIsolationHttpPostgresTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("admin:")]
    [InlineData("caller:admin:")]
    public async Task CallerNamesCannotImpersonateAdministratorOwnerOrReplayItsSourceKey(string callerName)
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection, callerName: callerName);
        using var caller = factory.CreateClient(new() { AllowAutoRedirect = false });
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.AddCsrfAsync(admin, await factory.SeedSessionAsync(admin));
        var bytes = ExtractionHttpPostgresTests.TestPng();
        var adminJob = await SeedHistoricalAdminJobAsync(factory, "same-source", bytes);

        Assert.Empty(await DataAsync<List<ExtractionJobView>>(await caller.GetAsync("/api/intake/extractions")));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().RunQueuedAsync(default);
        adminJob = await DataAsync<ExtractionJobView>(await admin.GetAsync($"/admin/api/extractions/{adminJob.Id}"));
        Assert.Equal("Succeeded", adminJob.Status);
        foreach (var suffix in new[]
        {
            "", "/versions/1", $"/sources/{adminJob.Sources.Single().Id}",
            $"/questions/{adminJob.Questions.Single().Question.Id}/images/1/0"
        })
        {
            using var denied = await caller.GetAsync($"/api/intake/extractions/{adminJob.Id}{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using var deniedRetry = await caller.PostAsJsonAsync($"/api/intake/extractions/{adminJob.Id}/retry",
            new ExtractionRetryRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, deniedRetry.StatusCode);

        var receipts = await Task.WhenAll(
            SubmitAsync(caller, "/api/intake/extractions", "same-source", bytes),
            SubmitAsync(caller, "/api/intake/extractions", "same-source", bytes));
        var ownJob = receipts[0];
        Assert.NotEqual(adminJob.Id, ownJob.Id);
        Assert.Equal(ownJob.Id, receipts[1].Id);
        Assert.Equal(adminJob.Id, (await SeedHistoricalAdminJobAsync(factory, "same-source", bytes)).Id);
        Assert.Equal(ownJob.Id, Assert.Single(await DataAsync<List<ExtractionJobView>>(
            await caller.GetAsync("/api/intake/extractions"))).Id);
        Assert.Equal(2, (await DataAsync<List<ExtractionJobView>>(await admin.GetAsync("/admin/api/extractions"))).Count);
        await AssertReadableAsync(admin, "/admin/api/extractions", adminJob, bytes);

    }

    [Fact]
    public async Task LegacySigningRejectsOwnForeignAndAdminExtractionObjectsAndAliasesWhileDedicatedReadsWork()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = factory.CreateClient(new() { AllowAutoRedirect = false });
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.AddCsrfAsync(admin, await factory.SeedSessionAsync(admin));
        using var created = await admin.PostAsJsonAsync("/admin/api/callers", new { name = "foreign-caller", note = "isolation test" });
        var foreignCredential = await DataAsync<JsonElement>(created);
        using var foreign = factory.CreateClient(new() { AllowAutoRedirect = false });
        foreign.DefaultRequestHeaders.Add("X-Emenda-Api-Key", foreignCredential.GetProperty("apiKey").GetString());
        var bytes = ExtractionHttpPostgresTests.TestPng();
        var ownJob = await SubmitAsync(caller, "/api/intake/extractions", "own-source", bytes);
        var foreignJob = await SubmitAsync(foreign, "/api/intake/extractions", "foreign-source", bytes);
        var adminJob = await SeedHistoricalAdminJobAsync(factory, "admin-source", bytes);
        using (var scope = factory.Services.CreateScope())
            for (var i = 0; i < 3; i++)
                await scope.ServiceProvider.GetRequiredService<ExtractionService>().RunQueuedAsync(default);

        foreach (var submitted in new[] { ownJob, foreignJob, adminJob })
        {
            var job = await DataAsync<ExtractionJobView>(await admin.GetAsync($"/admin/api/extractions/{submitted.Id}"));
            Assert.Equal("Succeeded", job.Status);
            await AssertReadableAsync(admin, "/admin/api/extractions", job, bytes);
            if (job.Id == ownJob.Id) await AssertReadableAsync(caller, "/api/intake/extractions", job, bytes);
            else
            {
                using var denied = await caller.GetAsync($"/api/intake/extractions/{job.Id}/sources/{job.Sources.Single().Id}");
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                using var deniedCrop = await caller.GetAsync($"/api/intake/extractions/{job.Id}/questions/{job.Questions.Single().Question.Id}/images/1/0");
                Assert.Equal(HttpStatusCode.NotFound, deniedCrop.StatusCode);
            }
            if (job.Id == foreignJob.Id) await AssertReadableAsync(foreign, "/api/intake/extractions", job, bytes);
            foreach (var objectPath in new[] { job.Sources.Single().Path, job.Questions.Single().Images.Single().Path })
            {
                var paths = new[]
                {
                    objectPath,
                    ThumbnailHelper.GetThumbnailPath(objectPath, "thumbnail"),
                    objectPath.Replace("mistakes/extractions/", "mistakes/./extractions/", StringComparison.Ordinal),
                    objectPath.Replace("mistakes/extractions/", "mistakes/legacy/../extractions/", StringComparison.Ordinal),
                    objectPath.Replace("mistakes/extractions/", "mistakes//extractions/", StringComparison.Ordinal),
                    objectPath.Replace('/', '\\'),
                    objectPath.Replace("mistakes/extractions/", "mistakes/Extractions/", StringComparison.Ordinal)
                };
                foreach (var path in paths)
                foreach (var size in new[] { "full", "thumbnail" })
                {
                    using var denied = await caller.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = path, size });
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                    Assert.Contains("extraction_read_endpoint_required", await denied.Content.ReadAsStringAsync());
                }
            }
        }

        var oss = factory.Services.GetRequiredService<IOssService>();
        var legacyPath = await oss.UploadAsync(bytes, "page.png", "image/png", OssBucket.Mistakes, "extractions-legacy");
        foreach (var size in new[] { "full", "thumbnail" })
        {
            using var allowed = await caller.PostAsJsonAsync("/api/mistakes/presigned-url", new { objectPath = legacyPath, size });
            await DataAsync<JsonElement>(allowed);
        }
    }

    private static async Task<ExtractionJobView> SeedHistoricalAdminJobAsync(ExtractionHostFixture factory, string sourceKey, byte[] bytes)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExtractionService>();
        var image = new ProductIntakeImage { FileName = "page.png", ContentType = "image/png", Content = bytes };
        return await service.SubmitAsync(new ExtractionAccess("admin", "admin", Administrator: true),
            new ExtractionUpload("student-isolation", 4, sourceKey, [image]), default);
    }

    private static async Task<ExtractionJobView> SubmitAsync(HttpClient client, string endpoint, string sourceKey, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-isolation"), "studentId");
        form.Add(new StringContent("4"), "grade");
        form.Add(new StringContent(sourceKey), "sourceKey");
        var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "images", "page.png");
        using var response = await client.PostAsync(endpoint, form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await DataAsync<ExtractionJobView>(response);
    }

    private static async Task AssertReadableAsync(HttpClient client, string endpoint, ExtractionJobView job, byte[] originalBytes)
    {
        using var original = await client.GetAsync($"{endpoint}/{job.Id}/sources/{job.Sources.Single().Id}");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal(originalBytes, await original.Content.ReadAsByteArrayAsync());
        var question = job.Questions.Single();
        using var crop = await client.GetAsync($"{endpoint}/{job.Id}/questions/{question.Question.Id}/images/{job.Version}/0");
        Assert.Equal(HttpStatusCode.OK, crop.StatusCode);
        Assert.Equal("image/png", crop.Content.Headers.ContentType?.MediaType);
        using var decoded = new ImageMagick.MagickImage(await crop.Content.ReadAsByteArrayAsync());
        Assert.Equal((uint)question.Images.Single().Width, decoded.Width);
        Assert.Equal((uint)question.Images.Single().Height, decoded.Height);
    }

    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("data").Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
