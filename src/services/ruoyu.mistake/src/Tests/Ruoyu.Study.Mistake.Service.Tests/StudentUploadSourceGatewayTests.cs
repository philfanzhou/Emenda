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
        var source = new ProcessingUploadScanPage
        {
            Items = new List<UploadRecordDto>
            {
                new() { Id = "upload-1", StudentId = "student-1", ImageEntries = new List<ImageEntryDto> { sourceEntry } }
            },
            HasMore = true,
            NextCursor = "next"
        };
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        client.Setup(c => c.ScanProcessingUploadRecordsAsync("cursor", 1, cancellation.Token)).ReturnsAsync(source);

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability()).GetProcessingUploadsAsync("cursor", 1, cancellation.Token);

        Assert.True(actual.HasMore);
        Assert.Equal("next", actual.NextCursor);
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
        client.Setup(c => c.ScanProcessingUploadRecordsAsync(null, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessingUploadScanPage { Items = new(), HasMore = false });

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability()).GetProcessingUploadsAsync(null, 50);

        Assert.Empty(actual.Items);
        Assert.False(actual.HasMore);
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

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability()).RemoveProcessedImagesAsync("record", paths);

        Assert.Equal((success, deleted, remaining, error),
            (actual.Success, actual.RecordDeleted, actual.RemainingImageCount, actual.ErrorMessage));
        Assert.Equal(paths, delegatedPaths);
        Assert.NotSame(paths, delegatedPaths);
        client.VerifyAll();
    }

    [Fact]
    public async Task CreateReturnedRecordAsync_PreservesArgumentsAndClassifiedOutcome()
    {
        var entries = new List<UploadImageEntry> { new() { Path = "uploads/return.png", Type = "mistake" } };
        List<ImageEntryDto>? delegatedEntries = null;
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.CreateReturnedRecordAsync("student", It.IsAny<List<ImageEntryDto>>(), "Rejected", "reason", "mistake-return:1", cancellation.Token))
            .Callback<string, List<ImageEntryDto>, string?, string?, string?, CancellationToken>((_, sent, _, _, _, _) => delegatedEntries = sent)
            .ReturnsAsync(new CreateReturnedRecordOutcome
            {
                Status = CreateReturnedRecordStatus.Succeeded, Replayed = true, Message = "ok"
            });

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability()).CreateReturnedRecordAsync(
            "student", entries, "Rejected", "reason", "mistake-return:1", cancellation.Token);

        Assert.Equal(CreateReturnedUploadRecordStatus.Succeeded, actual.Status);
        Assert.True(actual.Replayed);
        Assert.Equal("ok", actual.Message);
        var delegatedEntry = Assert.Single(delegatedEntries!);
        Assert.Equal(("uploads/return.png", "mistake"),
            (delegatedEntry.Path, delegatedEntry.Type));
        entries[0].Path = "changed";
        Assert.Equal("uploads/return.png", delegatedEntry.Path);
        client.VerifyAll();
    }

    [Theory]
    [InlineData(CreateReturnedRecordStatus.Succeeded, CreateReturnedUploadRecordStatus.Succeeded)]
    [InlineData(CreateReturnedRecordStatus.PayloadConflict, CreateReturnedUploadRecordStatus.PayloadConflict)]
    [InlineData(CreateReturnedRecordStatus.Unknown, CreateReturnedUploadRecordStatus.Unknown)]
    public async Task CreateReturnedRecordAsync_MapsEveryFailureClassification(
        CreateReturnedRecordStatus source, CreateReturnedUploadRecordStatus expected)
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.CreateReturnedRecordAsync("student", It.IsAny<List<ImageEntryDto>>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateReturnedRecordOutcome { Status = source, Message = "m" });

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability()).CreateReturnedRecordAsync(
            "student", new List<UploadImageEntry>(), null, null, null);

        Assert.Equal(expected, actual.Status);
        Assert.Equal("m", actual.Message);
        client.VerifyAll();
    }

    [Fact]
    public async Task CreateReturnedRecordAsync_PreservesNullOperationIdForLegacyCalls()
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.CreateReturnedRecordAsync("student", It.IsAny<List<ImageEntryDto>>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateReturnedRecordOutcome { Status = CreateReturnedRecordStatus.Succeeded });

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability()).CreateReturnedRecordAsync(
            "student", new List<UploadImageEntry>(), null, null, null);

        Assert.Equal(CreateReturnedUploadRecordStatus.Succeeded, actual.Status);
        client.VerifyAll();
    }

    [Fact]
    public async Task Gateway_PropagatesExceptionsAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        var failure = new InvalidOperationException("upstream failure");
        client.Setup(c => c.ScanProcessingUploadRecordsAsync(null, 50, cancellation.Token))
            .ThrowsAsync(failure);
        client.Setup(c => c.RemoveProcessedImagesAsync("record", It.IsAny<List<string>>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var gateway = new StudentUploadSourceGateway(client.Object, EnabledCapability());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.GetProcessingUploadsAsync(null, 50, cancellation.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.RemoveProcessedImagesAsync("record", new List<string>(), cancellation.Token));
        client.VerifyAll();
    }

    private static StudentCapabilityOptions EnabledCapability() => new() { Enabled = true };

    [Fact]
    public async Task GetAllowedStudentIdsAsync_ReturnsUnavailableWithoutCallingStudentWhenCapabilityDisabled()
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        var gateway = new StudentUploadSourceGateway(client.Object, new StudentCapabilityOptions { Enabled = false });

        var actual = await gateway.GetAllowedStudentIdsAsync("account-1");

        Assert.False(actual.Available);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAllowedStudentIdsAsync_MapsDistinctNonEmptyStudentIds()
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.GetStudentsByIdentityAccountIdStrictAsync("account-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StudentsByAccountResult
            {
                Students = new List<StudentInfoDto>
                {
                    new() { Id = "student-1" },
                    new() { Id = "student-2" },
                    new() { Id = "student-1" },
                    new() { Id = "" }
                }
            });

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability())
            .GetAllowedStudentIdsAsync("account-1");

        Assert.True(actual.Available);
        Assert.Equal(new[] { "student-1", "student-2" }, actual.StudentIds);
        client.VerifyAll();
    }

    [Fact]
    public async Task GetAllowedStudentIdsAsync_EmptyMappingStaysAvailable()
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.GetStudentsByIdentityAccountIdStrictAsync("account-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StudentsByAccountResult());

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability())
            .GetAllowedStudentIdsAsync("account-1");

        Assert.True(actual.Available);
        Assert.Empty(actual.StudentIds);
        client.VerifyAll();
    }

    [Fact]
    public async Task GetAllowedStudentIdsAsync_FailsClosedOnResolutionFailure()
    {
        // The strict client throws HttpRequestException both for transport
        // failures and for non-success envelopes; the gateway must surface
        // both as "unavailable", never as an empty mapping.
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        client.Setup(c => c.GetStudentsByIdentityAccountIdStrictAsync("account-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability())
            .GetAllowedStudentIdsAsync("account-1");

        Assert.False(actual.Available);
        client.VerifyAll();
    }

    [Fact]
    public async Task GetAllowedStudentIdsAsync_FailsClosedOnTimeout()
    {
        var client = new Mock<IStudentHttpClient>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        client.Setup(c => c.GetStudentsByIdentityAccountIdStrictAsync("account-1", cancellation.Token))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var actual = await new StudentUploadSourceGateway(client.Object, EnabledCapability())
            .GetAllowedStudentIdsAsync("account-1", cancellation.Token);

        Assert.False(actual.Available);
        client.VerifyAll();
    }
}
