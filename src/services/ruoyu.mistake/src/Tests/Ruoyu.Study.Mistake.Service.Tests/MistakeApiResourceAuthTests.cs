using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Ruoyu.Study.Common.Oss;
using Xunit;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEPZ acceptance tests for the per-resource authorization matrix. They
/// mirror MistakeApiStrictAuthTests: real JwtBearer pipeline with a test
/// signing key through the real Mistake Program.cs, EF Core InMemory context,
/// and a fake IUploadSourceGateway/IOssService. Every matrix cell
/// (18 endpoints x Learner/Staff/Admin) has at least one assertion here.
/// </summary>
public sealed class MistakeApiResourceAuthTests
{
    private const string Issuer = "http://localhost:5002";
    private const string SharedAudience = "QuantumZhou.microservices";
    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("ikjepz-mistake-resource-signing-key"));

    private const string LearnerSub = "learner-1";
    private const string EmptyMappingSub = "learner-2";
    private const string UnresolvableSub = "learner-3";
    private const string StaffSub = "staff-1";
    private const string AdminSub = "admin-1";
    private const string StudentA = "student-A";
    private const string StudentB = "student-B";
    private const string UploadA = "upload-A";
    private const string UploadB = "upload-B";
    private const string UploadMixed = "upload-mixed";
    private const string ItemAPath = "mistakes/a-1.png";
    private const string ItemBPath = "mistakes/b-1.png";
    private const string PracticePath = "mistakes/practice-1.png";

    private static Guid ItemAId { get; } = Guid.Parse("10000000-0000-0000-0000-00000000000a");
    private static Guid ItemBId { get; } = Guid.Parse("10000000-0000-0000-0000-00000000000b");
    private static Guid ItemA2Id { get; } = Guid.Parse("10000000-0000-0000-0000-0000000000aa");
    private static Guid ItemMixedBId { get; } = Guid.Parse("10000000-0000-0000-0000-0000000000bb");
    private static Guid ItemB2Id { get; } = Guid.Parse("10000000-0000-0000-0000-0000000000b2");
    private static Guid JobBId { get; } = Guid.Parse("20000000-0000-0000-0000-00000000000b");

    private static string Token(string sub, string? role = null) => CreateToken(SharedAudience, sub, role);

    // ===== 1. POST /api/mistakes/upload =====

    [Fact]
    public async Task UploadMatrix_LearnerAndStaffForbidden_AdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        var body = Seed.uploadBodyJson(StudentA);

        (await client.SendAsync(Post("/api/mistakes/upload", body, Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/api/mistakes/upload", body, Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await client.SendAsync(Post("/api/mistakes/upload", body, Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    // ===== 2. GET /api/mistakes/{id} =====

    [Fact]
    public async Task GetItemMatrix_LearnerOwnAllowedCrossStudentForbidden_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Get($"/api/mistakes/{ItemAId}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 3. GET /api/mistakes/by-upload/{sourceUploadId} =====

    [Fact]
    public async Task ByUploadMatrix_LearnerAllowedOnlyWhenAllItemsOwned_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Get($"/api/mistakes/by-upload/{UploadA}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/by-upload/{UploadMixed}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Get($"/api/mistakes/by-upload/{UploadB}", Token(StaffSub, "assistant"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/by-upload/{UploadB}", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 4. GET /api/mistakes =====

    [Fact]
    public async Task ListMatrix_LearnerScopedToOwnedStudents_StaffAdminUnchanged()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Learner with own studentId parameter: allowed, only owned rows.
        using var own = await client.SendAsync(
            Get($"/api/mistakes?studentId={StudentA}&subject=0&grade=0&reviewStatus=0&page=1&size=50", Token(LearnerSub)));
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownBody = await own.Content.ReadAsStringAsync();
        ownBody.Should().Contain(StudentA).And.NotContain(StudentB);

        // Learner with another student's id: forbidden.
        (await client.SendAsync(
                Get($"/api/mistakes?studentId={StudentB}&subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Learner without studentId: allowed-student scope is injected and the
        // page meta (TotalCount) is computed over the scoped query only.
        using var scoped = await client.SendAsync(
            Get("/api/mistakes?subject=0&grade=0&reviewStatus=0&page=1&size=1", Token(LearnerSub)));
        scoped.StatusCode.Should().Be(HttpStatusCode.OK);
        var scopedBody = await scoped.Content.ReadAsStringAsync();
        scopedBody.Should().Contain(StudentA).And.NotContain(StudentB);
        scopedBody.Should().Contain("\"totalCount\":3").And.Contain("\"totalPages\":3");

        // Learner whose account maps to no student: no scope at all.
        (await client.SendAsync(
                Get("/api/mistakes?subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(EmptyMappingSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(
                Get($"/api/mistakes?studentId={StudentA}&subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(EmptyMappingSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Staff/Admin keep the baseline behavior (single-student filter).
        using var staff = await client.SendAsync(
            Get($"/api/mistakes?studentId={StudentB}&subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.Content.ReadAsStringAsync()).Should().Contain(StudentB);
        using var admin = await client.SendAsync(
            Get($"/api/mistakes?studentId={StudentB}&subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync()).Should().Contain(StudentB);
    }

    // ===== 5. GET /api/mistakes/pending-reviews =====

    [Fact]
    public async Task PendingReviewsMatrix_LearnerForbidden_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Get("/api/mistakes/pending-reviews?page=1&size=10", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Get("/api/mistakes/pending-reviews?page=1&size=10", Token(LearnerSub, "student"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Get("/api/mistakes/pending-reviews?page=1&size=10", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get("/api/mistakes/pending-reviews?page=1&size=10", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 6. POST /api/mistakes/{id}/review =====

    [Fact]
    public async Task ReviewMatrix_LearnerForbidden_StaffAdminBoundToSub()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string Body(string reviewer) =>
            $$"""{"id":"{{ItemBId}}","reviewStatus":2,"reviewerId":"{{reviewer}}"}""";

        (await client.SendAsync(Post($"/api/mistakes/{ItemBId}/review", Body(LearnerSub), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var staff = await client.SendAsync(
            Post($"/api/mistakes/{ItemBId}/review", Body(StaffSub), Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        (await client.SendAsync(Post($"/api/mistakes/{ItemBId}/review", Body("someone-else"), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await client.SendAsync(
            Post($"/api/mistakes/{ItemB2Id}/review", Body(AdminSub), Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        (await client.SendAsync(Post($"/api/mistakes/{ItemBId}/review", Body(StaffSub), Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ===== 7. POST /api/mistakes/{id}/reanalyze =====

    [Fact]
    public async Task ReanalyzeMatrix_LearnerForbidden_StaffAdminBoundToSubAndItemOwner()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string Body(string reviewer, string student) =>
            $$"""{"id":"{{ItemBId}}","reviewerId":"{{reviewer}}","studentId":"{{student}}"}""";

        (await client.SendAsync(Post($"/api/mistakes/{ItemBId}/reanalyze", Body(LearnerSub, StudentB), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // reviewerId != sub -> 403.
        (await client.SendAsync(
                Post($"/api/mistakes/{ItemBId}/reanalyze", Body("someone-else", StudentB), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // request studentId != item owner -> 403 and no job persisted for
        // the target item (the seeded job belongs to a different item).
        (await client.SendAsync(
                Post($"/api/mistakes/{ItemBId}/reanalyze", Body(StaffSub, StudentA), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetActiveJobItemIdsAsync(client, Token(StaffSub, "teacher")))
            .Should().NotContain(ItemBId, "the rejected reanalyze must not enqueue a job");

        using var staff = await client.SendAsync(
            Post($"/api/mistakes/{ItemBId}/reanalyze", Body(StaffSub, StudentB), Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        using var admin = await client.SendAsync(
            Post($"/api/mistakes/{ItemBId}/reanalyze", Body(AdminSub, StudentB), Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.SendAsync(
                Post($"/api/mistakes/{ItemBId}/reanalyze", Body(AdminSub, StudentA), Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ===== 8. POST /api/mistakes/reanalyze/jobs =====

    [Fact]
    public async Task ReanalyzeJobsMatrix_LearnerForbidden_StaffAdminBoundToSub()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string Body(string reviewer) =>
            $$"""{"reviewerId":"{{reviewer}}","jobIds":["{{JobBId}}"]}""";

        (await client.SendAsync(Post("/api/mistakes/reanalyze/jobs", Body(LearnerSub), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var staff = await client.SendAsync(
            Post("/api/mistakes/reanalyze/jobs", Body(StaffSub), Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        (await client.SendAsync(Post("/api/mistakes/reanalyze/jobs", Body("someone-else"), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await client.SendAsync(
            Post("/api/mistakes/reanalyze/jobs", Body(AdminSub), Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 9. GET /api/mistakes/reanalyze/active =====

    [Fact]
    public async Task ActiveReanalyzeMatrix_LearnerForbidden_StaffAdminBoundToSub()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Get($"/api/mistakes/reanalyze/active?reviewerId={LearnerSub}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var staff = await client.SendAsync(
            Get($"/api/mistakes/reanalyze/active?reviewerId={StaffSub}", Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        (await client.SendAsync(
                Get($"/api/mistakes/reanalyze/active?reviewerId={StaffSub}", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await client.SendAsync(
            Get($"/api/mistakes/reanalyze/active?reviewerId={AdminSub}", Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 10. POST /api/mistakes =====

    [Fact]
    public async Task AddItemMatrix_LearnerForbidden_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string body =
            $$"""{"studentId":"{{StudentA}}","subject":2,"grade":3,"sourceUploadId":"upload-new","sourceRegions":[{"sourceImagePath":"mistakes/new.png"}]}""";

        (await client.SendAsync(Post("/api/mistakes", body, Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/api/mistakes", body, Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Post("/api/mistakes", body, Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 11. PUT /api/mistakes/{id} =====

    [Fact]
    public async Task UpdateMatrix_OnlyAdminAllowed_OwnerMigrationAdminOnly()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Learner and Staff are denied outright; only Admin can update, and
        // only through Admin can the owner (studentId) be migrated.
        (await client.SendAsync(Put($"/api/mistakes/{ItemAId}", UpdateBody(StudentB), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Put($"/api/mistakes/{ItemAId}", UpdateBody(StudentB), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await client.SendAsync(
            Put($"/api/mistakes/{ItemAId}", UpdateBody(StudentB), Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync()).Should().Contain(StudentB);
    }

    // ===== 12. DELETE /api/mistakes/{id} =====

    [Fact]
    public async Task DeleteMatrix_LearnerOwnAllowedCrossForbidden_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Delete($"/api/mistakes/{ItemA2Id}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Delete($"/api/mistakes/{ItemBId}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Delete($"/api/mistakes/{ItemBId}", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Delete($"/api/mistakes/{ItemBId}", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 13. POST /api/mistakes/complete-review =====

    [Fact]
    public async Task CompleteReviewMatrix_LearnerForbidden_StaffAdminBoundToSub()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string Body(string reviewer) =>
            $$"""{"sourceUploadId":"unknown-upload","reviewerId":"{{reviewer}}"}""";

        (await client.SendAsync(Post("/api/mistakes/complete-review", Body(LearnerSub), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Unknown upload keeps the baseline business result (200 with
        // success=false) once the authorization passes.
        using var staff = await client.SendAsync(
            Post("/api/mistakes/complete-review", Body(StaffSub), Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.Content.ReadAsStringAsync()).Should().Contain("\"success\":false");

        (await client.SendAsync(Post("/api/mistakes/complete-review", Body("someone-else"), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await client.SendAsync(
            Post("/api/mistakes/complete-review", Body(AdminSub), Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 14. POST /api/mistakes/{id}/practices =====

    [Fact]
    public async Task AddPracticeMatrix_LearnerOnlyAndBoundToOwner_StaffAdminForbidden()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string Body(string student) =>
            $$"""{"mistakeItemId":"{{ItemAId}}","studentId":"{{student}}","isCorrect":true}""";

        using var own = await client.SendAsync(
            Post($"/api/mistakes/{ItemAId}/practices", Body(StudentA), Token(LearnerSub)));
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        (await own.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        // practice.studentId must match the item owner.
        (await client.SendAsync(Post($"/api/mistakes/{ItemAId}/practices", Body(StudentB), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Cross-student item: ownership denies before the binding.
        (await client.SendAsync(Post($"/api/mistakes/{ItemBId}/practices", Body(StudentB), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await client.SendAsync(Post($"/api/mistakes/{ItemAId}/practices", Body(StudentA), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post($"/api/mistakes/{ItemAId}/practices", Body(StudentA), Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ===== 15. GET /api/mistakes/{id}/practices =====

    [Fact]
    public async Task GetPracticesMatrix_LearnerOwnAllowedCrossForbidden_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Get($"/api/mistakes/{ItemAId}/practices", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}/practices", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}/practices", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}/practices", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 16. POST /api/mistakes/{id}/process-logs =====

    [Fact]
    public async Task LogProcessMatrix_LearnerOnlyAndBoundToOwner_StaffAdminForbidden()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        string Body(string student) =>
            $$"""{"mistakeItemId":"{{ItemAId}}","studentId":"{{student}}","action":5,"details":"practice logged"}""";

        using var own = await client.SendAsync(
            Post($"/api/mistakes/{ItemAId}/process-logs", Body(StudentA), Token(LearnerSub)));
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        (await own.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        (await client.SendAsync(Post($"/api/mistakes/{ItemAId}/process-logs", Body(StudentB), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post($"/api/mistakes/{ItemBId}/process-logs", Body(StudentB), Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await client.SendAsync(Post($"/api/mistakes/{ItemAId}/process-logs", Body(StudentA), Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post($"/api/mistakes/{ItemAId}/process-logs", Body(StudentA), Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ===== 17. GET /api/mistakes/{id}/process-logs =====

    [Fact]
    public async Task GetProcessLogsMatrix_LearnerOwnAllowedCrossForbidden_StaffAdminAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.SendAsync(Get($"/api/mistakes/{ItemAId}/process-logs", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}/process-logs", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}/process-logs", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}/process-logs", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 18. POST /api/mistakes/presigned-url =====

    [Fact]
    public async Task PresignedMatrix_LearnerOnlyReferencedPaths_StaffAdminPrefixAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Learner: item source region reference -> allowed.
        using var region = await client.SendAsync(
            Post("/api/mistakes/presigned-url", $$"""{"objectPath":"{{ItemAPath}}","expirySeconds":60}""", Token(LearnerSub)));
        region.StatusCode.Should().Be(HttpStatusCode.OK);
        (await region.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        // Learner: practice image path reference -> allowed.
        using var practice = await client.SendAsync(
            Post("/api/mistakes/presigned-url", $$"""{"objectPath":"{{PracticePath}}","expirySeconds":60}""", Token(LearnerSub)));
        practice.StatusCode.Should().Be(HttpStatusCode.OK);

        // Learner: un-referenced object key (even under mistakes/) -> 403
        // before any OSS access.
        (await client.SendAsync(
                Post("/api/mistakes/presigned-url", $$"""{"objectPath":"mistakes/unreferenced.png"}""", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Learner: non-mistakes prefix keeps the baseline 400.
        (await client.SendAsync(
                Post("/api/mistakes/presigned-url", $$"""{"objectPath":"uploads/a.png"}""", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Staff/Admin: any mistakes/ path (review visibility).
        using var staff = await client.SendAsync(
            Post("/api/mistakes/presigned-url", $$"""{"objectPath":"mistakes/anything.png"}""", Token(StaffSub, "teacher")));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        using var admin = await client.SendAsync(
            Post("/api/mistakes/presigned-url", $$"""{"objectPath":"mistakes/anything.png"}""", Token(AdminSub, "admin")));
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== Fail-closed and capability table =====

    [Fact]
    public async Task LearnerScopeFailsClosedWhenResolutionUnavailable_StaffAdminUnaffected()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Unresolvable learner: explicit unavailable (503 semantics), never an
        // empty-set allowance nor an anonymous allowance.
        (await client.SendAsync(Get($"/api/mistakes/{ItemAId}", Token(UnresolvableSub))))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.SendAsync(
                Get("/api/mistakes?subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(UnresolvableSub))))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.SendAsync(
                Post("/api/mistakes/presigned-url", $$"""{"objectPath":"{{ItemAPath}}"}""", Token(UnresolvableSub))))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Staff/Admin paths do not resolve ownership and stay available.
        (await client.SendAsync(Get("/api/mistakes/pending-reviews?page=1&size=10", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemAId}", Token(AdminSub, "admin"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LearnerScopeFailsClosedWhenStudentCapabilityDisabled()
    {
        // Real gateway (no fake): the capability defaults to disabled in
        // Local mode, so the resolution is unavailable before any network
        // call and learner-scope requests fail closed with 503.
        using var factory = NewFactory(useRealGateway: true);
        using var client = factory.CreateClient();

        (await client.SendAsync(
                Get("/api/mistakes?subject=0&grade=0&reviewStatus=0&page=1&size=10", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Staff/Admin are not affected by the Student capability for their
        // own matrix cells.
        (await client.SendAsync(Get($"/api/mistakes/{ItemAId}", Token(StaffSub, "teacher"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== Authorization audit log =====

    [Fact]
    public async Task AuthorizationLogContainsSubjectEndpointResourceDecision_AndNeverTokensOrPresignedUrls()
    {
        // The log contract is asserted on the real authorization service with
        // a spy logger: the Serilog-backed pipeline factory cannot inject
        // providers, and the service only ever passes structured fields to
        // the logger (no token or presigned URL enters the log call).
        var spy = new LogSpy();
        var service = new MistakeApiResourceAuthorizationService(
            new MistakeApiResourceAuthOptions { Enabled = true },
            DefaultGateway(),
            new Moq.Mock<Domain.Services.IMistakeDomainService>().Object,
            spy.CreateLogger<MistakeApiResourceAuthorizationService>());
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, LearnerSub),
        ], "Bearer"));

        var decision = await service.DecideItemOwnershipAsync(
            "GET /api/mistakes/{id}", user, ItemBId.ToString(), StudentB, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        var entry = spy.Entries.Single();
        entry.Should().Contain("Endpoint=GET /api/mistakes/{id}")
            .And.Contain("Subject=" + LearnerSub)
            .And.Contain("Kind=Learner")
            .And.Contain($"Resource={ItemBId}")
            .And.Contain("Decision=Forbidden");
        entry.Should().NotContainAny(["Bearer ", "eyJ"], "no token material may reach the log");
    }

    // ===== resource=false compatibility window =====

    [Fact]
    public async Task ResourceAuthOff_KeepsBaselineAuthorizationBehavior()
    {
        // strict=true, resource=false: a no-role token keeps reaching
        // staff-only endpoints (the slice-1 authentication-only contract).
        using var factory = NewFactory(resource: false);
        using var client = factory.CreateClient();

        (await client.SendAsync(Get("/api/mistakes/pending-reviews?page=1&size=10", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get($"/api/mistakes/{ItemBId}", Token(LearnerSub))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== Helpers =====

    private static MistakeResourceAuthWebApplicationFactory NewFactory(
        bool resource = true,
        ResourceAuthGateway? gatewayOverride = null,
        bool useRealGateway = false)
    {
        var factory = new MistakeResourceAuthWebApplicationFactory(
            strict: true,
            resource: resource,
            useRealGateway ? null : gatewayOverride ?? DefaultGateway());
        factory.SeedData();
        return factory;
    }

    private static ResourceAuthGateway DefaultGateway() => new()
    {
        Resolutions =
        {
            [LearnerSub] = AllowedStudentsResolution.FromIds([StudentA]),
            [EmptyMappingSub] = AllowedStudentsResolution.FromIds([]),
        }
    };

    private static async Task<List<Guid>> GetActiveJobItemIdsAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            Get($"/api/mistakes/reanalyze/active?reviewerId={ExtractSub(token)}", token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        return [.. System.Text.Json.JsonSerializer.Deserialize<JsonJobs>(body, JsonCaseInsensitive)?.Data.Jobs
            .Select(j => Guid.Parse(j.ItemId)) ?? []];
    }

    private sealed class JsonJobs
    {
        public JsonJobsData Data { get; set; } = new();
    }

    private sealed class JsonJobsData
    {
        public List<JsonJob> Jobs { get; set; } = new();
    }

    private sealed class JsonJob
    {
        public string JobId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonCaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    private static string ExtractSub(string token)
    {
        var payload = new JwtSecurityTokenHandler().ReadJwtToken(token).Payload;
        return payload.Sub ?? string.Empty;
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Post(string path, string json, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Put(string path, string json, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Delete(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string UpdateBody(string student) =>
        $$"""{"id":"{{ItemAId}}","studentId":"{{student}}","subject":0,"grade":0,"sourceRegions":[]}""";

    internal static string CreateToken(
        string audience,
        string sub,
        string? role = null,
        string issuer = Issuer,
        SecurityKey? signingKey = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, sub),
            new("unique_name", sub),
        };
        if (role is not null)
        {
            claims.Add(new Claim("role", role));
        }
        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore: (expires ?? now.AddMinutes(5)).AddMinutes(-10),
            expires: expires ?? now.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal sealed class ResourceAuthGateway : IUploadSourceGateway
    {
        public Dictionary<string, AllowedStudentsResolution> Resolutions { get; init; } = new();

        public Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(
            string identityAccountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Resolutions.TryGetValue(identityAccountId, out var resolution)
                ? resolution
                : AllowedStudentsResolution.Unavailable);
        }

        public Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    internal sealed class FakeOssService : IOssService
    {
        public Task<bool> ObjectExistsAsync(string objectPath) => Task.FromResult(true);
        public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600) =>
            Task.FromResult($"https://oss.example/{objectPath}?test-signature");
        public Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();
        public Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();
        public Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null) =>
            throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectPath) => throw new NotImplementedException();
        public Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths) => throw new NotImplementedException();
        public Task CopyObjectAsync(string sourcePath, string destPath) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(string objectPath) => throw new NotImplementedException();
        public Task<int> DeleteManyAsync(IEnumerable<string> objectPaths) => throw new NotImplementedException();
        public Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600) =>
            throw new NotImplementedException();
        public Task<bool> CheckConnectivityAsync() => throw new NotImplementedException();
        public Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null) => throw new NotImplementedException();
        public Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null) =>
            throw new NotImplementedException();
    }

    private static class Seed
    {
        public static string uploadBodyJson(string student) =>
            $$"""{"studentId":"{{student}}","subject":2,"grade":3,"sourceUploadId":"upload-seed","imagePaths":["mistakes/seeded.png"]}""";

        public static void SeedDatabase(MistakeDbContext db)
        {
            db.MistakeItems.AddRange(
                Item(StudentA, ItemAId, UploadA, ItemAPath, reviewStatus: 2),
                Item(StudentA, ItemA2Id, UploadA, "mistakes/a-2.png", reviewStatus: 1),
                Item(StudentB, ItemBId, UploadB, ItemBPath, reviewStatus: 1),
                Item(StudentB, ItemMixedBId, UploadMixed, "mistakes/mixed-b.png", reviewStatus: 1),
                Item(StudentB, ItemB2Id, UploadB, "mistakes/b-2.png", reviewStatus: 1),
                Item(StudentA, Guid.Parse("10000000-0000-0000-0000-0000000000ab"), UploadMixed, "mistakes/mixed-a.png", reviewStatus: 1));
            db.MistakePractices.Add(new MistakePracticeEntity
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
                MistakeItemId = ItemAId,
                StudentId = StudentA,
                ImagePaths = [PracticePath]
            });
            db.ReanalyzeJobs.Add(new ReanalyzeJobEntity
            {
                Id = JobBId,
                ItemId = ItemB2Id,
                ReviewerId = StaffSub,
                StudentId = StudentB,
                Status = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            db.SaveChanges();
        }

        private static MistakeItemEntity Item(
            string studentId, Guid id, string uploadId, string imagePath, int reviewStatus) => new()
        {
            Id = id,
            StudentId = studentId,
            Subject = 2,
            Grade = 3,
            SourceUploadId = uploadId,
            SourceRegions = [new SourceRegionEntry { SourceImagePath = imagePath }],
            ReviewStatus = reviewStatus,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
    }

    /// <summary>
    /// Real Mistake host (Local mode) with strict=true and the IKJEPZ
    /// resource switch. The JWT pipeline is real (static test signing keys);
    /// PostgreSQL is swapped for EF Core InMemory and seeded per factory
    /// instance; IUploadSourceGateway/IOssService are replaced with fakes.
    /// Passing gatewayOverride=null keeps the real gateway, whose Student
    /// capability is disabled by default in Local mode (fail-closed path).
    /// </summary>
    internal sealed class MistakeResourceAuthWebApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string[] OwnedEnvironmentKeys =
        [
            "Mistake__ConfigMode",
            "ConnectionStrings__MistakeLocal",
            "Mistake__Local__Oss__InternalEndpoint",
            "Mistake__Local__Oss__InternalSecure",
            "Mistake__Local__Oss__AccessKey",
            "Mistake__Local__Oss__SecretKey",
            "Mistake__Local__Oss__BucketName",
            "Mistake__Local__Oss__PublicBaseUrl",
            "IdentityService__Authority",
            "IdentityService__Issuer",
            "IdentityService__Audience",
            "IdentityService__RequireHttpsMetadata",
            "IdentityService__AdminAudience",
            "IdentityService__MistakeApiStrictAuth",
            "IdentityService__MistakeApiResourceAuth",
            "IDENTITY_APP_ID_ADMIN",
        ];

        private readonly ResourceAuthGateway? _gatewayOverride;

        public MistakeResourceAuthWebApplicationFactory(
            bool strict, bool resource, ResourceAuthGateway? gatewayOverride = null)
        {
            _gatewayOverride = gatewayOverride;
            Environment.SetEnvironmentVariable("IDENTITY_APP_ID_ADMIN", null);
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", "Local");
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__MistakeLocal",
                "Host=127.0.0.1;Port=5432;Database=mistake_resource_auth_tests;Username=mistake_tester;Password=mistake-resource-auth-tests-only");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__InternalEndpoint", "127.0.0.1:19031");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__AccessKey", "resource-auth-test-access");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__SecretKey", "resource-auth-test-secret");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__BucketName", "mistake-resource-auth-tests");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__PublicBaseUrl", "http://127.0.0.1/oss");
            Environment.SetEnvironmentVariable("IdentityService__Authority", Issuer);
            Environment.SetEnvironmentVariable("IdentityService__Issuer", Issuer);
            Environment.SetEnvironmentVariable("IdentityService__Audience", SharedAudience);
            Environment.SetEnvironmentVariable("IdentityService__RequireHttpsMetadata", "false");
            Environment.SetEnvironmentVariable("IdentityService__AdminAudience", null);
            Environment.SetEnvironmentVariable("IdentityService__MistakeApiStrictAuth", strict ? "true" : null);
            Environment.SetEnvironmentVariable(
                "IdentityService__MistakeApiResourceAuth", resource ? "true" : null);
        }

        protected override void Dispose(bool disposing)
        {
            foreach (var key in OwnedEnvironmentKeys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var databaseName = $"mistake_resource_auth_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));

                if (_gatewayOverride is not null)
                {
                    services.RemoveAll<IUploadSourceGateway>();
                    services.AddScoped<IUploadSourceGateway>(_ => _gatewayOverride);
                }
                services.RemoveAll<IOssService>();
                services.AddSingleton<IOssService>(new FakeOssService());

                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, UseStaticSigningKeys);
                services.PostConfigure<JwtBearerOptions>(Ruoyu.Study.Common.Authentication.RuoyuAdminApiAuthorization.SchemeName, UseStaticSigningKeys);
            });
        }

        /// <summary>
        /// Seeds the shared InMemory store. Must run after the host is built
        /// (first Services access) so request scopes see the same store.
        /// </summary>
        public void SeedData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            Seed.SeedDatabase(db);
        }

        private static void UseStaticSigningKeys(JwtBearerOptions options)
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(SigningKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.Authority = Issuer;
            options.RequireHttpsMetadata = false;
        }
    }

    internal sealed class LogSpy
    {
        public List<string> Entries { get; } = new();

        public ILogger<T> CreateLogger<T>() => new SpyLogger<T>(this);

        private sealed class SpyLogger<T>(LogSpy owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                {
                    owner.Entries.Add(formatter(state, exception));
                }
            }
        }
    }
}
