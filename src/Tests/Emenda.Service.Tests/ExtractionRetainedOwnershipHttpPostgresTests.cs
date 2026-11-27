using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Database.Repositories;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Host.Security;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Service.Tests;

public sealed class ExtractionRetainedOwnershipHttpPostgresTests
{
    private const string Student = "retained-owner-student";
    private const string OtherKey = "retained-other-caller-test-key";
    private const string Statement = "Private retained worksheet analysis";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericReadsAndWritesRemainCallerScopedBeforeAndAfterArchival(bool archived)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var baseFactory = new ExtractionHostFixture(database.Connection);
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<ProductCallerAuthenticationOptions>(options =>
                options.Callers.Add(new() { Name = "admin:", ApiKey = OtherKey }))));
        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var foreign = factory.CreateClient();
        foreign.DefaultRequestHeaders.Add("X-Emenda-Api-Key", OtherKey);
        var item = await SeedAsync(database, archived, "caller:http-tests");
        var route = $"/api/mistakes/{item.Id:D}";

        var detail = await DataAsync(await owner.GetAsync(route));
        Assert.Equal(Statement, detail.GetProperty("content").GetProperty("statement").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(route)).StatusCode);
        var listPath = $"/api/mistakes?studentId={Student}&subject=0&grade=0&reviewStatus=0&page=1&size=20";
        Assert.Single((await DataAsync(await owner.GetAsync(listPath))).GetProperty("items").EnumerateArray());
        var foreignList = await DataAsync(await foreign.GetAsync(listPath));
        Assert.Empty(foreignList.GetProperty("items").EnumerateArray());
        Assert.Equal(0, foreignList.GetProperty("pageMeta").GetProperty("totalCount").GetInt32());
        Assert.Single((await DataAsync(await owner.GetAsync($"/api/mistakes/by-upload/{item.SourceUploadId}")))
            .GetProperty("items").EnumerateArray());
        Assert.Empty((await DataAsync(await foreign.GetAsync($"/api/mistakes/by-upload/{item.SourceUploadId}")))
            .GetProperty("items").EnumerateArray());
        foreach (var (childRoute, field) in new[] { ("practices", "practices"), ("process-logs", "logs") })
        {
            Assert.Single((await DataAsync(await owner.GetAsync($"{route}/{childRoute}"))).GetProperty(field).EnumerateArray());
            Assert.Empty((await DataAsync(await foreign.GetAsync($"{route}/{childRoute}"))).GetProperty(field).EnumerateArray());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PutAsJsonAsync(route, new { grade = 5 })).StatusCode);
        var deniedDelete = await DataAsync(await foreign.DeleteAsync(route));
        Assert.False(deniedDelete.GetProperty("success").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await foreign.PostAsJsonAsync($"{route}/practices",
            new { studentId = Student, studentAnswer = "foreign write" })).StatusCode);
        var deniedLog = await DataAsync(await foreign.PostAsJsonAsync($"{route}/process-logs",
            new { studentId = Student, action = 1, details = "foreign write" }));
        Assert.False(deniedLog.GetProperty("success").GetBoolean());

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(route,
            new { studentId = "retargeted-student" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(route,
            new { sourceRegions = new[] { new { sourceImagePath = "foreign/crop.png" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(route, new { grade = 5 })).StatusCode);
        Assert.Equal(Statement, (await DataAsync(await owner.GetAsync(route)))
            .GetProperty("content").GetProperty("statement").GetString());

        await using var db = database.CreateContext();
        Assert.Equal(Student, (await db.MistakeItems.SingleAsync()).StudentId);
        Assert.Single(await db.MistakePractices.ToListAsync());
        Assert.Single(await db.ProcessLogs.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryApiKeysCannotReadAdministratorOwnedExtractionItems(bool archived)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection, callerName: "admin:");
        using var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        var item = await SeedAsync(database, archived, "admin:");
        Assert.Equal(HttpStatusCode.NotFound, (await caller.GetAsync($"/api/mistakes/{item.Id:D}")).StatusCode);
        await using var db = database.CreateContext();
        Assert.NotNull(await new MistakeItemRepository(db).GetByIdAsync(item.Id)); // Trusted background scope.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericRepositoryCannotRetargetExtractionIdentityEvenWithoutQuestionRows(bool archived)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var item = await SeedAsync(database, archived, "caller:http-tests");
        foreach (var field in new[] { "student", "upload", "question", "path", "bounds" })
        {
            await using var db = database.CreateContext();
            db.ConversationCallerScope = "http-tests";
            var repository = new MistakeItemRepository(db);
            var proposed = (await repository.GetByIdAsync(item.Id))!;
            switch (field)
            {
                case "student": proposed.StudentId = "retargeted-student"; break;
                case "upload": proposed.SourceUploadId = "legacy-upload"; break;
                case "question": proposed.QuestionId = Guid.NewGuid().ToString("D"); break;
                case "path": proposed.SourceRegions[0].SourceImagePath = "foreign/crop.png"; break;
                case "bounds": proposed.SourceRegions[0].BoundingBox!.X2++; break;
            }
            var error = await Assert.ThrowsAsync<MistakeConflictException>(() => repository.UpdateAsync(proposed));
            Assert.Equal("source_managed", error.Code);
        }
    }

    [Fact]
    public async Task ScopeChangesCannotBypassFiltersThroughPreviouslyTrackedItemsAndLegacyItemsStayVisible()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var item = await SeedAsync(database, true, "caller:http-tests");
        await using var db = database.CreateContext();
        var repository = new MistakeItemRepository(db);
        var tracked = (await repository.GetByIdAsync(item.Id))!;
        var legacy = new MistakeItemEntity { StudentId = Student, SourceUploadId = "legacy-upload" };
        db.MistakeItems.Add(legacy);
        await db.SaveChangesAsync();
        db.ConversationCallerScope = "unrelated";
        Assert.Null(await repository.GetByIdAsync(item.Id));
        Assert.False(await repository.UpdateAsync(tracked));
        Assert.False(await repository.DeleteAsync(item.Id));
        Assert.False(await repository.ExistsBySourceUploadIdAsync(item.SourceUploadId));
        Assert.NotNull(await repository.GetByIdAsync(legacy.Id));
        Assert.Equal(legacy.Id, Assert.Single(await db.MistakeItems.ToListAsync()).Id);
        db.ConversationCallerScope = "http-tests";
        Assert.NotNull(await repository.GetByIdAsync(item.Id));
    }

    private static async Task<MistakeItemEntity> SeedAsync(PostgresTestDatabase database, bool archived, string owner)
    {
        await using var db = database.CreateContext();
        var jobId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var item = new MistakeItemEntity
        {
            StudentId = Student, Subject = 2, Grade = 3, SourceUploadId = $"extract:{jobId:N}",
            QuestionId = questionId.ToString("D"), ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = [new() { SourceImagePath = $"mistakes/extractions/{jobId:N}/versions/crop.png",
                BoundingBox = new() { X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 } }]
        };
        // Existing rc12 archives have this analysis but no question row or retainedImages metadata.
        MistakeSource.SetAnalysis(item, JsonSerializer.Serialize(new { statement = Statement, studentAnswer = "3",
            referenceAnswer = "2", explanation = "Private explanation", evidence = "Private evidence", sourceVersion = 1 }));
        db.ExtractionObjects.Add(new()
        {
            Path = item.SourceRegions[0].SourceImagePath, JobId = jobId,
            Pending = false, NextAttempt = DateTimeOffset.UtcNow
        });
        // The reference trigger requires a live queue row before the item insert.
        await db.SaveChangesAsync();
        if (archived)
            db.ExtractionDeletions.Add(new()
            {
                JobId = jobId, AdmissionHash = new string('a', 64), SourceBindingHash = new string('b', 64),
                OwnerHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(owner))),
                DeletedAt = DateTimeOffset.UtcNow, RetainedConfirmed = 1
            });
        else
        {
            db.ExtractionJobs.Add(new() { Id = jobId, Caller = owner, StudentId = Student, SourceKey = "worksheet",
                PayloadHash = new string('c', 64), Status = "Succeeded", CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow });
            db.ExtractionQuestions.Add(new() { Id = questionId, JobId = jobId, ItemId = item.Id, Active = true });
        }
        db.MistakeItems.Add(item);
        db.MistakePractices.Add(new() { MistakeItemId = item.Id, StudentId = Student, StudentAnswer = "private practice" });
        db.ProcessLogs.Add(new() { MistakeItemId = item.Id, StudentId = Student, Action = 1, Details = "private history" });
        await db.SaveChangesAsync();
        return item;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("data").Clone();
        }
    }
}
