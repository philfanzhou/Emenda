using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.StudentIntegration;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEY6 acceptance tests for the polling worker's removal-callback
/// classification: success (fresh or tombstone replay) logs Information,
/// unknown_record logs a Warning with manual-investigation semantics, other
/// failures keep the baseline Warning, and transport exceptions keep the
/// baseline Error.
/// </summary>
public sealed class UploadPollingNotifyTests
{
    [Fact]
    public async Task SuccessfulRemoval_LogsInformation()
    {
        var (logger, gateway) = await NotifyAsync(new RemoveProcessedUploadImagesResult
        {
            Success = true, RecordDeleted = false, RemainingImageCount = 2
        });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Removed processed images", entry.Message);
        Assert.Contains("RecordDeleted=False", entry.Message);
        Assert.Equal("record-1", Assert.Single(gateway.RemoveCalls).RecordId);
    }

    [Fact]
    public async Task TombstoneReplaySuccess_LogsInformationWithDeletedFlag()
    {
        var (logger, _) = await NotifyAsync(new RemoveProcessedUploadImagesResult
        {
            Success = true, RecordDeleted = true, RemainingImageCount = 0
        });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("RecordDeleted=True", entry.Message);
    }

    [Fact]
    public async Task UnknownRecord_LogsWarningRequiringManualInvestigation()
    {
        var (logger, _) = await NotifyAsync(new RemoveProcessedUploadImagesResult
        {
            Success = false, UnknownRecord = true
        });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("unknown record", entry.Message);
        Assert.Contains("manual investigation", entry.Message);
    }

    [Fact]
    public async Task OtherFailure_LogsBaselineWarning()
    {
        var (logger, _) = await NotifyAsync(new RemoveProcessedUploadImagesResult
        {
            Success = false, ErrorMessage = "student rejected removal"
        });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Failed to remove processed images", entry.Message);
        Assert.Contains("student rejected removal", entry.Message);
    }

    [Fact]
    public async Task TransportException_LogsErrorWithoutThrowing()
    {
        var logger = new SpyLogger();
        var gateway = new ThrowingGateway(new HttpRequestException("unreachable"));
        var provider = new ServiceCollection()
            .AddSingleton<IUploadSourceGateway>(gateway)
            .BuildServiceProvider();
        var worker = new UploadPollingWorker(provider, Options.Create(new VlAnalyzerOptions()), logger);
        var method = typeof(UploadPollingWorker).GetMethod(
            "NotifyStudentToRemoveImagesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)method.Invoke(worker,
            new object[] { provider.CreateScope().ServiceProvider, "record-1", new List<string>(), CancellationToken.None })!;

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Failed to call RemoveProcessedImages", entry.Message);
    }

    private static async Task<(SpyLogger Logger, RecordingGateway Gateway)> NotifyAsync(
        RemoveProcessedUploadImagesResult result)
    {
        var gateway = new RecordingGateway(result);
        var logger = new SpyLogger();
        var provider = new ServiceCollection()
            .AddSingleton<IUploadSourceGateway>(gateway)
            .BuildServiceProvider();
        var worker = new UploadPollingWorker(provider, Options.Create(new VlAnalyzerOptions()), logger);
        var method = typeof(UploadPollingWorker).GetMethod(
            "NotifyStudentToRemoveImagesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(worker,
            new object[] { provider.CreateScope().ServiceProvider, "record-1", new List<string> { "uploads/a" }, CancellationToken.None })!;
        return (logger, gateway);
    }

    private sealed class RecordingGateway(RemoveProcessedUploadImagesResult result) : IUploadSourceGateway
    {
        public List<(string RecordId, List<string> Paths)> RemoveCalls { get; } = new();

        public Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add((recordId, imagePaths.ToList()));
            return Task.FromResult(result);
        }

        public Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CreateReturnedUploadRecordResult> CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(string identityAccountId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class ThrowingGateway(Exception exception) : IUploadSourceGateway
    {
        public Task<RemoveProcessedUploadImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<ProcessingUploadsPage> GetProcessingUploadsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CreateReturnedUploadRecordResult> CreateReturnedRecordAsync(string studentId, List<UploadImageEntry> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<AllowedStudentsResolution> GetAllowedStudentIdsAsync(string identityAccountId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class SpyLogger : ILogger<UploadPollingWorker>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
