using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Mistake.Domain.Tests;

public class MistakeDomainServiceTests
{
    private readonly Mock<IMistakeItemRepository> _mockItemRepo;
    private readonly Mock<IMistakePracticeRepository> _mockPracticeRepo;
    private readonly Mock<IProcessLogRepository> _mockLogRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ILogger<MistakeDomainService>> _mockLogger;
    private readonly MistakeDomainService _service;

    public MistakeDomainServiceTests()
    {
        _mockItemRepo = new Mock<IMistakeItemRepository>();
        _mockPracticeRepo = new Mock<IMistakePracticeRepository>();
        _mockLogRepo = new Mock<IProcessLogRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<MistakeDomainService>>();

        _service = new MistakeDomainService(
            _mockItemRepo.Object,
            _mockPracticeRepo.Object,
            _mockLogRepo.Object,
            _mockUow.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_ShouldCreateItemWithSourceRegions()
    {
        var imagePaths = new List<string> { "img1.jpg", "img2.jpg" };

        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1")).ReturnsAsync(new List<MistakeItemModel>());

        var items = await _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, imagePaths, null, "upload-1");

        items.Should().HaveCount(1);
        items[0].SourceRegions.Should().HaveCount(2);
        items[0].SourceRegions[0].SourceImagePath.Should().Be("img1.jpg");
        items[0].SourceRegions[0].BoundingBox.Should().BeNull();
        items[0].SourceRegions[1].SourceImagePath.Should().Be("img2.jpg");
        items[0].ReviewStatus.Should().Be((int)ReviewStatus.PendingReview);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenAlreadyExists_ShouldCreateNewItem()
    {
        var existingItem = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            SourceUploadId = "upload-1",
            StudentId = "student-1"
        };

        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1")).ReturnsAsync(new List<MistakeItemModel> { existingItem });

        var items = await _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, new List<string> { "img.jpg" }, null, "upload-1");

        items.Should().HaveCount(1);
        items[0].Id.Should().NotBe(existingItem.Id);
        items[0].Id.Should().NotBe(Guid.Empty);
        items[0].SourceUploadId.Should().Be("upload-1");
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenPending_ShouldConfirm()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.PendingReview
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", "looks good");

        result.Should().NotBeNull();
        item.ReviewStatus.Should().Be((int)ReviewStatus.Confirmed);
        item.ReviewerId.Should().Be("teacher-1");
        item.ReviewComment.Should().Be("looks good");
        item.ReviewedAt.Should().NotBeNull();
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenAlreadyReviewed_ShouldThrow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var act = () => _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", null);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*只能审核待审核状态*");
    }

    [Fact]
    public async Task DeleteMistakeItemAsync_WhenConfirmed_ShouldDelete()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.DeleteAsync(itemId)).ReturnsAsync(true);

        var result = await _service.DeleteMistakeItemAsync(itemId);

        result.Should().BeTrue();
        _mockPracticeRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockLogRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockItemRepo.Verify(x => x.DeleteAsync(itemId), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeItemAsync_WhenPendingReview_ShouldDelete()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.PendingReview
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.DeleteAsync(itemId)).ReturnsAsync(true);

        var result = await _service.DeleteMistakeItemAsync(itemId);

        result.Should().BeTrue();
        _mockPracticeRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockLogRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockItemRepo.Verify(x => x.DeleteAsync(itemId), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task CompleteUploadReviewAsync_WhenPendingItemsExist_ShouldReturnError()
    {
        _mockItemRepo.Setup(x => x.CountPendingBySourceUploadIdAsync("upload-1")).ReturnsAsync(2);

        var result = await _service.CompleteUploadReviewAsync("upload-1", "teacher-1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("2 条错题待审核");
        result.SourceUploadId.Should().BeNull();
    }

    [Fact]
    public async Task CompleteUploadReviewAsync_WhenAllReviewed_ShouldReturnSuccessWithStudentId()
    {
        _mockItemRepo.Setup(x => x.CountPendingBySourceUploadIdAsync("upload-1")).ReturnsAsync(0);
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1"))
            .ReturnsAsync(new List<MistakeItemModel>
            {
                new() { Id = Guid.NewGuid(), SourceUploadId = "upload-1", StudentId = "student-1" }
            });

        var result = await _service.CompleteUploadReviewAsync("upload-1", "teacher-1");

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.SourceUploadId.Should().Be("upload-1");
        result.StudentId.Should().Be("student-1");
    }

    [Fact]
    public async Task AddMistakeItemAsync_WithSourceRegions_ShouldSucceed()
    {
        var item = new MistakeItemModel
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 3,
            SourceUploadId = "upload-1",
            SourceRegions = new List<SourceRegionModel>
            {
                new()
                {
                    SourceImagePath = "img1.jpg",
                    BoundingBox = new BoundingBoxModel { X1 = 10, Y1 = 20, X2 = 100, Y2 = 200 }
                },
                new()
                {
                    SourceImagePath = "img2.jpg",
                    BoundingBox = null
                }
            }
        };

        var result = await _service.AddMistakeItemAsync(item);

        result.Id.Should().NotBe(Guid.Empty);
        result.SourceRegions.Should().HaveCount(2);
        result.SourceRegions[0].BoundingBox.Should().NotBeNull();
        result.SourceRegions[1].BoundingBox.Should().BeNull();
        result.ReviewStatus.Should().Be((int)ReviewStatus.PendingReview);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task AddPracticeAsync_WhenItemNotConfirmed_ShouldThrow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.PendingReview
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var practice = new MistakePracticeModel { MistakeItemId = itemId };
        var act = () => _service.AddPracticeAsync(practice);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*只能对已确认的错题进行练习*");
    }

    [Fact]
    public async Task AddPracticeAsync_WhenItemConfirmed_ShouldAddPractice()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var practice = new MistakePracticeModel
        {
            MistakeItemId = itemId,
            StudentId = "student-1",
            IsCorrect = false
        };

        var result = await _service.AddPracticeAsync(practice);

        result.Should().NotBeNull();
        result.MistakeItemId.Should().Be(itemId);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenPending_ShouldReject()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.PendingReview
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "需要重新上传");

        result.Should().NotBeNull();
        item.ReviewStatus.Should().Be((int)ReviewStatus.Rejected);
        item.ReviewComment.Should().Be("需要重新上传");
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task CompleteUploadReviewAsync_WhenNoItems_ShouldReturnError()
    {
        _mockItemRepo.Setup(x => x.CountPendingBySourceUploadIdAsync("upload-1")).ReturnsAsync(0);
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1"))
            .ReturnsAsync(new List<MistakeItemModel>());

        var result = await _service.CompleteUploadReviewAsync("upload-1", "teacher-1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("未找到该上传记录的错题");
    }

    [Fact]
    public async Task GetAllReferencedImagePathsAsync_ShouldReturnAllPaths()
    {
        var items = new List<MistakeItemModel>
        {
            new()
            {
                SourceRegions = new List<SourceRegionModel>
                {
                    new() { SourceImagePath = "path1.jpg" },
                    new() { SourceImagePath = "path2.jpg" }
                }
            }
        };
        var practices = new List<MistakePracticeModel>
        {
            new()
            {
                ImagePaths = new List<string> { "practice1.jpg" }
            }
        };

        _mockItemRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(items);
        _mockPracticeRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(practices);

        var result = await _service.GetAllReferencedImagePathsAsync();

        result.Should().HaveCount(3);
        result.Should().Contain("path1.jpg");
        result.Should().Contain("path2.jpg");
        result.Should().Contain("practice1.jpg");
    }

    [Fact]
    public async Task UpdateMistakeItemAsync_ShouldUpdateItem()
    {
        var item = new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1"
        };

        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var result = await _service.UpdateMistakeItemAsync(item);

        result.Should().BeTrue();
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task GetPendingReviewUploadsAsync_ShouldReturnPendingItems()
    {
        var expectedUploads = new List<PendingReviewUploadModel>
        {
            new() { StudentId = "student-1", SourceUploadId = "upload-1" }
        };

        _mockItemRepo.Setup(x => x.GetPendingReviewUploadsAsync(1, 10, null))
            .ReturnsAsync((expectedUploads, 1));

        var result = await _service.GetPendingReviewUploadsAsync(1, 10);

        result.Uploads.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
    }
}
