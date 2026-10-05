extern alias consumer;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using consumer::Ruoyu.Study.MistakeBff.HttpClients;
using static Ruoyu.Study.Mistake.Service.Tests.MistakeApiResourceAuthTests;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>Actual producer Program, JWT policies, endpoints and source guards;
/// only persistence, OSS and Student are the existing isolated host fixtures.</summary>
public sealed class MistakeConsumerHostModesTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingModesShareSafeConsumerOutcomesWithoutRelaxingProducerGuardsAsync(bool strict, bool resource)
    {
        using var factory = new MistakeResourceAuthWebApplicationFactory(strict, resource, new ResourceAuthGateway());
        factory.SeedData();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddMistakeHttpClient("http://localhost");
        var wire = new ObservedWire(factory.Server.CreateHandler());
        services.AddHttpClient<IMistakeHttpClient, MistakeHttpClient>()
            .ConfigurePrimaryHttpMessageHandler(() => wire);
        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<IHttpContextAccessor>();
        void Token(string? sub, string? role = null)
        {
            context.HttpContext = new DefaultHttpContext();
            if (sub != null) context.HttpContext.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                CreateToken("QuantumZhou.microservices", sub, role)).ToString();
        }
        var client = provider.GetRequiredService<IMistakeHttpClient>();
        Token("staff-1", "teacher");
        (await client.GetMistakeItemListAsync("student-A", 0, 0, 0, 1, 50)).Items.Should().HaveCount(3);
        (await client.GetMistakeItemAsync("10000000-0000-0000-0000-00000000000a"))!.StudentId.Should().Be("student-A");
        (await client.CompleteUploadReviewAsync("missing-upload", "staff-1")).Success.Should().BeFalse();
        (await client.GetMistakeItemAsync(Guid.NewGuid().ToString())).Should().BeNull();
        Token(null);
        if (strict)
        {
            await RejectedAsync(() => client.GetMistakeItemListAsync("student-A", 0, 0, 0, 1, 50), 502);
            wire.LastStatus.Should().Be(401);
        }
        else
            (await client.GetMistakeItemListAsync("student-A", 0, 0, 0, 1, 50)).Items.Should().HaveCount(3);

        // A real managed relationship triggers the permanent authentication/role
        // entry guard even with both rollout switches disabled. No intake work is run.
        var managedId = Guid.Parse("10000000-0000-0000-0000-0000000000aa");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
            var source = Guid.NewGuid(); var revision = Guid.NewGuid();
            db.SourceIntakes.Add(new SourceIntakeEntity { SourceUploadId = source, ContentRevision = revision,
                StudentId = "student-A", Manifest = "{}", Mode = "Managed" });
            db.SourceIntakeGroups.Add(new SourceIntakeGroupEntity { ItemId = managedId, SourceUploadId = source,
                ContentRevision = revision, GroupHash = "host-mode-auth-boundary", Regions = "[]", Result = "{}" });
            var reserved = await db.MistakeItems.SingleAsync(x => x.Id == Guid.Parse("10000000-0000-0000-0000-00000000000a"));
            reserved.SourceRegions = [new SourceRegionEntry { SourceImagePath = "mistakes/source-targets/boundary.png" }];
            await db.SaveChangesAsync();
        }
        Token(null);
        await RejectedAsync(() => client.GetMistakeItemAsync(managedId.ToString()), 502);
        wire.LastStatus.Should().Be(401);
        Token("learner-1");
        await RejectedAsync(() => client.ReviewMistakeItemAsync(managedId.ToString(), MistakeReviewStatus.Confirmed,
            "learner-1", null, 3, 2, null), 502);
        wire.LastStatus.Should().Be(403);
        Token("staff-1", "teacher");
        await RejectedAsync(() => client.CompleteUploadReviewAsync("upload-A", "staff-1"), 409);
        wire.LastStatus.Should().Be(409);
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        (await finalDb.MistakeItems.SingleAsync(x => x.Id == managedId)).ReviewStatus.Should().Be(1);
        (await finalDb.MistakeItems.CountAsync()).Should().Be(6);
    }

    private sealed class ObservedWire(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int LastStatus { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            LastStatus = (int)response.StatusCode;
            return response;
        }
    }

    private static async Task RejectedAsync(Func<Task> operation, int expectedStatus)
    {
        var error = await Assert.ThrowsAsync<MistakeFailureException>(operation);
        error.StatusCode.Should().Be(expectedStatus);
        error.InnerException.Should().BeNull();
        error.ToString().Should().NotContain("authentication_required").And.NotContain("reserved_target");
    }
}
