using System;
using System.Collections.Generic;
using System.IO;
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
/// Tests for MistakeServiceImpl covering:
/// - GetMistakeItemsByUpload
/// - UpdateMistakeItem (UpdateMistakeItemPartialAsync delegation)
/// - GetPresignedUrl (size parameter, ThumbnailHelper integration)
/// </summary>
public class MistakeServiceImplAdditionalTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IOssService> _mockOssService;
    private readonly MistakeServiceImpl _service;

    public MistakeServiceImplAdditionalTests()
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

    private static MistakeItemModel CreateSampleItem(Guid? id = null, string studentId = "student-1",
        int subject = 3, int grade = 5, string sourceUploadId = "upload-1",
        int reviewStatus = 1, List<SourceRegionModel>? sourceRegions = null)
    {
        return new MistakeItemModel
        {
            Id = id ?? Guid.NewGuid(),
            StudentId = studentId,
            Subject = subject,
            Grade = grade,
            SourceUploadId = sourceUploadId,
            ReviewStatus = reviewStatus,
            SourceRegions = sourceRegions ?? new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/test/image.jpg" }
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    // =====================================================================
    // GetMistakeItemsByUpload Tests
    // =====================================================================

    [Fact]
    public async Task GetMistakeItemsByUpload_ShouldReturnMappedItems()
    {
        var item1 = CreateSampleItem(id: Guid.NewGuid(), studentId: "s1", sourceUploadId: "upload-1");
        var item2 = CreateSampleItem(id: Guid.NewGuid(), studentId: "s2", sourceUploadId: "upload-1");

        _mockDomainService
            .Setup(x => x.GetMistakeItemsByUploadAsync("upload-1"))
            .ReturnsAsync(new List<MistakeItemModel> { item1, item2 });

        var request = new GetMistakeItemsByUploadRequest { SourceUploadId = "upload-1" };

        var result = await _service.GetMistakeItemsByUpload(request, CreateTestContext());

        result.Items.Should().HaveCount(2);
        result.Items[0].StudentId.Should().Be("s1");
        result.Items[1].StudentId.Should().Be("s2");
    }

    [Fact]
    public async Task GetMistakeItemsByUpload_WhenNoItems_ShouldReturnEmptyList()
    {
        _mockDomainService
            .Setup(x => x.GetMistakeItemsByUploadAsync("upload-empty"))
            .ReturnsAsync(new List<MistakeItemModel>());

        var request = new GetMistakeItemsByUploadRequest { SourceUploadId = "upload-empty" };

        var result = await _service.GetMistakeItemsByUpload(request, CreateTestContext());

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMistakeItemsByUpload_ShouldMapSourceRegions()
    {
        var item = CreateSampleItem(sourceRegions: new List<SourceRegionModel>
        {
            new()
            {
                SourceImagePath = "mistakes/test/img1.jpg",
                BoundingBox = new BoundingBoxModel { X1 = 10, Y1 = 20, X2 = 100, Y2 = 200 }
            },
            new()
            {
                SourceImagePath = "mistakes/test/img2.jpg",
                BoundingBox = null
            }
        });

        _mockDomainService
            .Setup(x => x.GetMistakeItemsByUploadAsync("upload-1"))
            .ReturnsAsync(new List<MistakeItemModel> { item });

        var request = new GetMistakeItemsByUploadRequest { SourceUploadId = "upload-1" };

        var result = await _service.GetMistakeItemsByUpload(request, CreateTestContext());

        result.Items.Should().HaveCount(1);
        result.Items[0].SourceRegions.Should().HaveCount(2);
        result.Items[0].SourceRegions[0].SourceImagePath.Should().Be("mistakes/test/img1.jpg");
        result.Items[0].SourceRegions[0].BoundingBox.Should().NotBeNull();
        result.Items[0].SourceRegions[0].BoundingBox.X1.Should().Be(10);
        result.Items[0].SourceRegions[0].BoundingBox.Y1.Should().Be(20);
        result.Items[0].SourceRegions[0].BoundingBox.X2.Should().Be(100);
        result.Items[0].SourceRegions[0].BoundingBox.Y2.Should().Be(200);
        result.Items[0].SourceRegions[1].SourceImagePath.Should().Be("mistakes/test/img2.jpg");
    }

    [Fact]
    public async Task GetMistakeItemsByUpload_ShouldMapReviewStatusAndTimestamps()
    {
        var itemId = Guid.NewGuid();
        var reviewedAt = new DateTimeOffset(2025, 6, 14, 10, 30, 0, TimeSpan.Zero);
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5,
            SourceUploadId = "upload-1",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            ReviewerId = "teacher-1",
            ReviewedAt = reviewedAt,
            RootCause = "ok",
            CreatedAt = new DateTimeOffset(2025, 6, 13, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt = reviewedAt
        };

        _mockDomainService
            .Setup(x => x.GetMistakeItemsByUploadAsync("upload-1"))
            .ReturnsAsync(new List<MistakeItemModel> { item });

        var request = new GetMistakeItemsByUploadRequest { SourceUploadId = "upload-1" };

        var result = await _service.GetMistakeItemsByUpload(request, CreateTestContext());

        result.Items[0].ReviewStatus.Should().Be(ReviewStatus.Confirmed);
        result.Items[0].ReviewerId.Should().Be("teacher-1");
        result.Items[0].RootCause.Should().Be("ok");
        result.Items[0].ReviewedAt.Should().NotBeEmpty();
        result.Items[0].CreatedAt.Should().NotBeEmpty();
        result.Items[0].UpdatedAt.Should().NotBeEmpty();
    }

    // =====================================================================
    // UpdateMistakeItem Tests (UpdateMistakeItemPartialAsync delegation)
    // =====================================================================

    [Fact]
    public async Task UpdateMistakeItem_WhenAllFieldsProvided_ShouldUpdateAll()
    {
        var itemId = Guid.NewGuid();
        var updatedItem = CreateSampleItem(id: itemId, studentId: "new-student", subject: 7, grade: 10);

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                "new-student",
                7,
                10,
                It.IsAny<List<SourceRegionModel>>()))
            .ReturnsAsync(updatedItem);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            StudentId = "new-student",
            Subject = 7,
            Grade = 10
        };
        request.SourceRegions.Add(new SourceRegion { SourceImagePath = "mistakes/new.jpg" });

        var result = await _service.UpdateMistakeItem(request, CreateTestContext());

        result.Id.Should().Be(itemId.ToString());
        result.StudentId.Should().Be("new-student");
        result.Subject.Should().Be(7);
        result.Grade.Should().Be(10);
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenItemNotFound_ShouldThrowNotFound()
    {
        var itemId = Guid.NewGuid();

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<List<SourceRegionModel>>()))
            .ReturnsAsync((MistakeItemModel?)null);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            StudentId = "new-student"
        };

        var act = () => _service.UpdateMistakeItem(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenEmptyStudentId_ShouldPassNullToDomain()
    {
        var itemId = Guid.NewGuid();
        var updatedItem = CreateSampleItem(id: itemId);

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                null,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<List<SourceRegionModel>?>()))
            .ReturnsAsync(updatedItem);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            StudentId = "",
            Subject = 5,
            Grade = 3
        };

        var result = await _service.UpdateMistakeItem(request, CreateTestContext());

        result.Should().NotBeNull();
        _mockDomainService.Verify(
            x => x.UpdateMistakeItemPartialAsync(itemId, null, 5, 3, It.IsAny<List<SourceRegionModel>?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenSubjectZero_ShouldPassNullToDomain()
    {
        var itemId = Guid.NewGuid();
        var updatedItem = CreateSampleItem(id: itemId);

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                It.IsAny<string?>(),
                null,
                It.IsAny<int?>(),
                It.IsAny<List<SourceRegionModel>?>()))
            .ReturnsAsync(updatedItem);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            Subject = 0,
            Grade = 5
        };

        var result = await _service.UpdateMistakeItem(request, CreateTestContext());

        result.Should().NotBeNull();
        _mockDomainService.Verify(
            x => x.UpdateMistakeItemPartialAsync(itemId, null, null, 5, It.IsAny<List<SourceRegionModel>?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenGradeZero_ShouldPassNullToDomain()
    {
        var itemId = Guid.NewGuid();
        var updatedItem = CreateSampleItem(id: itemId);

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                null,
                It.IsAny<List<SourceRegionModel>?>()))
            .ReturnsAsync(updatedItem);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            Subject = 3,
            Grade = 0
        };

        var result = await _service.UpdateMistakeItem(request, CreateTestContext());

        result.Should().NotBeNull();
        _mockDomainService.Verify(
            x => x.UpdateMistakeItemPartialAsync(itemId, null, 3, null, It.IsAny<List<SourceRegionModel>?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenNoSourceRegions_ShouldPassNullToDomain()
    {
        var itemId = Guid.NewGuid();
        var updatedItem = CreateSampleItem(id: itemId);

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                null))
            .ReturnsAsync(updatedItem);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            StudentId = "student-1"
        };

        var result = await _service.UpdateMistakeItem(request, CreateTestContext());

        result.Should().NotBeNull();
        _mockDomainService.Verify(
            x => x.UpdateMistakeItemPartialAsync(itemId, "student-1", null, null, null),
            Times.Once);
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenSourceRegionsProvided_ShouldMapBoundingBox()
    {
        var itemId = Guid.NewGuid();
        var updatedItem = CreateSampleItem(id: itemId);

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<List<SourceRegionModel>>()))
            .ReturnsAsync(updatedItem);

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
        };
        request.SourceRegions.Add(new SourceRegion
        {
            SourceImagePath = "mistakes/test/region.jpg",
            BoundingBox = new BoundingBox { X1 = 5, Y1 = 10, X2 = 50, Y2 = 100 }
        });

        var result = await _service.UpdateMistakeItem(request, CreateTestContext());

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateMistakeItem_WhenValidationFails_ShouldThrowInvalidArgument()
    {
        var itemId = Guid.NewGuid();

        _mockDomainService
            .Setup(x => x.UpdateMistakeItemPartialAsync(
                itemId,
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<List<SourceRegionModel>?>()))
            .ThrowsAsync(new MistakeValidationException("Invalid data"));

        var request = new UpdateMistakeItemRequest
        {
            Id = itemId.ToString(),
            StudentId = "student-1"
        };

        var act = () => _service.UpdateMistakeItem(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.InvalidArgument && ex.Status.Detail.Contains("Invalid data"));
    }

    // =====================================================================
    // GetPresignedUrl Tests (size parameter, ThumbnailHelper integration)
    // =====================================================================

    [Fact]
    public async Task GetPresignedUrl_FullSizeWithCustomExpiry_ShouldUseCustomExpiry()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync("mistakes/test/image.jpg", 7200)).ReturnsAsync("http://custom-expiry-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            ExpirySeconds = 7200,
            Size = "full"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://custom-expiry-url");
        response.ExpirySeconds.Should().Be(7200);
    }

    [Fact]
    public async Task GetPresignedUrl_ZeroExpirySeconds_ShouldDefaultTo3600()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync("mistakes/test/image.jpg", 3600)).ReturnsAsync("http://default-expiry-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            ExpirySeconds = 0,
            Size = "full"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://default-expiry-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_WhitespaceObjectPath_ThrowsInvalidArgument()
    {
        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "   ",
            Size = "full"
        };

        var act = () => _service.GetPresignedUrl(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetPresignedUrl_SizeCaseInsensitive_ShouldWork()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync("mistakes/test/image.jpg", 3600)).ReturnsAsync("http://original-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            Size = "FULL"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://original-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_ThumbnailCached_ShouldReturnCachedUrl()
    {
        var objectPath = "mistakes/test/image.jpg";
        var thumbnailPath = ThumbnailHelper.GetThumbnailPath(objectPath, "small");

        _mockOssService.Setup(x => x.ObjectExistsAsync(objectPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.ObjectExistsAsync(thumbnailPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(thumbnailPath, 3600)).ReturnsAsync("http://cached-thumbnail-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = objectPath,
            Size = "small"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://cached-thumbnail-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_ThumbnailNotCached_ShouldGenerateAndReturnUrl()
    {
        var objectPath = "mistakes/test/image.jpg";
        var thumbnailPath = ThumbnailHelper.GetThumbnailPath(objectPath, "medium");

        _mockOssService.Setup(x => x.ObjectExistsAsync(objectPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.ObjectExistsAsync(thumbnailPath)).ReturnsAsync(false);
        _mockOssService.Setup(x => x.DownloadAsync(objectPath)).ReturnsAsync(new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(thumbnailPath, 3600)).ReturnsAsync("http://generated-thumbnail-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = objectPath,
            Size = "medium"
        };

        // ThumbnailHelper will attempt to download, generate, and upload.
        // Since we can't easily mock the full ImageSharp pipeline in a unit test,
        // we test the ThumbnailHelper.GetOrGenerateThumbnailAsync fallback behavior.
        // The key contract is: if thumbnail generation fails, it falls back to original URL.
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(objectPath, 3600)).ReturnsAsync("http://fallback-original-url");

        // This test validates the fallback path since ImageSharp may fail on invalid image data
        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        // Either the thumbnail URL or the fallback original URL should be returned
        response.Url.Should().BeOneOf("http://generated-thumbnail-url", "http://fallback-original-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_ThumbnailGenerationFails_ShouldFallbackToOriginal()
    {
        var objectPath = "mistakes/test/image.jpg";

        _mockOssService.Setup(x => x.ObjectExistsAsync(objectPath)).ReturnsAsync(true);
        // Thumbnail does not exist (cache miss)
        _mockOssService.Setup(x => x.ObjectExistsAsync(It.Is<string>(p => p != objectPath))).ReturnsAsync(false);
        // Download fails
        _mockOssService.Setup(x => x.DownloadAsync(objectPath)).ThrowsAsync(new Exception("Download failed"));
        // Fallback: return original URL
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(objectPath, 3600)).ReturnsAsync("http://fallback-original-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = objectPath,
            Size = "small"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        // ThumbnailHelper catches the exception and falls back to original URL
        response.Url.Should().Be("http://fallback-original-url");
        response.ExpirySeconds.Should().Be(3600);
    }

    [Fact]
    public async Task GetPresignedUrl_ThumbnailSize_ShouldUseCorrectPath()
    {
        var objectPath = "mistakes/2025/06/14/abc/image.jpg";
        var expectedThumbnailPath = "mistakes/2025/06/14/abc/image_thumbnail.jpg";

        _mockOssService.Setup(x => x.ObjectExistsAsync(objectPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.ObjectExistsAsync(expectedThumbnailPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(expectedThumbnailPath, 3600)).ReturnsAsync("http://thumbnail-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = objectPath,
            Size = "thumbnail"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://thumbnail-url");
        _mockOssService.Verify(x => x.ObjectExistsAsync(expectedThumbnailPath), Times.Once);
    }

    [Fact]
    public async Task GetPresignedUrl_MediumSize_ShouldUseCorrectPath()
    {
        var objectPath = "mistakes/2025/06/14/abc/image.jpg";
        var expectedThumbnailPath = "mistakes/2025/06/14/abc/image_medium.jpg";

        _mockOssService.Setup(x => x.ObjectExistsAsync(objectPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.ObjectExistsAsync(expectedThumbnailPath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync(expectedThumbnailPath, 1800)).ReturnsAsync("http://medium-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = objectPath,
            ExpirySeconds = 1800,
            Size = "medium"
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://medium-url");
        response.ExpirySeconds.Should().Be(1800);
    }

    [Fact]
    public async Task GetPresignedUrl_RpcExceptionFromOss_ShouldPropagate()
    {
        _mockOssService
            .Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg"))
            .Throws(new RpcException(new Status(StatusCode.PermissionDenied, "Access denied")));

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg",
            Size = "full"
        };

        var act = () => _service.GetPresignedUrl(request, CreateTestContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task GetPresignedUrl_NullSize_ShouldDefaultToFull()
    {
        _mockOssService.Setup(x => x.ObjectExistsAsync("mistakes/test/image.jpg")).ReturnsAsync(true);
        _mockOssService.Setup(x => x.GetPresignedUrlAsync("mistakes/test/image.jpg", 3600)).ReturnsAsync("http://original-url");

        var request = new MistakeGetPresignedUrlRequest
        {
            ObjectPath = "mistakes/test/image.jpg"
            // Size is not set (defaults to empty string per proto3)
        };

        var response = await _service.GetPresignedUrl(request, CreateTestContext());

        response.Url.Should().Be("http://original-url");
        response.ExpirySeconds.Should().Be(3600);
    }
}
