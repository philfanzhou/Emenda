using Moq;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Ruoyu.Study.MistakeBff.HttpClients;

namespace Ruoyu.Study.Mistake.Service.Tests;

public class StudentUploadSourceGatewayTests
{
    [Fact]
    public async Task GetProcessingUploadsAsync_MapsAllUsedFieldsWithoutSharingMutableLists()
    {
        var sourceEntry = new ImageEntryDto { Path = "uploads/中文 A.png", Type = "mistake" };
        var source = new UploadRecordsPage
        {
            Items = new List<UploadRecordDto>
            {
                new() { Id = "upload-1", StudentId = "student-1", ImageEntries = new List<ImageEntryDto> { sourceEntry } }
            },
            TotalCount = 7,
            Page = 2,
            PageSize = 1
        };
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        client.Setup(c => c.GetProcessingUploadRecordsAsync(2, 1, cancellation.Token)).ReturnsAsync(source);

        var actual = await new StudentUploadSourceGateway(client.Object).GetProcessingUploadsAsync(2, 1, cancellation.Token);

        Assert.Equal((7, 2, 1), (actual.TotalCount, actual.Page, actual.PageSize));
        var record = Assert.Single(actual.Items);
        Assert.Equal(("upload-1", "student-1"), (record.Id, record.StudentId));
        var entry = Assert.Single(record.ImageEntries);
        Assert.Equal(("uploads/中文 A.png", "mistake"), (entry.Path, entry.Type));
        sourceEntry.Path = "changed";
        source.Items.Clear();
        Assert.Equal("uploads/中文 A.png", entry.Path);
        Assert.Single(actual.Items);
        client.VerifyAll();
    }

    [Fact]
    public async Task GetProcessingUploadsAsync_PreservesEmptyPage()
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.GetProcessingUploadRecordsAsync(1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadRecordsPage { TotalCount = 0, Page = 1, PageSize = 50 });

        var actual = await new StudentUploadSourceGateway(client.Object).GetProcessingUploadsAsync(1, 50);

        Assert.Empty(actual.Items);
        Assert.Equal(0, actual.TotalCount);
        client.VerifyAll();
    }

    [Theory]
    [InlineData(true, true, 0, "")]
    [InlineData(false, false, 2, "student rejected removal")]
    public async Task RemoveProcessedImagesAsync_PreservesResultAndCopiesInput(bool success, bool deleted, int remaining, string error)
    {
        var paths = new List<string> { "uploads/a" };
        List<string>? delegatedPaths = null;
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.RemoveProcessedImagesAsync("record", It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, List<string>, CancellationToken>((_, sent, _) => delegatedPaths = sent)
            .ReturnsAsync(new RemoveProcessedImagesResult
            {
                Success = success, RecordDeleted = deleted, RemainingImageCount = remaining, ErrorMessage = error
            });

        var actual = await new StudentUploadSourceGateway(client.Object).RemoveProcessedImagesAsync("record", paths);

        Assert.Equal((success, deleted, remaining, error),
            (actual.Success, actual.RecordDeleted, actual.RemainingImageCount, actual.ErrorMessage));
        Assert.Equal(paths, delegatedPaths);
        Assert.NotSame(paths, delegatedPaths);
        client.VerifyAll();
    }

    [Fact]
    public async Task CreateReturnedRecordAsync_PreservesArgumentsTaskAndCancellation()
    {
        var entries = new List<UploadImageEntry> { new() { Path = "uploads/return.png", Type = "mistake" } };
        List<ImageEntryDto>? delegatedEntries = null;
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.CreateReturnedRecordAsync("student", It.IsAny<List<ImageEntryDto>>(), "Rejected", "reason", cancellation.Token))
            .Callback<string, List<ImageEntryDto>, string?, string?, CancellationToken>((_, sent, _, _, _) => delegatedEntries = sent)
            .Returns(completion.Task);

        var task = new StudentUploadSourceGateway(client.Object).CreateReturnedRecordAsync(
            "student", entries, "Rejected", "reason", cancellation.Token);

        Assert.Same(completion.Task, task);
        Assert.False(task.IsCompleted);
        var delegatedEntry = Assert.Single(delegatedEntries!);
        Assert.Equal(("uploads/return.png", "mistake"),
            (delegatedEntry.Path, delegatedEntry.Type));
        entries[0].Path = "changed";
        Assert.Equal("uploads/return.png", delegatedEntry.Path);
        completion.SetResult();
        await task;
        client.VerifyAll();
    }

    [Fact]
    public async Task Gateway_PropagatesExceptionsAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        var failure = new InvalidOperationException("upstream failure");
        client.Setup(c => c.GetProcessingUploadRecordsAsync(1, 50, cancellation.Token))
            .ThrowsAsync(failure);
        client.Setup(c => c.RemoveProcessedImagesAsync("record", It.IsAny<List<string>>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var gateway = new StudentUploadSourceGateway(client.Object);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.GetProcessingUploadsAsync(1, 50, cancellation.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.RemoveProcessedImagesAsync("record", new List<string>(), cancellation.Token));
        client.VerifyAll();
    }
}
