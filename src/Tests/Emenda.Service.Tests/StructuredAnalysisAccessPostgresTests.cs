using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Database.Repositories;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Emenda.Service.Tests;

public sealed class StructuredAnalysisAccessPostgresTests
{
    private const string SharedAudience = "QuantumZhou.microservices";
    private const string AdminAudience = "test-admin-app-id-ikjeic";

    [Theory]
    [InlineData("http-tests")]
    [InlineData("admin:")]
    public async Task ProductCallerCanOnlyReadStartAndConfirmItsOwnExtractionAnalysis(string callerName)
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var own = await SeedAsync(database, "caller:" + callerName);
        var foreign = await SeedAsync(database, "caller:foreign-caller");
        var adminOwned = await SeedAsync(database, "admin:");
        var ordinary = await SeedAsync(database, null);
        using var factory = new ExtractionHostFixture(database.Connection, callerName: callerName);
        using var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.AddCsrfAsync(admin, await factory.SeedSessionAsync(admin));
        using var created = await admin.PostAsJsonAsync("/admin/api/callers", new { name = "foreign-caller", note = "analysis isolation" });
        var credential = await DataAsync<JsonElement>(created);
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Emenda-Api-Key", credential.GetProperty("apiKey").GetString());

        await AssertDeniedAsync(caller, foreign, "teacher", HttpStatusCode.NotFound);
        await AssertDeniedAsync(caller, adminOwned, "teacher", HttpStatusCode.NotFound);
        await AssertDeniedAsync(other, own, "teacher", HttpStatusCode.NotFound);
        await AssertDeniedAsync(other, adminOwned, "teacher", HttpStatusCode.NotFound);
        await AssertAllowedAsync(caller, own, "teacher");
        await AssertAllowedAsync(other, foreign, "teacher");
        // Non-extraction items intentionally keep the existing trusted-BFF behavior.
        await AssertAllowedAsync(caller, ordinary, "teacher");
        await using var db = database.CreateContext();
        Assert.Null((await db.MistakeAnalyses.FindAsync(adminOwned))!.ConfirmedReport);
        Assert.Equal(2, (await db.MistakeAnalyses.FindAsync(adminOwned))!.Revision);
        var feedback = (await db.MistakeAnalyses.FindAsync(own))!.TeacherFeedback;
        using var feedbackDocument = JsonDocument.Parse(feedback);
        Assert.Equal(callerName, feedbackDocument.RootElement.EnumerateArray().Last().GetProperty("caller").GetString());
    }

    [Fact]
    public async Task SuccessfulAnonymousSchemeResultCannotReplaceProductCallerOrAuthorizeAnonymousAnalysis()
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var itemId = await SeedAsync(database, "caller:http-tests");
        using (var original = new ExtractionHostFixture(database.Connection))
        using (var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(AnonymousSchemes)))
        using (var caller = factory.CreateClient())
        {
            caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
            await AssertAllowedAsync(caller, itemId, "teacher");
        }
        using var jwtOriginal = new MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory(false, AdminAudience);
        using var postgres = WithDatabase(jwtOriginal, database.Connection);
        using var anonymousFactory = postgres.WithWebHostBuilder(builder => builder.ConfigureTestServices(AnonymousSchemes));
        using var anonymous = anonymousFactory.CreateClient();
        await AssertDeniedAsync(anonymous, itemId, "teacher", HttpStatusCode.Unauthorized);
    }

    private static void AnonymousSchemes(IServiceCollection services)
    {
        foreach (var scheme in new[] { "Bearer", Emenda.Common.Authentication.EmendaAdminApiAuthorization.SchemeName })
            services.PostConfigure<JwtBearerOptions>(scheme, options => options.Events.OnMessageReceived = context =>
            {
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.Role, "admin"), new Claim("emenda:caller", "http-tests")]));
                context.Success();
                return Task.CompletedTask;
            });
    }

    [Theory]
    [InlineData(false, SharedAudience)]
    [InlineData(false, AdminAudience)]
    [InlineData(true, SharedAudience)]
    [InlineData(true, AdminAudience)]
    public async Task BothJwtSchemesAuthenticateAnalysisAndOnlyActualAdminCanAccessExtraction(bool strict, string audience)
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var callerOwned = await SeedAsync(database, "caller:http-tests");
        var adminOwned = await SeedAsync(database, "admin:");
        var ordinary = await SeedAsync(database, null);
        using var original = new MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory(strict, AdminAudience);
        using var factory = WithDatabase(original, database.Connection);
        using var client = factory.CreateClient();
        await AssertDeniedAsync(client, ordinary, "user-1", HttpStatusCode.Unauthorized);
        await AssertDeniedAsync(client, callerOwned, "user-1", HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = new("Bearer", MistakeApiStrictAuthTests.CreateToken("wrong-audience", "admin"));
        await AssertDeniedAsync(client, ordinary, "user-1", HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = new("Bearer", MistakeApiStrictAuthTests.CreateToken(audience));
        await AssertDeniedAsync(client, ordinary, "user-1", HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Authorization = new("Bearer", MistakeApiStrictAuthTests.CreateToken(audience, "teacher"));
        // Even with a signed caller claim and anonymous admin-role identity added below,
        // a staff JWT is neither a ProductCaller credential nor an authenticated admin.
        await AssertDeniedAsync(client, callerOwned, "user-1", HttpStatusCode.Forbidden);
        await AssertDeniedAsync(client, adminOwned, "user-1", HttpStatusCode.Forbidden);
        await AssertAllowedAsync(client, ordinary, "user-1");
        client.DefaultRequestHeaders.Authorization = new("Bearer", MistakeApiStrictAuthTests.CreateToken(audience, "admin"));
        using (var mismatch = await client.PostAsJsonAsync($"/api/mistakes/{callerOwned}/analysis/confirm",
                   new { expectedRevision = 1, reviewerId = "other-reviewer" }))
            Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        await AssertAllowedAsync(client, callerOwned, "user-1");
        await AssertAllowedAsync(client, adminOwned, "user-1");
    }

    [Theory]
    [InlineData("noncanonical-job")]
    [InlineData("malformed-job")]
    [InlineData("missing-job")]
    [InlineData("question")]
    [InlineData("item")]
    [InlineData("student")]
    [InlineData("subject")]
    public async Task ExtractionAnalysisRejectsInvalidSourceOrQuestionItemBinding(string mismatch)
    {
        await using var database = await ProductIntakePostgresTests.TestDatabase.CreateAsync();
        var itemId = await SeedAsync(database, "caller:http-tests");
        await using (var db = database.CreateContext())
        {
            var item = (await db.MistakeItems.FindAsync(itemId))!;
            switch (mismatch)
            {
                case "noncanonical-job": item.SourceUploadId = "extract:" + Guid.ParseExact(item.SourceUploadId[8..], "N").ToString("D"); break;
                case "malformed-job": item.SourceUploadId = "extract:invalid"; break;
                case "missing-job": item.SourceUploadId = $"extract:{Guid.NewGuid():N}"; break;
                case "question": item.QuestionId = Guid.NewGuid().ToString("D"); break;
                case "item":
                    var unbound = new MistakeItemEntity { StudentId = item.StudentId, Subject = item.Subject, Grade = item.Grade,
                        SourceUploadId = item.SourceUploadId, QuestionId = item.QuestionId, ReviewStatus = 1,
                        SourceRegions = item.SourceRegions, CreatedAt = DateTimeOffset.UtcNow };
                    db.MistakeItems.Add(unbound);
                    itemId = unbound.Id;
                    break;
                case "student": item.StudentId = "other-student"; break;
                case "subject": item.Subject = 3; break;
            }
            await db.SaveChangesAsync();
        }
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        await AssertDeniedAsync(caller, itemId, "teacher",
            mismatch == "missing-job" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict);
    }

    private static WebApplicationFactory<Program> WithDatabase(
        MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory original, string connection) =>
        original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<MistakeDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<MistakeDbContext>>();
            services.AddDbContext<MistakeDbContext>(options => options.UseNpgsql(connection));
            foreach (var scheme in new[] { "Bearer", Emenda.Common.Authentication.EmendaAdminApiAuthorization.SchemeName })
                services.PostConfigure<JwtBearerOptions>(scheme, options =>
                    options.Events.OnTokenValidated = context =>
                    {
                        var identity = (ClaimsIdentity)context.Principal!.Identity!;
                        identity.AddClaim(new Claim("emenda:caller", "http-tests"));
                        context.Principal.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));
                        return Task.CompletedTask;
                    });
        }));

    private static async Task<Guid> SeedAsync(ProductIntakePostgresTests.TestDatabase database, string? owner)
    {
        await using var db = database.CreateContext();
        var jobId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var item = new MistakeItemEntity
        {
            StudentId = "student", Subject = 2, Grade = 7, ReviewStatus = 1,
            SourceUploadId = owner is null ? "ordinary-source" : $"extract:{jobId:N}",
            QuestionId = questionId.ToString("D"), CreatedAt = DateTimeOffset.UtcNow,
            SourceRegions = [new() { SourceImagePath = "mistakes/question.png" }]
        };
        db.MistakeItems.Add(item);
        if (owner is not null)
        {
            db.ExtractionJobs.Add(new ExtractionJobEntity { Id = jobId, Caller = owner, SourceKey = jobId.ToString("N"),
                PayloadHash = new string('a', 64), StudentId = item.StudentId, Grade = item.Grade,
                Status = "Succeeded", Version = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.ExtractionQuestions.Add(new ExtractionQuestionEntity { Id = questionId, JobId = jobId, ItemId = item.Id, Active = true });
            var snapshot = new ExtractionSnapshot([new(new ExtractionQuestionDraft { Id = questionId, Subject = 2,
                Statement = "1 + 1", StudentAnswer = "3", Outcome = "incorrect" }, item.Id, false, [])]);
            db.ExtractionRevisions.Add(new ExtractionRevisionEntity { JobId = jobId, Version = 1, RequestKey = Guid.NewGuid(),
                PayloadHash = new string('b', 64), SnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Actor = "seed", Reason = "analysis access fixture", Model = "test-model", CreatedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync();
        await new MistakeAnalysisRepository(db).StartAsync(item.Id, "seed", "seed", Guid.NewGuid(), 0, null);
        var jobs = new ReanalyzeJobRepository(db);
        var claimed = Assert.Single(await jobs.ClaimAsync(1));
        Assert.True(await jobs.CompleteAnalysisAsync(claimed.Id, claimed.OwnerId!.Value, claimed.FencingVersion,
            new StructuredAnalysisResult { Model = "test-model", PromptVersion = "test-v1",
                Report = new MistakeAnalysisReport { Summary = "Retained private report", KnowledgePoints = [new() { Name = "Addition" }] } }, 1));
        return item.Id;
    }

    private static async Task AssertDeniedAsync(HttpClient client, Guid itemId, string reviewer, HttpStatusCode status)
    {
        using var read = await client.GetAsync($"/api/mistakes/{itemId}/analysis");
        Assert.Equal(status, read.StatusCode);
        Assert.DoesNotContain("Retained private report", await read.Content.ReadAsStringAsync());
        using var confirm = await client.PostAsJsonAsync($"/api/mistakes/{itemId}/analysis/confirm",
            new { expectedRevision = 2, reviewerId = reviewer });
        Assert.Equal(status, confirm.StatusCode);
        using var start = await client.PostAsJsonAsync($"/api/mistakes/{itemId}/reanalyze",
            new { structuredAnalysis = true, requestId = Guid.NewGuid(), expectedRevision = 2, reviewerId = reviewer });
        Assert.Equal(status, start.StatusCode);
    }

    private static async Task AssertAllowedAsync(HttpClient client, Guid itemId, string reviewer)
    {
        var analysis = await DataAsync<MistakeAnalysisModel>(await client.GetAsync($"/api/mistakes/{itemId}/analysis"));
        Assert.Equal("Retained private report", analysis.DraftReport!.Summary);
        var confirmed = await DataAsync<MistakeAnalysisModel>(await client.PostAsJsonAsync($"/api/mistakes/{itemId}/analysis/confirm",
            new { expectedRevision = analysis.Revision, reviewerId = reviewer }));
        Assert.Equal(reviewer, confirmed.ConfirmedBy);
        using var start = await client.PostAsJsonAsync($"/api/mistakes/{itemId}/reanalyze",
            new { structuredAnalysis = true, requestId = Guid.NewGuid(), expectedRevision = confirmed.Revision, reviewerId = reviewer });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
    }

    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetProperty("data").Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }
}
