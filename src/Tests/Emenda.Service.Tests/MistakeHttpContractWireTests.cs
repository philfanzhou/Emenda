using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.HttpContracts;

namespace Emenda.Service.Tests;

/// <summary>Real HTTP binding/domain/serialization; InMemory and OSS doubles are local tests, not the PG/S3 deployment acceptance.</summary>
public sealed class MistakeHttpContractWireTests
{
    [Fact]
    public async Task LifecycleWorkflowSerializesEveryCoreOperationAgainstThePublishedSchemaAsync()
    {
        using var factory = new StandaloneWebApplicationFactory(null) { Oss = new FakeOssService() };
        using var client = factory.Client();
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            db.MistakeItems.Add(new MistakeItemEntity
            {
                Id = id, StudentId = "synthetic-student", Subject = 1, Grade = 7, SourceUploadId = "synthetic-source",
                SourceRegions = [new SourceRegionEntry { SourceImagePath = "mistakes/synthetic.png" }],
                ReviewStatus = (int)ReviewStatus.PendingReview, CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var visited = new HashSet<string>();
        async Task<JsonElement> Send(string method, string path, object? body = null, int expected = 200)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(expected, await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var uri = new Uri(client.BaseAddress!, path);
            MistakeHttpContract.ValidateResponse(method, uri.AbsolutePath, expected, json.RootElement);
            visited.Add(method + " " + path.Split('?')[0]);
            return json.RootElement.Clone();
        }
        await Send("GET", "/api/mistakes/" + id);
        await Send("GET", "/api/mistakes/by-upload/synthetic-source");
        var page = await Send("GET", "/api/mistakes?page=0&size=200&subject=0&grade=0&reviewStatus=0");
        page.GetProperty("data").GetProperty("pageMeta").GetProperty("size").GetInt32().Should().Be(100);
        await Send("PUT", "/api/mistakes/" + id, new { sourceRegions = Array.Empty<object>(), subject = 2 });
        await Send("POST", "/api/mistakes/" + id + "/review", new { reviewStatus = 2, reviewerId = "synthetic-actor" });
        await Send("POST", "/api/mistakes/" + id + "/process-logs", new { studentId = "synthetic-student", action = 2 });
        await Send("GET", "/api/mistakes/" + id + "/process-logs");
        await Send("POST", "/api/mistakes/presigned-url", new { objectPath = "mistakes/synthetic.png", expirySeconds = 0 });
        await Send("DELETE", "/api/mistakes/" + id);
        var missingDelete = await Send("DELETE", "/api/mistakes/" + id);
        missingDelete.GetProperty("success").GetBoolean().Should().BeTrue();
        missingDelete.GetProperty("data").GetProperty("success").GetBoolean().Should().BeFalse();
        visited.Should().HaveCount(9);
    }

    [Theory]
    [InlineData("GET", "/api/mistakes", null, 400)]
    [InlineData("GET", "/api/mistakes?page=bad&size=10&subject=0&grade=0&reviewStatus=0", null, 400)]
    [InlineData("GET", "/api/mistakes/not-a-guid", null, 404)]
    [InlineData("GET", "/api/mistakes/00000000-0000-0000-0000-000000000001", null, 404)]
    [InlineData("POST", "/api/mistakes/00000000-0000-0000-0000-000000000001/review", "{", 400)]
    [InlineData("POST", "/api/mistakes/00000000-0000-0000-0000-000000000001/review", "{\"reviewStatus\":999}", 400)]
    [InlineData("POST", "/api/mistakes/presigned-url", "{\"objectPath\":\"uploads/wrong.png\"}", 400)]
    public async Task ApplicationAndFrameworkErrorsPreserveTheirDifferentContentShapesAsync(string method, string path, string? body, int expected)
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(expected);
        var text = await response.Content.ReadAsStringAsync();
        // Framework constraint 404 does not define a JSON envelope or a pinned operation.
        if (text.Length > 0 && response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
        {
            using var json = JsonDocument.Parse(text);
            MistakeHttpContract.ValidateResponse(method, new Uri(client.BaseAddress!, path).AbsolutePath, expected, json.RootElement);
        }
    }
}
