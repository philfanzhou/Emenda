using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Xunit;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for UploadPollingWorker covering poll-and-process logic,
/// VL analysis integration, OSS migration, and error handling.
/// </summary>
public class UploadPollingWorkerTests
{
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<ILogger<UploadPollingWorker>> _mockLogger;
    private readonly Mock<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IVlAnalyzer> _mockVlAnalyzer;
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<IOssService> _mockOssService;
    private VlAnalyzerOptions _vlOptions;

    public UploadPollingWorkerTests()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockLogger = new Mock<ILogger<UploadPollingWorker>>();
        _mockStudentClient = new Mock<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
        _mockVlAnalyzer = new Mock<IVlAnalyzer>();
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockOssService = new Mock<IOssService>();
        _vlOptions = new VlAnalyzerOptions
        {
            ApiKey = "test-api-key",
            Model = "test-model",
            ApiBaseUrl = "http://localhost:8000",
            Temperature = 0.1,
            TimeoutSeconds = 30,
            MaxRetries = 0,
            RetryDelayMs = 0
        };

        // Default scoped service resolution
        var mockScope = new Mock<IServiceProvider>();
        mockScope.Setup(x => x.GetService(typeof(StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient)))
            .Returns(_mockStudentClient.Object);
        mockScope.Setup(x => x.GetService(typeof(IVlAnalyzer)))
            .Returns(_mockVlAnalyzer.Object);
        mockScope.Setup(x => x.GetService(typeof(IMistakeDomainService)))
            .Returns(_mockDomainService.Object);
        mockScope.Setup(x => x.GetService(typeof(IOssService)))
            .Returns(_mockOssService.Object);

        _mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(new MockServiceScopeFactory(mockScope.Object));
    }

    private UploadPollingWorker CreateWorker(VlAnalyzerOptions? options = null)
    {
        var opts = options ?? _vlOptions;
        return new UploadPollingWorker(
            _mockServiceProvider.Object,
            Options.Create(opts),
            _mockLogger.Object);
    }

    private static StudentProto.UploadRecordDto CreateRecord(
        string recordId = "record-1",
        string studentId = "student-1",
        string comments = "test comments",
        List<StudentProto.ImageEntry>? entries = null)
    {
        var record = new StudentProto.UploadRecordDto
        {
            Id = recordId,
            StudentId = studentId,
            Comments = comments,
            Status = StudentProto.UploadStatus.Processing
        };
        if (entries != null)
        {
            record.ImageEntries.Add(entries);
        }
        return record;
    }

    private static StudentProto.ImageEntry CreateImageEntry(string path, string type = "mistake")
    {
        return new StudentProto.ImageEntry { Path = path, Type = type };
    }

    private static MistakeItemModel CreateMistakeItem(string studentId = "student-1", int subject = 1, int grade = 7)
    {
        return new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            Subject = subject,
            Grade = grade,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AsyncUnaryCall<StudentProto.UploadRecordsPageResult> CreateAsyncCall(StudentProto.UploadRecordsPageResult result)
    {
        return new AsyncUnaryCall<StudentProto.UploadRecordsPageResult>(
            Task.FromResult(result),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            null!,
            null!);
    }

    private static AsyncUnaryCall<StudentProto.RemoveProcessedImagesResponse> CreateAsyncCall(StudentProto.RemoveProcessedImagesResponse result)
    {
        return new AsyncUnaryCall<StudentProto.RemoveProcessedImagesResponse>(
            Task.FromResult(result),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            null!,
            null!);
    }

    private void SetupStudentClientResponse(StudentProto.UploadRecordsPageResult result)
    {
        _mockStudentClient
            .Setup(x => x.GetProcessingUploadRecordsAsync(
                It.IsAny<StudentProto.GetProcessingUploadRecordsRequest>(),
                null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncCall(result));
    }

    private void SetupVlAnalyzerResponse(VlAnalysisResult result)
    {
        _mockVlAnalyzer
            .Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private void SetupDomainServiceSubmit(List<MistakeItemModel> items)
    {
        _mockDomainService
            .Setup(x => x.SubmitMistakeUploadAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<List<string>>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(items);
    }

    #region PollAndProcessAsync - Happy Path

    [Fact]
    public async Task PollAndProcessAsync_HappyPath_ShouldCreateMistakeItemAndMigrateOss()
    {
        // Arrange: 1 Processing record with 2 mistake images, VL returns 1 group
        var record = CreateRecord(entries: new List<StudentProto.ImageEntry>
        {
            CreateImageEntry("uploads/student-1/img1.jpg"),
            CreateImageEntry("uploads/student-1/img2.jpg")
        });

        var pageResult = new StudentProto.UploadRecordsPageResult
        {
            TotalCount = 1,
            Page = 1,
            PageSize = 50
        };
        pageResult.Items.Add(record);
        SetupStudentClientResponse(pageResult);

        SetupVlAnalyzerResponse(new VlAnalysisResult
        {
            Groups = new List<VlImageGroup>
            {
                new()
                {
                    ImageIndices = new List<int> { 0, 1 },
                    Subject = 3,
                    Grade = 8,
                    RootCause = "English vocabulary"
                }
            }
        });

        var mistakeItem = CreateMistakeItem(subject: 3, grade: 8);
        mistakeItem.SourceRegions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "uploads/student-1/img1.jpg" },
            new() { SourceImagePath = "uploads/student-1/img2.jpg" }
        };
        SetupDomainServiceSubmit(new List<MistakeItemModel> { mistakeItem });

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var removeResponse = new StudentProto.RemoveProcessedImagesResponse
        {
            Success = true,
            RecordDeleted = true,
            RemainingImageCount = 0
        };
        _mockStudentClient
            .Setup(x => x.RemoveProcessedImagesAsync(
                It.IsAny<StudentProto.RemoveProcessedImagesRequest>(),
                null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncCall(removeResponse));

        var worker = CreateWorker();

        // Act
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert
        _mockDomainService.Verify(x => x.SubmitMistakeUploadAsync(
            "student-1", 3, 8,
            It.Is<List<string>>(p => p.Count == 2),
            "English vocabulary",
            "record-1"), Times.Once);

        _mockOssService.Verify(x => x.CopyObjectAsync("uploads/student-1/img1.jpg", "mistakes/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.CopyObjectAsync("uploads/student-1/img2.jpg", "mistakes/student-1/img2.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("uploads/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("uploads/student-1/img2.jpg"), Times.Once);

        _mockStudentClient.Verify(x => x.RemoveProcessedImagesAsync(
            It.Is<StudentProto.RemoveProcessedImagesRequest>(r => r.RecordId == "record-1"),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region PollAndProcessAsync - No Mistake Images

    [Fact]
    public async Task PollAndProcessAsync_WhenNoMistakeImages_ShouldSkipRecord()
    {
        // Arrange: Record has only "homework" type images
        var record = CreateRecord(entries: new List<StudentProto.ImageEntry>
        {
            CreateImageEntry("uploads/student-1/hw1.jpg", "homework"),
            CreateImageEntry("uploads/student-1/hw2.jpg", "homework")
        });

        var pageResult = new StudentProto.UploadRecordsPageResult { TotalCount = 1, Page = 1, PageSize = 50 };
        pageResult.Items.Add(record);
        SetupStudentClientResponse(pageResult);

        var worker = CreateWorker();

        // Act
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert
        _mockDomainService.Verify(x => x.SubmitMistakeUploadAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        _mockStudentClient.Verify(x => x.RemoveProcessedImagesAsync(
            It.IsAny<StudentProto.RemoveProcessedImagesRequest>(),
            null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region PollAndProcessAsync - VL Not Configured

    [Fact]
    public async Task PollAndProcessAsync_WhenVlNotConfigured_ShouldTreatAllImagesAsSingleGroup()
    {
        // Arrange: VL not configured (empty ApiKey)
        var record = CreateRecord(entries: new List<StudentProto.ImageEntry>
        {
            CreateImageEntry("uploads/student-1/img1.jpg"),
            CreateImageEntry("uploads/student-1/img2.jpg"),
            CreateImageEntry("uploads/student-1/img3.jpg")
        });

        var pageResult = new StudentProto.UploadRecordsPageResult { TotalCount = 1, Page = 1, PageSize = 50 };
        pageResult.Items.Add(record);
        SetupStudentClientResponse(pageResult);

        var mistakeItem = CreateMistakeItem(subject: 1, grade: 7);
        SetupDomainServiceSubmit(new List<MistakeItemModel> { mistakeItem });

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var removeResponse = new StudentProto.RemoveProcessedImagesResponse { Success = true, RecordDeleted = true, RemainingImageCount = 0 };
        _mockStudentClient
            .Setup(x => x.RemoveProcessedImagesAsync(It.IsAny<StudentProto.RemoveProcessedImagesRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncCall(removeResponse));

        var notConfiguredOptions = new VlAnalyzerOptions { ApiKey = "", Model = "test-model" };
        var worker = CreateWorker(notConfiguredOptions);

        // Act
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert: All 3 images treated as single group with default subject=1, grade=7
        _mockDomainService.Verify(x => x.SubmitMistakeUploadAsync(
            "student-1", 1, 7,
            It.Is<List<string>>(p => p.Count == 3),
            It.IsAny<string>(),
            "record-1"), Times.Once);

        // VL analyzer should NOT be called
        _mockVlAnalyzer.Verify(x => x.AnalyzeAsync(
            It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region PollAndProcessAsync - VL Returns No Groups

    [Fact]
    public async Task PollAndProcessAsync_WhenVlReturnsNoGroups_ShouldFallbackToSingleGroup()
    {
        // Arrange
        var record = CreateRecord(entries: new List<StudentProto.ImageEntry>
        {
            CreateImageEntry("uploads/student-1/img1.jpg"),
            CreateImageEntry("uploads/student-1/img2.jpg")
        });

        var pageResult = new StudentProto.UploadRecordsPageResult { TotalCount = 1, Page = 1, PageSize = 50 };
        pageResult.Items.Add(record);
        SetupStudentClientResponse(pageResult);

        // VL returns empty groups
        SetupVlAnalyzerResponse(new VlAnalysisResult { Skipped = true });

        var mistakeItem = CreateMistakeItem(subject: 1, grade: 7);
        SetupDomainServiceSubmit(new List<MistakeItemModel> { mistakeItem });

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var removeResponse = new StudentProto.RemoveProcessedImagesResponse { Success = true, RecordDeleted = true, RemainingImageCount = 0 };
        _mockStudentClient
            .Setup(x => x.RemoveProcessedImagesAsync(It.IsAny<StudentProto.RemoveProcessedImagesRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncCall(removeResponse));

        var worker = CreateWorker();

        // Act
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert: Fallback to single group with default subject=1, grade=7
        _mockDomainService.Verify(x => x.SubmitMistakeUploadAsync(
            "student-1", 1, 7,
            It.Is<List<string>>(p => p.Count == 2),
            It.IsAny<string>(),
            "record-1"), Times.Once);
    }

    #endregion

    #region PollAndProcessAsync - Student Client Throws

    [Fact]
    public async Task PollAndProcessAsync_WhenStudentClientThrows_ShouldLogErrorAndReturn()
    {
        // Arrange
        _mockStudentClient
            .Setup(x => x.GetProcessingUploadRecordsAsync(
                It.IsAny<StudentProto.GetProcessingUploadRecordsRequest>(),
                null, null, It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("gRPC connection failed"));

        var worker = CreateWorker();

        // Act - should not throw
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert
        _mockDomainService.Verify(x => x.SubmitMistakeUploadAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region PollAndProcessAsync - OSS Migration Fails

    [Fact]
    public async Task PollAndProcessAsync_WhenOssCopyFailsForOneImage_ShouldStillProcessOthers()
    {
        // Arrange
        var record = CreateRecord(entries: new List<StudentProto.ImageEntry>
        {
            CreateImageEntry("uploads/student-1/img1.jpg"),
            CreateImageEntry("uploads/student-1/img2.jpg"),
            CreateImageEntry("uploads/student-1/img3.jpg")
        });

        var pageResult = new StudentProto.UploadRecordsPageResult { TotalCount = 1, Page = 1, PageSize = 50 };
        pageResult.Items.Add(record);
        SetupStudentClientResponse(pageResult);

        SetupVlAnalyzerResponse(new VlAnalysisResult
        {
            Groups = new List<VlImageGroup>
            {
                new()
                {
                    ImageIndices = new List<int> { 0, 1, 2 },
                    Subject = 1,
                    Grade = 7,
                    RootCause = "Single group"
                }
            }
        });

        var mistakeItem = CreateMistakeItem();
        mistakeItem.SourceRegions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "uploads/student-1/img1.jpg" },
            new() { SourceImagePath = "uploads/student-1/img2.jpg" },
            new() { SourceImagePath = "uploads/student-1/img3.jpg" }
        };
        SetupDomainServiceSubmit(new List<MistakeItemModel> { mistakeItem });

        // img2 copy fails, others succeed
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img1.jpg", It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img2.jpg", It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("OSS copy failed"));
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img3.jpg", It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var removeResponse = new StudentProto.RemoveProcessedImagesResponse { Success = true, RecordDeleted = false, RemainingImageCount = 1 };
        _mockStudentClient
            .Setup(x => x.RemoveProcessedImagesAsync(It.IsAny<StudentProto.RemoveProcessedImagesRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncCall(removeResponse));

        var worker = CreateWorker();

        // Act
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert: img1 and img3 migrated, img2 failed
        _mockOssService.Verify(x => x.CopyObjectAsync("uploads/student-1/img1.jpg", "mistakes/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.CopyObjectAsync("uploads/student-1/img3.jpg", "mistakes/student-1/img3.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("uploads/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("uploads/student-1/img3.jpg"), Times.Once);

        // RemoveProcessedImages still called with successful paths
        _mockStudentClient.Verify(x => x.RemoveProcessedImagesAsync(
            It.Is<StudentProto.RemoveProcessedImagesRequest>(r => r.RecordId == "record-1"),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ConvertUploadPathToMistakePath

    [Theory]
    [InlineData("uploads/student1/img.jpg", "mistakes/student1/img.jpg")]
    [InlineData("other/path.jpg", "mistakes/other/path.jpg")]
    [InlineData("uploads/abc/def/test.png", "mistakes/abc/def/test.png")]
    public void ConvertUploadPathToMistakePath_ShouldConvertCorrectly(string input, string expected)
    {
        // Use reflection to test the private static method
        var method = typeof(UploadPollingWorker).GetMethod(
            "ConvertUploadPathToMistakePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull("ConvertUploadPathToMistakePath should exist");

        var result = method!.Invoke(null, new object[] { input });

        result.Should().Be(expected);
    }

    #endregion

    #region Pagination

    [Fact]
    public async Task PollAndProcessAsync_WhenMorePagesExist_ShouldFetchNextPage()
    {
        // Arrange: First page returns 50 records with TotalCount=100
        var firstPageRecords = new StudentProto.UploadRecordsPageResult
        {
            TotalCount = 100,
            Page = 1,
            PageSize = 50
        };
        for (int i = 0; i < 50; i++)
        {
            firstPageRecords.Items.Add(CreateRecord(
                recordId: $"record-{i}",
                entries: new List<StudentProto.ImageEntry>
                {
                    CreateImageEntry($"uploads/student-1/img_{i}.jpg")
                }));
        }

        var secondPageRecords = new StudentProto.UploadRecordsPageResult
        {
            TotalCount = 100,
            Page = 2,
            PageSize = 50
        };

        var callCount = 0;
        _mockStudentClient
            .Setup(x => x.GetProcessingUploadRecordsAsync(
                It.IsAny<StudentProto.GetProcessingUploadRecordsRequest>(),
                null, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return CreateAsyncCall(callCount == 1 ? firstPageRecords : secondPageRecords);
            });

        SetupVlAnalyzerResponse(new VlAnalysisResult { Skipped = true });

        var mistakeItem = CreateMistakeItem();
        mistakeItem.SourceRegions = new List<SourceRegionModel> { new() { SourceImagePath = "test.jpg" } };
        SetupDomainServiceSubmit(new List<MistakeItemModel> { mistakeItem });

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var removeResponse = new StudentProto.RemoveProcessedImagesResponse { Success = true, RecordDeleted = true, RemainingImageCount = 0 };
        _mockStudentClient
            .Setup(x => x.RemoveProcessedImagesAsync(It.IsAny<StudentProto.RemoveProcessedImagesRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncCall(removeResponse));

        var worker = CreateWorker();

        // Act
        await InvokePollAndProcessAsync(worker, CancellationToken.None);

        // Assert: Second page should have been fetched
        _mockStudentClient.Verify(x => x.GetProcessingUploadRecordsAsync(
            It.Is<StudentProto.GetProcessingUploadRecordsRequest>(r => r.Page == 2 && r.PageSize == 50),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Helper to invoke private PollAndProcessAsync

    /// <summary>
    /// Invokes the private PollAndProcessAsync method via reflection.
    /// </summary>
    private static async Task InvokePollAndProcessAsync(UploadPollingWorker worker, CancellationToken ct)
    {
        var method = typeof(UploadPollingWorker).GetMethod(
            "PollAndProcessAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull("PollAndProcessAsync should exist");

        var task = (Task)method!.Invoke(worker, new object[] { ct })!;
        await task;
    }

    #endregion

    #region Helper: MockServiceScopeFactory

    private class MockServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public MockServiceScopeFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IServiceScope CreateScope()
        {
            return new MockServiceScope(_serviceProvider);
        }
    }

    private class MockServiceScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; }

        public MockServiceScope(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
        }

        public void Dispose() { }
    }

    #endregion
}
