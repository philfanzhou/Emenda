using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Ruoyu.Study.Mistake.StudentClient;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class UploadPollingScanTests
{
    [Fact]
    public async Task WorkerReadsSecondPageAfterFirstPageChangesWithoutOffset()
    {
        var gateway = new StubGateway((cursor, _) => cursor switch
        {
            null => new ProcessingUploadsPage
            {
                Items = Enumerable.Range(0, 50).Select(i => new ProcessingUploadRecord { Id = i.ToString(), StudentId = "student" }).ToList(),
                HasMore = true,
                NextCursor = "after-first"
            },
            "after-first" => new ProcessingUploadsPage
            {
                Items = new() { new ProcessingUploadRecord { Id = "last", StudentId = "student" } },
                HasMore = false
            },
            _ => throw new InvalidOperationException("Unexpected cursor")
        });

        await RunCycleAsync(gateway, CancellationToken.None);
        Assert.Equal(new string?[] { null, "after-first" }, gateway.Cursors);
    }

    [Fact]
    public async Task ReadFailureStopsCycleAndNextCycleStartsFresh()
    {
        var gateway = new StubGateway((_, _) => throw new ProcessingUploadScanException(
            ProcessingUploadScanFailure.Transport, "unavailable"));
        await RunCycleAsync(gateway, CancellationToken.None);
        await RunCycleAsync(gateway, CancellationToken.None);
        Assert.Equal(new string?[] { null, null }, gateway.Cursors);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var gateway = new StubGateway((_, ct) => throw new OperationCanceledException(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCycleAsync(gateway, source.Token));
        Assert.Empty(gateway.Cursors);
    }

    private static Task RunCycleAsync(IUploadSourceGateway gateway, CancellationToken cancellationToken)
    {
        var provider = new ServiceCollection().AddSingleton(gateway).BuildServiceProvider();
        var worker = new UploadPollingWorker(provider, Options.Create(new VlAnalyzerOptions()),
            NullLogger<UploadPollingWorker>.Instance);
        var method = typeof(UploadPollingWorker).GetMethod("PollAndProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(worker, new object[] { cancellationToken })!;
    }

    private sealed class StubGateway(Func<string?, CancellationToken, ProcessingUploadsPage> read) : IUploadSourceGateway
    {
        public List<string?> Cursors { get; } = new();

        public Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
        {
            Cursors.Add(cursor);
            return Task.FromResult(read(cursor, cancellationToken));
        }

        public Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<CreateReturnedUploadRecordResult> CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(string identityAccountId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
