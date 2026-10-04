using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.HttpContracts;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>Real HTTP binding/domain/serialization; InMemory and OSS doubles are local tests, not the PG/S3 deployment acceptance.</summary>
public sealed class MistakeHttpContractWireTests
{
    [Fact]
    public async Task LegacyDomainWorkflowSerializesEveryLegacyOperationAgainstThePublishedSchemaAsync()
    {
        using var original = new MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory(false, "test-admin-app-id-ikjeic");
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOssService>();
            services.AddSingleton<IOssService, MistakeApiResourceAuthTests.FakeOssService>();
        }));
        using var client = factory.CreateClient();
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
        var added = await Send("POST", "/api/mistakes", new { studentId = "synthetic-student", subject = 1, grade = 7,
            sourceUploadId = "synthetic-source", sourceRegions = new[] { new { sourceImagePath = "mistakes/synthetic.png", boundingBox = (object?)null } } });
        var id = added.GetProperty("data").GetProperty("id").GetString();
        await Send("GET", "/api/mistakes/" + id);
        await Send("GET", "/api/mistakes/by-upload/synthetic-source");
        var page = await Send("GET", "/api/mistakes?page=0&size=200&subject=0&grade=0&reviewStatus=0");
        page.GetProperty("data").GetProperty("pageMeta").GetProperty("size").GetInt32().Should().Be(100);
        await Send("GET", "/api/mistakes/pending-reviews?page=1&size=10&subjects=invalid,1");
        await Send("PUT", "/api/mistakes/" + id, new { sourceRegions = Array.Empty<object>(), subject = 2 });
        await Send("POST", "/api/mistakes/" + id + "/review", new { reviewStatus = 2, reviewerId = "synthetic-actor" });
        await Send("POST", "/api/mistakes/" + id + "/practices", new { studentId = "synthetic-student", isCorrect = false, imagePaths = Array.Empty<string>() });
        await Send("GET", "/api/mistakes/" + id + "/practices");
        await Send("POST", "/api/mistakes/" + id + "/process-logs", new { studentId = "synthetic-student", action = 6 });
        await Send("GET", "/api/mistakes/" + id + "/process-logs");
        var job = await Send("POST", "/api/mistakes/" + id + "/reanalyze", new { studentId = "synthetic-student", reviewerId = "synthetic-actor" });
        await Send("POST", "/api/mistakes/reanalyze/jobs", new { reviewerId = "synthetic-actor", jobIds = new[] { job.GetProperty("data").GetProperty("jobId").GetString(), "invalid-id" } });
        await Send("GET", "/api/mistakes/reanalyze/active?reviewerId=synthetic-actor");
        await Send("POST", "/api/mistakes/complete-review", new { sourceUploadId = "synthetic-source", reviewerId = "synthetic-actor" });
        await Send("POST", "/api/mistakes/presigned-url", new { objectPath = "mistakes/synthetic.png", expirySeconds = 0 });
        await Send("POST", "/api/mistakes/upload", new { studentId = "synthetic-student", subject = 1, grade = 7, sourceUploadId = "synthetic-upload", imagePaths = new[] { "mistakes/synthetic.png" } });
        await Send("DELETE", "/api/mistakes/" + id);
        var missingDelete = await Send("DELETE", "/api/mistakes/" + id);
        missingDelete.GetProperty("success").GetBoolean().Should().BeTrue();
        missingDelete.GetProperty("data").GetProperty("success").GetBoolean().Should().BeFalse();
        visited.Should().HaveCount(18);
    }

    [Theory]
    [InlineData("GET", "/api/mistakes", null, 400)]
    [InlineData("GET", "/api/mistakes?page=bad&size=10&subject=0&grade=0&reviewStatus=0", null, 400)]
    [InlineData("GET", "/api/mistakes/not-a-guid", null, 404)]
    [InlineData("GET", "/api/mistakes/00000000-0000-0000-0000-000000000001", null, 404)]
    [InlineData("POST", "/api/mistakes/upload", "{", 400)]
    [InlineData("POST", "/api/mistakes/upload", "{}", 400)]
    [InlineData("POST", "/api/mistakes/00000000-0000-0000-0000-000000000001/review", "{\"reviewStatus\":999}", 400)]
    [InlineData("POST", "/api/mistakes/presigned-url", "{\"objectPath\":\"uploads/wrong.png\"}", 400)]
    [InlineData("GET", "/api/mistakes/intakes/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000002", null, 401)]
    [InlineData("POST", "/api/mistakes/intakes/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000002/targets", null, 401)]
    [InlineData("POST", "/api/mistakes/intakes/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000002/handoff", null, 401)]
    public async Task ApplicationAndFrameworkErrorsPreserveTheirDifferentContentShapesAsync(string method, string path, string? body, int expected)
    {
        using var factory = new MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory(false, "test-admin-app-id-ikjeic");
        using var client = factory.CreateClient();
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
