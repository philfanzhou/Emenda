using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;

namespace Emenda.Service.Tests;

/// <summary>PUT /api/mistakes/{id} cannot rewrite the identity a source assigned to an item.</summary>
public sealed class SourceIdentityGuardTests
{
    [Theory]
    [InlineData("studentId")]
    [InlineData("sourceRegions")]
    public async Task ExtractionLinkedItemKeepsStudentAndRegionsAsync(string field)
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        var id = await SeedAsync(factory, conversation: false);
        using var client = factory.Client();

        object body = field == "studentId"
            ? new { studentId = "other-student" }
            : new { sourceRegions = new[] { new { sourceImagePath = "mistakes/extractions/other.png" } } };
        using var response = await client.PutAsJsonAsync($"/api/mistakes/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorKindAsync(response)).Should().Be("source_managed");
        (await ReadAsync(factory, id)).StudentId.Should().Be("owner-student");
    }

    [Fact]
    public async Task ConversationItemKeepsItsStudentAsync()
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        var id = await SeedAsync(factory, conversation: true);
        using var client = factory.Client();

        using var response = await client.PutAsJsonAsync($"/api/mistakes/{id}", new { studentId = "other-student" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorKindAsync(response)).Should().Be("conversation_identity");
        (await ReadAsync(factory, id)).StudentId.Should().Be("owner-student");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonIdentityFieldsStayEditableAsync(bool conversation)
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        var id = await SeedAsync(factory, conversation);
        using var client = factory.Client();

        using var response = await client.PutAsJsonAsync($"/api/mistakes/{id}", new { subject = 3, grade = 8 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = await ReadAsync(factory, id);
        (item.Subject, item.Grade, item.StudentId).Should().Be((3, 8, "owner-student"));
    }

    private static async Task<Guid> SeedAsync(StandaloneWebApplicationFactory factory, bool conversation)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        var id = Guid.NewGuid();
        db.MistakeItems.Add(new MistakeItemEntity
        {
            Id = id, StudentId = "owner-student", Subject = 2, Grade = 7,
            SourceUploadId = conversation ? "conversation:source" : $"extract:{Guid.NewGuid():N}",
            SourceRegions = [new SourceRegionEntry { SourceImagePath = "mistakes/extractions/original.png" }],
            ReviewStatus = (int)ReviewStatus.PendingReview, CreatedAt = DateTimeOffset.UtcNow,
            ConversationSource = conversation ? "{}" : null,
            ConversationCaller = conversation ? StandaloneWebApplicationFactory.CallerName : null
        });
        if (!conversation)
            db.ExtractionQuestions.Add(new ExtractionQuestionEntity { Id = Guid.NewGuid(), JobId = Guid.NewGuid(), ItemId = id, Active = true });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<MistakeItemEntity> ReadAsync(StandaloneWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        return await db.MistakeItems.FindAsync(id) ?? throw new InvalidOperationException("Item disappeared.");
    }

    private static async Task<string?> ErrorKindAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("errorKind").GetString();
    }
}
