using System.Text.Json;
using FluentAssertions;
using Emenda.HttpContracts;

namespace Emenda.Service.Tests;

/// <summary>Pinned-contract coverage of the administrative layout operations (IKQA2P):
/// the additive v3 registration of the start request, the durable job view with its
/// per-image status and validated regions, and the number-typed normalized region
/// coordinates the executable vocabulary newly supports.</summary>
public sealed class LayoutHttpContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Parse(object value) => JsonSerializer.SerializeToElement(value, Web);

    private static JsonElement Schema(string name) =>
        MistakeHttpContract.Definition.GetProperty("components").GetProperty("schemas").GetProperty(name);

    [Fact]
    public void StartRequestKeyIsRequiredAndUuid()
    {
        MistakeHttpContract.ValidateSchema(Parse(new { requestKey = Guid.NewGuid().ToString() }),
            Schema("StartLayoutJobRequest"));
        Assert.Throws<JsonException>(
            () => MistakeHttpContract.ValidateSchema(Parse(new { }), Schema("StartLayoutJobRequest")));
    }

    [Fact]
    public void JobViewAcceptsARepresentativeWireValueAndRejectsMissingFields()
    {
        var job = new
        {
            jobId = Guid.NewGuid().ToString(),
            sourceUploadId = Guid.NewGuid().ToString(),
            contentRevision = Guid.NewGuid().ToString(),
            snapshotId = Guid.NewGuid().ToString(),
            studentId = "student-1",
            actor = "actor-1",
            resultRevision = 1,
            status = "Queued",
            config = "{\"contractId\":\"mistake-layout-v1\"}",
            createdAt = "2026-10-25T08:00:00.000Z",
            completedAt = (object?)null,
            errorKind = (object?)null,
            images = new object[]
            {
                new { imagePath = "uploads/layout/a.png", position = 0, expectedLength = 32L,
                    expectedSha256 = new string('a', 64), status = "Pending", attempts = 0,
                    lastError = (object?)null, retryNotBefore = (object?)null }
            },
            results = new object[]
            {
                new { resultId = Guid.NewGuid().ToString(), imagePath = "uploads/layout/a.png",
                    resultRevision = 1, attempt = 1, upstreamRequestId = "request-1",
                    contractId = "mistake-layout-v1", coordinateFrame = "top-left-origin-normalized",
                    width = 100, height = 80, orientation = (object?)6, engine = (object?)"structadoc",
                    model = (object?)"layout-smoke", version = (object?)"0.1.13", sourceRotation = (object?)90,
                    bytesLength = 32L, bytesSha256 = new string('a', 64), createdAt = "2026-10-25T08:00:01.000Z",
                    diagnostics = new[] { "bounded" },
                    regions = new object[] { new { regionId = 0, label = "text", x0 = 0.1, y0 = 0.2, x1 = 0.5, y1 = 0.6 } } }
            }
        };
        MistakeHttpContract.ValidateSchema(Parse(new { jobs = new[] { job } }), Schema("LayoutJobListResult"));
        MistakeHttpContract.ValidateSchema(Parse(job), Schema("LayoutJobView"));

        var missing = new Dictionary<string, object?>(
            (JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(job, Web)) ?? []).Where(p => p.Key != "status"));
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateSchema(Parse(missing), Schema("LayoutJobView")));
        var invalidStatus = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(job, Web)) ?? [];
        invalidStatus["status"] = "Somewhere";
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateSchema(Parse(invalidStatus), Schema("LayoutJobView")));
    }

    [Fact]
    public void RegionCoordinatesValidateAsNumbers()
    {
        var region = new { regionId = 0, label = "text", x0 = 0.05, y0 = 0.02, x1 = 0.95, y1 = 0.98 };
        MistakeHttpContract.ValidateSchema(Parse(region), Schema("LayoutResultRegionView"));
        var textual = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(region, Web)) ?? [];
        textual["x0"] = "0.05";
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateSchema(Parse(textual), Schema("LayoutResultRegionView")));
    }

    [Fact]
    public void LayoutOperationsAreRegisteredAdditivelyOnTheCurrentContract()
    {
        var paths = MistakeHttpContract.Definition.GetProperty("paths");
        var start = paths.GetProperty("/api/mistakes/intakes/{sourceId}/{revision}/layout").GetProperty("post");
        var status = paths.GetProperty("/api/mistakes/intakes/{sourceId}/{revision}/layout").GetProperty("get");
        foreach (var operation in new[] { start, status })
        {
            operation.GetProperty("security").EnumerateArray().Single().EnumerateObject().Single().Name.Should().Be("Bearer");
            operation.GetProperty("x-ruoyu").GetProperty("authorization").GetProperty("compatibility").GetString()
                .Should().Be("always authenticated Admin subject; no anonymous compatibility window");
            operation.GetProperty("x-ruoyu").GetProperty("semantics").EnumerateObject().Should().NotBeEmpty();
        }
        start.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/StartLayoutJobRequest");
        MistakeHttpContract.Definition.GetProperty("x-ruoyu").GetProperty("businessOperationCount").GetInt32().Should().Be(25);
    }
}
