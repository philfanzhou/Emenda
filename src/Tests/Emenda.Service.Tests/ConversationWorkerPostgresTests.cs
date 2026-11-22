using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Host.Services.Conversation;
using Emenda.HttpContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Emenda.Service.Tests;

public sealed class ConversationWorkerPostgresTests
{
    [Fact]
    public async Task ActualWorkerClaimsAdmittedEvidenceAndCommitsOneIndependentMistake()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        const string student = "worker-learner", actor = "worker-account", owner = "worker-owner";
        var conversation = Guid.NewGuid();
        var question = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var eventId = ConversationExport.Hash($"{ConversationExport.Hash($"{owner}\n{conversation:D}\n0")}\n{question:D}");
        var packet = new ConversationExport(1, conversation,
            [new(eventId, 0, new(question, 2, "2 + 2 = ?", [0], [], new(0, "5", null), "incorrect",
                "2 + 2 is 4, not the supplied 5.", "4", [], false))], [], [new(0, "2 + 2 = 5", null)]);
        var fingerprint = ConversationExport.Hash(JsonSerializer.Serialize(packet, ConversationExport.Json));
        var authorization = new Mock<IConversationAuthorization>();
        authorization.Setup(a => a.AllowedAsync(actor, student, owner, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var exports = new Mock<IConversationExportReader>();
        exports.Setup(e => e.ReadAsync(operation, owner, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string _, bool checkOnly, CancellationToken _) => new ConversationCollectionResponse("completed", checkOnly ? null : packet, fingerprint));
        var services = new ServiceCollection();
        services.AddDbContext<MistakeDbContext>(options => options.UseNpgsql(database.Connection));
        services.AddSingleton(authorization.Object);
        services.AddSingleton(exports.Object);
        services.AddSingleton<IOssService>(database.Files);
        services.AddScoped<ConversationCollectionStore>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ConversationCollectionStore>().AdmitAsync("platform", actor, operation, student, owner, 7, CancellationToken.None);
        using var worker = new ConversationCollectionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConversationCollectionWorker>.Instance, new ConversationCollectionOptions { CallerName = "platform" });
        await worker.RunOneAsync(CancellationToken.None);
        await using var db = database.CreateContext();
        var job = await db.ConversationCollections.SingleAsync();
        Assert.Equal("completed", job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Null(job.LeaseId);
        var item = await db.MistakeItems.SingleAsync();
        Assert.Equal(student, item.StudentId);
        Assert.Equal("platform", item.ConversationCaller);
        Assert.Equal("", item.SourceUploadId);
        Assert.True(item.ConversationActive);
        var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(item.ConversationSource!, ConversationExport.Json)!;
        Assert.Equal("2 + 2 = ?", snapshot.Statement);
        Assert.Equal("5", snapshot.StudentAttempt!.Quote);
        Assert.Single(await db.ConversationMistakeReceipts.ToListAsync());
        await worker.RunOneAsync(CancellationToken.None);
        Assert.Equal(1, await db.MistakeItems.CountAsync());
        Assert.Equal(1, await db.ConversationMistakeReceipts.CountAsync());
    }
}
