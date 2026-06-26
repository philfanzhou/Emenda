using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Xunit;

using Ruoyu.Study.Student.Contract.Protos;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;
using MistakeGetPresignedUrlRequest = Ruoyu.Study.Mistake.Contract.Protos.GetPresignedUrlRequest;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for MistakeServiceImpl focusing on the refactored review flow
/// where OSS migration is handled by the polling worker, not during review.
/// </summary>
public class MistakeServiceImplOssMigrationTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<IReanalyzeJobService> _mockReanalyzeJobService;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IOssService> _mockOssService;
    private readonly MistakeServiceImpl _service;

    public MistakeServiceImplOssMigrationTests()
    {
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockReanalyzeJobService = new Mock<IReanalyzeJobService>();
        _mockLogger = new Mock<ILogger<MistakeServiceImpl>>();
        _mockStudentClient = new Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
        _mockOssService = new Mock<IOssService>();
        var mockVlAnalyzer = new Mock<Ruoyu.Study.Mistake.Service.Services.IVlAnalyzer>();

        _service = new MistakeServiceImpl(
            _mockDomainService.Object,
            _mockReanalyzeJobService.Object,
            _mockStudentClient.Object,
            _mockOssService.Object,
            mockVlAnalyzer.Object,
            _mockLogger.Object);
    }

    private static ServerCallContext CreateTestContext()
    {
        return TestServerCallContext.Create(
            "TestMethod",
            "localhost",
            DateTime.UtcNow.AddHours(1),
            new Metadata(),
            CancellationToken.None,
            "peer",
            null,
            null,
            _ => Task.CompletedTask,
            () => new WriteOptions(),
            _ => { });
    }

    private void SetupCreateReturnedRecordAsync()
    {
        _mockStudentClient
            .Setup(x => x.CreateReturnedRecordAsync(
                It.IsAny<StudentProto.CreateReturnedRecordRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<StudentProto.UploadImagesResponse>(
                Task.FromResult(new StudentProto.UploadImagesResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));
    }

    // === ReviewMistakeItem Confirm Tests ===

    [Fact]
    public async Task ReviewMistakeItem_WhenConfirmed_ShouldSucceedWithoutMigration()
    {
        // After refactoring, confirm no longer triggers OSS migration or Student calls.
        // Images are already in mistakes/ from the polling worker.
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", "ok", null, null))
            .ReturnsAsync(item);

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1",
            RootCause = "ok"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // No OSS migration should happen
        _mockOssService.Verify(x => x.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        // No Student gRPC calls should happen for confirm
        _mockStudentClient.Verify(x => x.RemoveImagesFromRecordAsync(
            It.IsAny<StudentProto.RemoveImagesFromRecordRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenConfirmed_WithUploadPaths_ShouldStillSucceed()
    {
        // Even if paths are still at uploads/ (edge case), confirm should succeed
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", It.IsAny<string>(), null, null))
            .ReturnsAsync(item);

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // No OSS migration, no Student calls
        _mockOssService.Verify(x => x.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // === ReviewMistakeItem Reject Tests ===

    [Fact]
    public async Task ReviewMistakeItem_WhenRejected_ShouldMigrateBackAndCreateReturnedRecord()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Rejected,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "bad", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId))
            .ReturnsAsync(true);
        SetupCreateReturnedRecordAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Rejected,
            ReviewerId = "teacher-1",
            RootCause = "bad"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Should migrate image back: mistakes/ -> uploads/
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "mistakes/student-1/img1.jpg", "uploads/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("mistakes/student-1/img1.jpg"), Times.Once);
        // Should delete MistakeItem
        _mockDomainService.Verify(x => x.DeleteMistakeItemAsync(itemId), Times.Once);
        // Should create returned record in Student
        _mockStudentClient.Verify(x => x.CreateReturnedRecordAsync(
            It.Is<StudentProto.CreateReturnedRecordRequest>(r =>
                r.StudentId == "student-1" &&
                r.ImageEntries.Count == 1 &&
                r.ImageEntries[0].Path == "uploads/student-1/img1.jpg" &&
                r.ImageEntries[0].Type == "mistake"),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenRejected_MultipleImages_ShouldMigrateAllBack()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Rejected,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/student-1/img1.jpg" },
                new() { SourceImagePath = "mistakes/student-1/img2.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "bad", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId))
            .ReturnsAsync(true);
        SetupCreateReturnedRecordAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Rejected,
            ReviewerId = "teacher-1",
            RootCause = "bad"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Both images should be migrated back
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "mistakes/student-1/img1.jpg", "uploads/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "mistakes/student-1/img2.jpg", "uploads/student-1/img2.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("mistakes/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("mistakes/student-1/img2.jpg"), Times.Once);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenRejected_OssMigrationFails_ShouldStillDeleteAndCreateRecord()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Rejected,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "bad", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId))
            .ReturnsAsync(true);
        // OSS copy fails
        _mockOssService.Setup(x => x.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("OSS error"));
        SetupCreateReturnedRecordAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Rejected,
            ReviewerId = "teacher-1",
            RootCause = "bad"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        // Should still succeed even if OSS migration fails
        result.Success.Should().BeTrue();
        // MistakeItem should still be deleted
        _mockDomainService.Verify(x => x.DeleteMistakeItemAsync(itemId), Times.Once);
        // No returned record should be created since migration failed
        _mockStudentClient.Verify(x => x.CreateReturnedRecordAsync(
            It.IsAny<StudentProto.CreateReturnedRecordRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenRejected_WithUploadPaths_ShouldNotMigrateBack()
    {
        // If paths are already at uploads/, no migration back needed
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Rejected,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "bad", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId))
            .ReturnsAsync(true);
        SetupCreateReturnedRecordAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Rejected,
            ReviewerId = "teacher-1",
            RootCause = "bad"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // No OSS copy for uploads/ paths
        _mockOssService.Verify(x => x.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        // MistakeItem should still be deleted
        _mockDomainService.Verify(x => x.DeleteMistakeItemAsync(itemId), Times.Once);
    }

    // === CompleteUploadReview Tests (Legacy) ===

    [Fact]
    public async Task CompleteUploadReview_ShouldMigrateOnlyConfirmedItems()
    {
        var confirmedItem = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        var rejectedItem = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Rejected,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img2.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.CompleteUploadReviewAsync("upload-1", "teacher-1"))
            .ReturnsAsync(new CompleteUploadReviewResult
            {
                Success = true,
                SourceUploadId = "upload-1",
                StudentId = "student-1",
                Items = new List<MistakeItemModel> { confirmedItem, rejectedItem }
            });
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Only confirmed item's path should be migrated via OSS
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "uploads/student-1/img1.jpg", "mistakes/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("uploads/student-1/img1.jpg"), Times.Once);
        // Rejected item should NOT be migrated
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "uploads/student-1/img2.jpg", It.IsAny<string>()), Times.Never);
        confirmedItem.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
        rejectedItem.SourceRegions[0].SourceImagePath.Should().Be("uploads/student-1/img2.jpg");
    }

    [Fact]
    public async Task CompleteUploadReview_WhenOssFails_ShouldNotBlockFlow()
    {
        var item = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" },
                new() { SourceImagePath = "uploads/student-1/img2.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.CompleteUploadReviewAsync("upload-1", "teacher-1"))
            .ReturnsAsync(new CompleteUploadReviewResult
            {
                Success = true,
                SourceUploadId = "upload-1",
                StudentId = "student-1",
                Items = new List<MistakeItemModel> { item }
            });
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);

        // First image fails, second succeeds
        _mockOssService.SetupSequence(x => x.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("OSS error"))
            .Returns(Task.CompletedTask);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Failed image path unchanged
        item.SourceRegions[0].SourceImagePath.Should().Be("uploads/student-1/img1.jpg");
        // Successful image path updated
        item.SourceRegions[1].SourceImagePath.Should().Be("mistakes/student-1/img2.jpg");
    }

    [Fact]
    public async Task CompleteUploadReview_WhenPendingItemsExist_ShouldReturnError()
    {
        _mockDomainService.Setup(x => x.CompleteUploadReviewAsync("upload-1", "teacher-1"))
            .ReturnsAsync(new CompleteUploadReviewResult
            {
                Success = false,
                ErrorMessage = "Still has 2 pending review items"
            });

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pending");
        _mockOssService.Verify(x => x.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // === GetPresignedUrl Tests ===

    [Fact]
    public async Task GetPresignedUrl_FullSize_ReturnsPresignedUrlFromOssService()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync("mistakes/test/image.jpg", 3600)).ReturnsAsync("http://original-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            Size = "full"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://original-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_EmptySize_DefaultsToFull()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync("mistakes/test/image.jpg", 3600)).ReturnsAsync("http://original-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            Size = ""
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://original-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_ThumbnailCached_ReturnsCachedThumbnailUrl()
    {
        var objectPath = "mistakes/test/image.jpg";
        var thumbnailPath = "mistakes/test/image_small.jpg";

        _mockOssService.Setup(x => x.ObjectExistsAsync(objectPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.ObjectExistsAsync(thumbnailPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(thumbnailPath, 3600)).ReturnsAsync("http://thumbnail-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = objectPath,
            Size = "small"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://thumbnail-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_ObjectNotFound_ThrowsNotFound()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/nonexistent.jpg")).ReturnsAsync(false);

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/nonexistent.jpg",
            Size = "full"
        };

        var act = () => _service.GetPresignedUrl(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task GetPresignedUrl_OssServiceThrows_ThrowsInternalError()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ThrowsAsync(new Exception("OSS connection failed"));

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            Size = "full"
        };

        var act = () => _service.GetPresignedUrl(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.Internal);
    }

    [Fact]
    public async Task GetPresignedUrl_EmptyObjectPath_ThrowsInvalidArgument()
    {
        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "",
            Size = "full"
        };

        var act = () => _service.GetPresignedUrl(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.InvalidArgument);
    }
}
