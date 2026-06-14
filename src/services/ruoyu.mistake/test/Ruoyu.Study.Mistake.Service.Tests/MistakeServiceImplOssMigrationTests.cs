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
/// Tests for MistakeServiceImpl focusing on OSS image migration logic
/// in ReviewMistakeItem and CompleteUploadReview.
/// </summary>
public class MistakeServiceImplOssMigrationTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IOssService> _mockOssService;
    private readonly MistakeServiceImpl _service;

    public MistakeServiceImplOssMigrationTests()
    {
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockLogger = new Mock<ILogger<MistakeServiceImpl>>();
        _mockStudentClient = new Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
        _mockOssService = new Mock<IOssService>();

        _service = new MistakeServiceImpl(
            _mockDomainService.Object,
            _mockStudentClient.Object,
            _mockOssService.Object,
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

    private void SetupMigrateImagesToMistakeAsync(MigrateImagesToMistakeResponse response)
    {
        _mockStudentClient
            .Setup(x => x.MigrateImagesToMistakeAsync(
                It.IsAny<MigrateImagesToMistakeRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<MigrateImagesToMistakeResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));
    }

    private void SetupRemoveImagesFromRecordAsync()
    {
        _mockStudentClient
            .Setup(x => x.RemoveImagesFromRecordAsync(
                It.IsAny<RemoveImagesFromRecordRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<RemoveImagesFromRecordResponse>(
                Task.FromResult(new RemoveImagesFromRecordResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));
    }

    private void SetupMarkUploadRecordAsReturnedAsync()
    {
        _mockStudentClient
            .Setup(x => x.MarkUploadRecordAsReturnedAsync(
                It.IsAny<MarkUploadRecordAsReturnedRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<StudentProto.BoolResponse>(
                Task.FromResult(new StudentProto.BoolResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));
    }

    // === ReviewMistakeItem OSS Migration Tests ===

    [Fact]
    public async Task ReviewMistakeItem_WhenConfirmed_ShouldMigrateUploadsToMistakes()
    {
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
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", "ok", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);
        _mockDomainService.Setup(x => x.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<MistakeItemModel>());

        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "mistakes/student-1/img1.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);
        SetupRemoveImagesFromRecordAsync();
        SetupMarkUploadRecordAsReturnedAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1",
            ReviewComment = "ok"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        _mockStudentClient.Verify(x => x.MigrateImagesToMistakeAsync(
            It.IsAny<MigrateImagesToMistakeRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        item.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenRejected_ShouldNotMigrateImages()
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
                new() { SourceImagePath = "uploads/student-1/img1.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "bad", null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(It.IsAny<Guid>()))
            .ReturnsAsync(true);
        _mockDomainService.Setup(x => x.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<MistakeItemModel>());
        SetupMarkUploadRecordAsReturnedAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Rejected,
            ReviewerId = "teacher-1",
            ReviewComment = "bad"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        _mockStudentClient.Verify(x => x.MigrateImagesToMistakeAsync(
            It.IsAny<MigrateImagesToMistakeRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenMigrationFails_ShouldNotBlockFlow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            SourceUploadId = "upload-1",
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
        _mockDomainService.Setup(x => x.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<MistakeItemModel>());

        // First image migration fails, second succeeds
        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "",
            Success = false,
            ErrorMessage = "Migration failed"
        });
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img2.jpg",
            NewPath = "mistakes/student-1/img2.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);
        SetupRemoveImagesFromRecordAsync();
        SetupMarkUploadRecordAsReturnedAsync();

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
            StudentId = "student-1",
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/student-1/already.jpg" }
            }
        };

        _mockDomainService.Setup(x => x.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, It.IsAny<string>(), It.IsAny<string>(), null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<MistakeItemModel>());
        SetupRemoveImagesFromRecordAsync();
        SetupMarkUploadRecordAsReturnedAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        result.Success.Should().BeTrue();
        _mockStudentClient.Verify(x => x.MigrateImagesToMistakeAsync(
            It.IsAny<MigrateImagesToMistakeRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewMistakeItem_WhenMigrationSucceeds_ShouldUpdatePath()
    {
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
            itemId, (int)ReviewStatus.Confirmed, It.IsAny<string>(), It.IsAny<string>(), null, null))
            .ReturnsAsync(item);
        _mockDomainService.Setup(x => x.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(true);
        _mockDomainService.Setup(x => x.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<MistakeItemModel>());

        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "mistakes/student-1/img1.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);
        SetupRemoveImagesFromRecordAsync();
        SetupMarkUploadRecordAsReturnedAsync();

        var request = new ReviewMistakeItemRequest
        {
            Id = itemId.ToString(),
            ReviewStatus = ReviewStatus.Confirmed,
            ReviewerId = "teacher-1"
        };

        var result = await _service.ReviewMistakeItem(request, CreateTestContext());

        // Flow should succeed
        result.Success.Should().BeTrue();
        // Path is updated because migration succeeded
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

        // Only confirmed item's path should be migrated
        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "mistakes/student-1/img1.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // MigrateImagesToMistakeAsync should be called once with only confirmed paths
        _mockStudentClient.Verify(x => x.MigrateImagesToMistakeAsync(
            It.IsAny<MigrateImagesToMistakeRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        confirmedItem.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
        rejectedItem.SourceRegions[0].SourceImagePath.Should().Be("uploads/student-1/img2.jpg");
    }

    [Fact]
    public async Task CompleteUploadReview_WhenMigrationFails_ShouldNotBlockFlow()
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

        // First image migration fails, second succeeds
        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "",
            Success = false,
            ErrorMessage = "Migration error"
        });
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img2.jpg",
            NewPath = "mistakes/student-1/img2.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);

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

        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = sharedPath,
            NewPath = "mistakes/student-1/shared.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // MigrateImagesToMistakeAsync should only be called once (deduplication by pathsToMigrate list)
        _mockStudentClient.Verify(x => x.MigrateImagesToMistakeAsync(
            It.IsAny<MigrateImagesToMistakeRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
        _mockStudentClient.Verify(x => x.MigrateImagesToMistakeAsync(
            It.IsAny<MigrateImagesToMistakeRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteUploadReview_WhenAllMigrationFails_ShouldStillReturnSuccess()
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

        // All migrations fail
        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "",
            Success = false,
            ErrorMessage = "Migration completely down"
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);

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
    public async Task CompleteUploadReview_WhenMigrationSucceeds_ShouldUpdatePath()
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

        var migrateResponse = new MigrateImagesToMistakeResponse();
        migrateResponse.Results.Add(new MigrateImageResult
        {
            SourcePath = "uploads/student-1/img1.jpg",
            NewPath = "mistakes/student-1/img1.jpg",
            Success = true
        });
        SetupMigrateImagesToMistakeAsync(migrateResponse);

        var request = new CompleteUploadReviewRequest
        {
            SourceUploadId = "upload-1",
            ReviewerId = "teacher-1"
        };

        var result = await _service.CompleteUploadReview(request, CreateTestContext());

        result.Success.Should().BeTrue();
        // Path is updated because migration succeeded
        item.SourceRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
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
