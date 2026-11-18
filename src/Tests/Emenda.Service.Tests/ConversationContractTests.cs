using System.Text.Json;
using System.Text.Json.Nodes;
using Emenda.HttpContracts;

namespace Emenda.Service.Tests;

public sealed class ConversationContractTests
{
    [Fact]
    public void V5RetainsEveryV4SchemaAndOperationOutsideThePreciselyDeclaredAdditions()
    {
        using var stream = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream("mistake-http-v4.json")!;
        var previous = JsonNode.Parse(stream)!;
        var current = JsonNode.Parse(MistakeHttpContract.Definition.GetRawText())!;
        Assert.Equal("mistake-http-v5", MistakeHttpContract.Version);
        foreach (var path in previous["paths"]!.AsObject())
        foreach (var operation in path.Value!.AsObject())
        {
            var actual = current["paths"]![path.Key]![operation.Key]!.DeepClone();
            if (path.Key == "/api/mistakes" && operation.Key == "get")
                actual["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "reviewStatus")!.AsObject().Remove("description");
            if (path.Key == "/api/mistakes/{id}/review" && operation.Key == "post") actual.AsObject().Remove("description");
            Assert.True(JsonNode.DeepEquals(operation.Value, actual), path.Key + " " + operation.Key);
        }
        foreach (var schema in previous["components"]!["schemas"]!.AsObject())
        {
            var actual = current["components"]!["schemas"]![schema.Key]!.DeepClone();
            if (schema.Key == "MistakeItemDto")
            {
                actual["properties"]!.AsObject().Remove("conversationSource");
                actual["properties"]!.AsObject().Remove("conversationActive");
            }
            Assert.True(JsonNode.DeepEquals(schema.Value, actual), schema.Key);
        }
    }

    [Fact]
    public void AdmissionAndStatusAcceptOpaqueStudentIdentityButRejectMissingTrustedAssertions()
    {
        var request = new { operationId = Guid.NewGuid(), studentId = "opaque-learner", grade = 3, actorId = "account", ownerId = "portal-owned-context" };
        MistakeHttpContract.ValidateRequest("POST", "/api/mistakes/conversation-collections", JsonSerializer.SerializeToElement(request));
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateRequest("POST", "/api/mistakes/conversation-collections", JsonSerializer.SerializeToElement(new { request.operationId, request.studentId, request.grade })));
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateRequest("POST", "/api/mistakes/conversation-collections", JsonSerializer.SerializeToElement(request with { grade = 13 })));
        MistakeHttpContract.ValidateResponse("GET", "/api/mistakes/conversation-collections/22222222-2222-4222-8222-222222222222", 200,
            JsonSerializer.SerializeToElement(new { success = true, data = new { operationId = request.operationId, request.studentId, status = "pending", errorCode = (string?)null, result = (object?)null } }));
    }

    [Fact]
    public void AnalysisWireRequiresSafeModelAndCountsUnicodeRunes()
    {
        var request = new { studentId = "opaque-learner", actorId = "actor", evidenceHash = new string('a', 64), version = 1, model = "configured-model", content = string.Concat(Enumerable.Repeat("😀", 8000)) };
        MistakeHttpContract.ValidateRequest("POST", "/api/mistakes/22222222-2222-4222-8222-222222222222/conversation-analysis", JsonSerializer.SerializeToElement(request));
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateRequest("POST", "/api/mistakes/22222222-2222-4222-8222-222222222222/conversation-analysis", JsonSerializer.SerializeToElement(request with { model = "unsafe/model" })));
        Assert.Throws<JsonException>(() => MistakeHttpContract.ValidateRequest("POST", "/api/mistakes/22222222-2222-4222-8222-222222222222/conversation-analysis", JsonSerializer.SerializeToElement(request with { content = request.content + "x" })));
    }
}
