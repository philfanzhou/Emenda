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
    public async Task SubmitMistakeUploadAsync_ShouldCreateOneItemPerImage()
    {
        var imagePaths = new List<string> { "img1.jpg", "img2.jpg" };

        _mockItemRepo.Setup(x => x.ExistsBySourceUploadIdAsync("upload-1")).ReturnsAsync(false);

        var items = await _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, imagePaths, null, "upload-1");

        items.Should().HaveCount(2);
        items[0].SourceImagePath.Should().Be("img1.jpg");
        items[1].SourceImagePath.Should().Be("img2.jpg");
        items[0].ReviewStatus.Should().Be((int)ReviewStatus.PendingReview);
        items[1].ReviewStatus.Should().Be((int)ReviewStatus.PendingReview);
        _mockItemRepo.Verify(x => x.AddAsync(It.IsAny<MistakeItemModel>()), Times.Exactly(2));
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenAlreadyExists_ShouldThrow()
    {
        _mockItemRepo.Setup(x => x.ExistsBySourceUploadIdAsync("upload-1")).ReturnsAsync(true);

        var act = () => _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, new List<string> { "img.jpg" }, null, "upload-1");

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*已提交过错题*");
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

        result.Should().BeTrue();
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
    public async Task DeleteMistakeItemAsync_WhenConfirmed_ShouldThrow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.Confirmed
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var act = () => _service.DeleteMistakeItemAsync(itemId);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*已确认的错题不能删除*");
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

        var (success, errorMessage, sourceUploadId) = await _service.CompleteUploadReviewAsync("upload-1", "teacher-1");

        success.Should().BeFalse();
        errorMessage.Should().Contain("2 条错题待审核");
        sourceUploadId.Should().BeNull();
    }

    [Fact]
    public async Task CompleteUploadReviewAsync_WhenAllReviewed_ShouldReturnSuccess()
    {
        _mockItemRepo.Setup(x => x.CountPendingBySourceUploadIdAsync("upload-1")).ReturnsAsync(0);
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1"))
            .ReturnsAsync(new List<MistakeItemModel>
            {
                new() { Id = Guid.NewGuid(), SourceUploadId = "upload-1" }
            });

        var (success, errorMessage, sourceUploadId) = await _service.CompleteUploadReviewAsync("upload-1", "teacher-1");

        success.Should().BeTrue();
        errorMessage.Should().BeNull();
        sourceUploadId.Should().Be("upload-1");
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
}
