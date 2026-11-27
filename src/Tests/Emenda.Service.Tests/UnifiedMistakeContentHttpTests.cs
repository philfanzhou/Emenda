using System.Net;
using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.HttpContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Service.Tests;

/// <summary>
/// Every mistake response carries sourceKind and content (since mistake-http-v7), completed by MistakeMaterializer on
/// save (here on the in-memory provider), and validates against the pinned contract. Since v8 the extraction analysis is
/// content alone: there is no analysisSnapshot.
/// </summary>
public sealed class UnifiedMistakeContentHttpTests
{
    private const string Student = "unified-content-student";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EveryResponseCarriesTheSourceKindAndOneContentShape()
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        using var client = factory.Client();
        var job = Guid.NewGuid();
        var crop = $"mistakes/extractions/{job:N}/versions/2/q1.png";
        var original = $"mistakes/conversation/caller/student/{Guid.NewGuid():N}/original.jpg";
        var questionCrop = $"mistakes/conversation/caller/student/{Guid.NewGuid():N}/crop.jpg";
        var asset = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new ConversationQuestionSnapshot
        {
            ConversationId = Guid.NewGuid(), QuestionId = Guid.NewGuid(), Position = 4, MaterializedAt = DateTimeOffset.UtcNow,
            Statement = "Solve x+1=3", StudentAttempt = new(3, "x=1", asset), Evidence = "Subtracted wrongly", ReferenceAnswer = "x=2",
            KnowledgePoints = ["Linear equations"],
            Images = [new(3, null, original, null), new(3, asset, questionCrop, [10, 20, 300, 400])]
        }, Web);
        Guid pending, rejected, conversation, legacy;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            db.ExtractionJobs.Add(new()
            {
                Id = job, Caller = "caller:" + StandaloneWebApplicationFactory.CallerName, StudentId = Student, SourceKey = "worksheet",
                PayloadHash = "hash", Version = 2, Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            pending = Add(db, $"extract:{job:N}", (int)ReviewStatus.PendingReview, crop, i =>
            {
                i.QuestionId = Guid.NewGuid().ToString();
                MistakeSource.SetAnalysis(i, JsonSerializer.Serialize(new { statement = "2+2=?", studentAnswer = "5", referenceAnswer = "4",
                    explanation = "Add the units", evidence = "Wrote 5", sourceVersion = 2 }));
            });
            rejected = Add(db, $"extract:{job:N}", (int)ReviewStatus.Rejected, crop, i =>
                MistakeSource.SetAnalysis(i, JsonSerializer.Serialize(new { statement = "hidden", studentAnswer = "hidden", referenceAnswer = "hidden",
                    explanation = "hidden", evidence = "hidden", sourceVersion = 2 })));
            conversation = Add(db, "", (int)ReviewStatus.PendingReview, original, i =>
            {
                i.SourceRegions.Add(new() { SourceImagePath = questionCrop });
                MistakeSource.SetConversation(i, StandaloneWebApplicationFactory.CallerName, snapshot);
                i.ConversationActive = true;
            });
            legacy = Add(db, "legacy-upload", (int)ReviewStatus.Confirmed, "mistakes/2026/06/08/upload_0.jpg",
                i => i.SourceRegions[0].BoundingBox = new() { X1 = 1, Y1 = 2, X2 = 30, Y2 = 40 });
            await db.SaveChangesAsync();
        }

        using (var item = await GetAsync(client, $"/api/mistakes/{pending}"))
        {
            Assert.Equal("extraction", item.RootElement.GetProperty("sourceKind").GetString());
            Assert.False(item.RootElement.TryGetProperty("analysisSnapshot", out _));
            var content = item.RootElement.GetProperty("content");
            Assert.Equal("2+2=?", content.GetProperty("statement").GetString());
            Assert.Equal("5", content.GetProperty("studentAnswer").GetString());
            Assert.Equal("Add the units", content.GetProperty("explanation").GetString());
            var image = Assert.Single(content.GetProperty("images").EnumerateArray());
            Assert.Equal(crop, image.GetProperty("path").GetString());
            Assert.Equal("question", image.GetProperty("role").GetString());
            Assert.Equal(JsonValueKind.Null, image.GetProperty("box").ValueKind);
        }

        // A rejected extraction mistake does not expose its text (as since v6); its images stay listed.
        using (var item = await GetAsync(client, $"/api/mistakes/{rejected}"))
        {
            var content = item.RootElement.GetProperty("content");
            foreach (var name in new[] { "statement", "studentAnswer", "referenceAnswer", "explanation", "evidence" })
                Assert.Equal(JsonValueKind.Null, content.GetProperty(name).ValueKind);
            Assert.Single(content.GetProperty("images").EnumerateArray());
        }

        using (var item = await GetAsync(client, $"/api/mistakes/{conversation}"))
        {
            Assert.Equal("conversation", item.RootElement.GetProperty("sourceKind").GetString());
            var content = item.RootElement.GetProperty("content");
            Assert.Equal("Solve x+1=3", content.GetProperty("statement").GetString());
            Assert.Equal("x=1", content.GetProperty("studentAnswer").GetString());
            Assert.Equal(JsonValueKind.Null, content.GetProperty("explanation").ValueKind);
            Assert.Equal(new[] { "Linear equations" }, content.GetProperty("knowledgePoints").EnumerateArray().Select(p => p.GetString()));
            var images = content.GetProperty("images").EnumerateArray().ToArray();
            Assert.Equal(new[] { original, questionCrop }, images.Select(i => i.GetProperty("path").GetString()));
            Assert.Equal(new[] { "source", "question" }, images.Select(i => i.GetProperty("role").GetString()));
            Assert.Equal(new[] { 10, 20, 300, 400 }, images[1].GetProperty("box").EnumerateArray().Select(v => v.GetInt32()));
            // The conversation snapshot is still returned, now served from source_detail.
            Assert.Equal("Solve x+1=3", item.RootElement.GetProperty("conversationSource").GetProperty("statement").GetString());
        }

        using (var item = await GetAsync(client, $"/api/mistakes/{legacy}"))
        {
            Assert.Equal("legacy", item.RootElement.GetProperty("sourceKind").GetString());
            var content = item.RootElement.GetProperty("content");
            Assert.Equal(JsonValueKind.Null, content.GetProperty("statement").ValueKind);
            Assert.Equal(0, content.GetProperty("knowledgePoints").GetArrayLength());
            var image = Assert.Single(content.GetProperty("images").EnumerateArray());
            Assert.Equal(new[] { 1, 2, 30, 40 }, image.GetProperty("box").EnumerateArray().Select(v => v.GetInt32()));
        }

        // Lists carry the same shape.
        using var page = await GetAsync(client, $"/api/mistakes?studentId={Student}&subject=0&grade=0&reviewStatus=0&page=1&size=10");
        var kinds = page.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => Guid.Parse(i.GetProperty("id").GetString()!), i => i.GetProperty("sourceKind").GetString());
        Assert.Equal("extraction", kinds[pending]);
        Assert.Equal("conversation", kinds[conversation]);
        Assert.Equal("legacy", kinds[legacy]);
    }

    private static Guid Add(MistakeDbContext db, string source, int status, string path, Action<MistakeItemEntity> configure)
    {
        var item = new MistakeItemEntity
        {
            Id = Guid.NewGuid(), StudentId = Student, Subject = 2, Grade = 4, SourceUploadId = source, ReviewStatus = status,
            SourceRegions = [new() { SourceImagePath = path }], CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        configure(item);
        db.MistakeItems.Add(item);
        return item.Id;
    }

    // Returns the envelope's data after validating the whole response against the pinned contract.
    private static async Task<JsonDocument> GetAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        MistakeHttpContract.ValidateResponse("GET", route.Split('?')[0], 200, envelope.RootElement);
        return JsonDocument.Parse(envelope.RootElement.GetProperty("data").GetRawText());
    }
}
