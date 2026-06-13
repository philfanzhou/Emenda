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

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for MistakeServiceImpl focusing on OSS image migration logic
/// in ReviewMistakeItem and CompleteUploadReview.
/// </summary>
public class MistakeServiceImplOssMigrationTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<IOssService> _mockOssService;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly MistakeServiceImpl _service;

    public MistakeServiceImplOssMigrationTests()
    {
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockOssService = new Mock<IOssService>();
        _mockLogger = new Mock<ILogger<MistakeServiceImpl>>();
        _mockStudentClient = new Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();

        _service = new MistakeServiceImpl(
            _mockDomainService.Object,
            _mockOssService.Object,
            _mockStudentClient.Object,
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

    // === ReviewMistakeItem OSS Migration Tests ===

    [Fact]
    public async Task ReviewMistakeItem_WhenConfirmed_ShouldMigrateUploadsToMistakes()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", "ok", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1",
            ReviewComment = "ok"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "uploads/student-1/img1.jpg",
            "mistakes/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("uploads/student-1/img1.jpg"), Times.Once);
        item.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenRejected_ShouldNotMigrateImages()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Rejected,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "bad", null, null))
            .ReturnsAsync(item);

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Rejected,
            ReviewerId = "teacher-1",
            ReviewComment = "bad"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        _mockOssService.Verify(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockOssService.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenOssCopyFails_ShouldNotBlockFlow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" },
                new() { SourceImagePath = "uploads/student-1/img2.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, It.IsAny<string>(), It.IsAny<string>(), null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);

        // First image copy fails, second succeeds
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img1.jpg", It.IsAny<string>()))
            .ThrowsAsync(new Exception("OSS unavailable"));
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img2.jpg", It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Failed image path unchanged
        item.SourceRegions[0].SourceImagePath.Should().Be("uploads/student-1/img1.jpg");
        // Successful image path updated
        item.SourceRegions[1].SourceImagePath.Should().Be("mistakes/student-1/img2.jpg");
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenAlreadyMistakesPath_ShouldSkipMigration()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/student-1/already.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, It.IsAny<string>(), It.IsAny<string>(), null, null))
            .ReturnsAsync(item);

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        _mockOssService.Verify(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenOssDeleteFails_ShouldStillUpdatePath()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, It.IsAny<string>(), It.IsAny<string>(), null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        // Delete fails
        _mockOssService.Setup(x => x.DeleteAsync("uploads/student-1/img1.jpg"))
            .ThrowsAsync(new Exception("Delete failed"));

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        // Flow should still succeed
        result.Success.Should().BeTrue();
        // Path IS updated because Copy succeeded (Delete failure only logs warning)
        item.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
    }

    // === CompleteUploadReview OSS Migration Tests ===

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
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Only confirmed item should be migrated
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "uploads/student-1/img1.jpg",
            "mistakes/student-1/img1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.CopyObjectAsync(
            "uploads/student-1/img2.jpg",
            It.IsAny<string>()), Times.Never);
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

        // First image copy fails, second succeeds
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img1.jpg", It.IsAny<string>()))
            .ThrowsAsync(new Exception("OSS error"));
        _mockOssService.Setup(x => x.CopyObjectAsync("uploads/student-1/img2.jpg", It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        item.SourceRegions[0].SourceImagePath.Should().Be("uploads/student-1/img1.jpg");
        item.SourceRegions[1].SourceImagePath.Should().Be("mistakes/student-1/img2.jpg");
    }

    [Fact]
    public async Task CompleteUploadReview_ShouldDeduplicatePathsWithCache()
    {
        var sharedPath = "uploads/student-1/shared.jpg";
        var item1 = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = sharedPath }
            }
        };
        var item2 = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = sharedPath }
            }
        };

        _mockDomainService.Setup(x => x.CompleteUploadReviewAsync("upload-1", "teacher-1"))
            .ReturnsAsync(new CompleteUploadReviewResult
            {
                Success = true,
                SourceUploadId = "upload-1",
                StudentId = "student-1",
                Items = new List<MistakeItemModel> { item1, item2 }
            });
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // CopyObject should only be called once for the shared path
        _mockOssService.Verify(x => x.CopyObjectAsync(sharedPath, "mistakes/student-1/shared.jpg"), Times.Once);
        item1.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/shared.jpg");
        item2.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/shared.jpg");
    }

    [Fact]
    public async Task CompleteUploadReview_WhenPendingItemsExist_ShouldReturnError()
    {
        _mockDomainService.Setup(x => x.CompleteUploadReviewAsync("upload-1", "teacher-1"))
            .ReturnsAsync(new CompleteUploadReviewResult
            {
                Success = false,
                ErrorMessage = "还有 2 条错题待审核，请先完成所有审核"
            });

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("待审核");
        _mockOssService.Verify(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CompleteUploadReview_WhenAllOssFails_ShouldStillReturnSuccess()
    {
        var item = new MistakeItemModel
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

        _mockDomainService.Setup(x => x.CompleteUploadReviewAsync("upload-1", "teacher-1"))
            .ReturnsAsync(new CompleteUploadReviewResult
            {
                Success = true,
                SourceUploadId = "upload-1",
                StudentId = "student-1",
                Items = new List<MistakeItemModel> { item }
            });

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("OSS completely down"));

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        item.SourceRegions[0].SourceImagePath.Should().Be("uploads/student-1/img1.jpg");
    }

    [Fact]
    public async Task CompleteUploadReview_WhenOssDeleteFails_ShouldStillUpdatePath()
    {
        var item = new MistakeItemModel
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

        _mockOssService.Setup(x => x.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        // Delete fails
        _mockOssService.Setup(x => x.DeleteAsync("uploads/student-1/img1.jpg"))
            .ThrowsAsync(new Exception("Delete failed"));

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Path IS updated because Copy succeeded (Delete failure only logs warning)
        item.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
    }
}
