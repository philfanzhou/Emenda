using System.Text.Json;
using System.Text.Json.Nodes;
using Emenda.HttpContracts;

namespace Emenda.Service.Tests;

public sealed class ConversationContractTests
{
    [Fact]
    public void V5RetainsEveryV4SchemaAndOperationOutsideThePreciselyDeclaredAdditions()
    {
        var previous = Resource("mistake-http-v4");
        var current = Resource("mistake-http-v5");
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
    public void V6RetainsEveryUnremovedV5OperationAndSchemaWithDeclaredAuthorizationAndWorksheetDetailChanges()
    {
        var previous = Resource("mistake-http-v5");
        var current = JsonNode.Parse(MistakeHttpContract.Definition.GetRawText())!;
        Assert.Equal("mistake-http-v6", MistakeHttpContract.Version);
        var removed = current["x-ruoyu"]!["versionChange"]!["changedLocations"]!.AsArray()
            .Select(value => value!.GetValue<string>()).Where(value => value.StartsWith('/') || value.Contains(" /")).ToHashSet();
        var retained = 0;
        foreach (var path in previous["paths"]!.AsObject())
        foreach (var operation in path.Value!.AsObject())
        {
            var key = operation.Key + " " + path.Key;
            var actual = current["paths"]![path.Key]?[operation.Key];
            if (removed.Contains(key)) { Assert.Null(actual); continue; }
            Assert.NotNull(actual);
            retained++;
            var expected = operation.Value!.DeepClone().AsObject();
            var candidate = actual!.DeepClone().AsObject();
            foreach (var node in new[] { expected, candidate })
            {
                node.Remove("security");
                node.Remove("x-ruoyu");
                var responses = node["responses"]!.AsObject();
                responses.Remove("202");
                foreach (var response in responses)
                    response.Value?.AsObject().Remove("description");
            }
            Assert.True(JsonNode.DeepEquals(expected, candidate), key);
        }
        Assert.Equal(16, retained);
        Assert.Equal(15, removed.Count);
        foreach (var schema in current["components"]!["schemas"]!.AsObject())
        {
            var candidate = schema.Value!.DeepClone();
            if (schema.Key == "MistakeItemDto")
                Assert.True(candidate["properties"]!.AsObject().Remove("analysisSnapshot"));
            Assert.True(JsonNode.DeepEquals(previous["components"]!["schemas"]![schema.Key], candidate), schema.Key);
        }
    }

    private static JsonNode Resource(string version)
    {
        using var stream = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream(version + ".json")!;
        return JsonNode.Parse(stream)!;
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
