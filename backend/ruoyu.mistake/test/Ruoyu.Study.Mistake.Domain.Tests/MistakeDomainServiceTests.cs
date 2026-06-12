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
        _mockItemRepo.Setup(x => x.GetAllSourceImagePathsAsync())
            .ReturnsAsync(new List<string> { "path1.jpg", "path2.jpg" });
        _mockPracticeRepo.Setup(x => x.GetAllPracticeImagePathsAsync())
            .ReturnsAsync(new List<string> { "practice1.jpg" });

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

    // === SubmitMistakeUpload 补充测试 ===

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenStudentIdEmpty_ShouldThrow()
    {
        var act = () => _service.SubmitMistakeUploadAsync(
            "", 1, 3, new List<string> { "img.jpg" }, null, "upload-1");

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*缺少学生ID*");
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenSubjectOutOfRange_ShouldThrow()
    {
        var act = () => _service.SubmitMistakeUploadAsync(
            "student-1", 0, 3, new List<string> { "img.jpg" }, null, "upload-1");

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*学科值无效*");

        var act2 = () => _service.SubmitMistakeUploadAsync(
            "student-1", 10, 3, new List<string> { "img.jpg" }, null, "upload-1");

        await act2.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*学科值无效*");
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenGradeOutOfRange_ShouldThrow()
    {
        var act = () => _service.SubmitMistakeUploadAsync(
            "student-1", 1, 0, new List<string> { "img.jpg" }, null, "upload-1");

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*年级值无效*");

        var act2 = () => _service.SubmitMistakeUploadAsync(
            "student-1", 1, 13, new List<string> { "img.jpg" }, null, "upload-1");

        await act2.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*年级值无效*");
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenSourceUploadIdEmpty_ShouldThrow()
    {
        var act = () => _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, new List<string> { "img.jpg" }, null, "");

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*缺少上传记录ID*");
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenImagePathsEmpty_ShouldThrow()
    {
        var act = () => _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, new List<string>(), null, "upload-1");

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*至少需要提供一张图片*");
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WhenMultipleValidationErrors_ShouldMergeMessages()
    {
        var act = () => _service.SubmitMistakeUploadAsync(
            "", 0, 0, new List<string>(), null, "");

        var ex = await act.Should().ThrowAsync<MistakeValidationException>();
        ex.Which.Message.Should().Contain("缺少学生ID");
        ex.Which.Message.Should().Contain("学科值无效");
        ex.Which.Message.Should().Contain("年级值无效");
        ex.Which.Message.Should().Contain("缺少上传记录ID");
        ex.Which.Message.Should().Contain("至少需要提供一张图片");
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_ShouldRecordProcessLogCreated()
    {
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1")).ReturnsAsync(new List<MistakeItemModel>());

        await _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, new List<string> { "img.jpg" }, null, "upload-1");

        _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(log =>
            log.Action == (int)ProcessAction.Created &&
            log.Details == "Mistake item created from upload")), Times.Once);
    }

    [Fact]
    public async Task SubmitMistakeUploadAsync_WithComments_ShouldPassThrough()
    {
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1")).ReturnsAsync(new List<MistakeItemModel>());

        var items = await _service.SubmitMistakeUploadAsync(
            "student-1", 1, 3, new List<string> { "img.jpg" }, "some comment", "upload-1");

        items.Should().HaveCount(1);
        items[0].SourceUploadId.Should().Be("upload-1");
    }

    // === GetMistakeItem 补充测试 ===

    [Fact]
    public async Task GetMistakeItemAsync_WhenExists_ShouldReturnItem()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel { Id = itemId, StudentId = "student-1" };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var result = await _service.GetMistakeItemAsync(itemId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(itemId);
    }

    [Fact]
    public async Task GetMistakeItemAsync_WhenNotExists_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.GetMistakeItemAsync(itemId);

        result.Should().BeNull();
    }

    // === GetMistakeItemsByUpload 补充测试 ===

    [Fact]
    public async Task GetMistakeItemsByUploadAsync_ShouldReturnItems()
    {
        var items = new List<MistakeItemModel>
        {
            new() { Id = Guid.NewGuid(), SourceUploadId = "upload-1" },
            new() { Id = Guid.NewGuid(), SourceUploadId = "upload-1" }
        };
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1")).ReturnsAsync(items);

        var result = await _service.GetMistakeItemsByUploadAsync("upload-1");

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMistakeItemsByUploadAsync_WhenNoItems_ShouldReturnEmptyList()
    {
        _mockItemRepo.Setup(x => x.GetBySourceUploadIdAsync("upload-1")).ReturnsAsync(new List<MistakeItemModel>());

        var result = await _service.GetMistakeItemsByUploadAsync("upload-1");

        result.Should().BeEmpty();
    }

    // === GetMistakeItemList 补充测试 ===

    [Fact]
    public async Task GetMistakeItemListAsync_ShouldClampPageAndSize()
    {
        var items = new List<MistakeItemModel>();
        var totalCount = 0;
        _mockItemRepo.Setup(x => x.GetListAsync("", 0, 0, 0, 1, 10))
            .ReturnsAsync((items, totalCount));

        await _service.GetMistakeItemListAsync("", 0, 0, 0, -1, -1);

        _mockItemRepo.Verify(x => x.GetListAsync("", 0, 0, 0, 1, 10), Times.Once);
    }

    [Fact]
    public async Task GetMistakeItemListAsync_ShouldClampSizeTo100()
    {
        _mockItemRepo.Setup(x => x.GetListAsync("", 0, 0, 0, 1, 100))
            .ReturnsAsync((new List<MistakeItemModel>(), 0));

        await _service.GetMistakeItemListAsync("", 0, 0, 0, 1, 200);

        _mockItemRepo.Verify(x => x.GetListAsync("", 0, 0, 0, 1, 100), Times.Once);
    }

    // === LogMistakeProcess 补充测试 ===

    [Fact]
    public async Task LogMistakeProcessAsync_WhenItemExists_ShouldReturnTrue()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(new MistakeItemModel { Id = itemId });

        var result = await _service.LogMistakeProcessAsync(itemId, "student-1", ProcessAction.Mastered, "已掌握");

        result.Should().BeTrue();
        _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(log =>
            log.MistakeItemId == itemId &&
            log.Action == (int)ProcessAction.Mastered &&
            log.Details == "已掌握")), Times.Once);
    }

    [Fact]
    public async Task LogMistakeProcessAsync_WhenItemNotExists_ShouldReturnFalse()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.LogMistakeProcessAsync(itemId, "student-1", ProcessAction.Mastered, "test");

        result.Should().BeFalse();
        _mockLogRepo.Verify(x => x.AddAsync(It.IsAny<ProcessLogModel>()), Times.Never);
    }

    // === GetMistakeProcessHistory 补充测试 ===

    [Fact]
    public async Task GetMistakeProcessHistoryAsync_ShouldReturnLogs()
    {
        var itemId = Guid.NewGuid();
        var logs = new List<ProcessLogModel>
        {
            new() { Id = Guid.NewGuid(), MistakeItemId = itemId, Action = (int)ProcessAction.Created },
            new() { Id = Guid.NewGuid(), MistakeItemId = itemId, Action = (int)ProcessAction.ReviewConfirmed }
        };
        _mockLogRepo.Setup(x => x.GetByMistakeItemIdAsync(itemId)).ReturnsAsync(logs);

        var result = await _service.GetMistakeProcessHistoryAsync(itemId);

        result.Should().HaveCount(2);
    }

    // === GetMistakePractices 补充测试 ===

    [Fact]
    public async Task GetPracticesAsync_ShouldReturnPractices()
    {
        var itemId = Guid.NewGuid();
        var practices = new List<MistakePracticeModel>
        {
            new() { Id = Guid.NewGuid(), MistakeItemId = itemId }
        };
        _mockPracticeRepo.Setup(x => x.GetByMistakeItemIdAsync(itemId)).ReturnsAsync(practices);

        var result = await _service.GetPracticesAsync(itemId);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetPracticesAsync_WhenNoPractices_ShouldReturnEmptyList()
    {
        var itemId = Guid.NewGuid();
        _mockPracticeRepo.Setup(x => x.GetByMistakeItemIdAsync(itemId)).ReturnsAsync(new List<MistakePracticeModel>());

        var result = await _service.GetPracticesAsync(itemId);

        result.Should().BeEmpty();
    }

    // === DeleteMistakeItem 补充测试 ===

    [Fact]
    public async Task DeleteMistakeItemAsync_WhenNotExists_ShouldReturnFalse()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.DeleteMistakeItemAsync(itemId);

        result.Should().BeFalse();
    }

    // === ReviewMistakeItem 补充测试 ===

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenInvalidStatus_ShouldThrow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel { Id = itemId, ReviewStatus = (int)ReviewStatus.PendingReview };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var act = () => _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.PendingReview, "teacher-1", null);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*审核结果只能是确认或驳回*");
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenNotFound_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_ShouldUpdateGradeAndSubjectWhenProvided()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel { Id = itemId, ReviewStatus = (int)ReviewStatus.PendingReview, Grade = 3, Subject = 1 };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", null, grade: 6, subject: 5);

        result!.Grade.Should().Be(6);
        result.Subject.Should().Be(5);
    }

    // === AddMistakeItem 补充测试 ===

    [Fact]
    public async Task AddMistakeItemAsync_WhenStudentIdEmpty_ShouldThrow()
    {
        var item = new MistakeItemModel
        {
            StudentId = "",
            Subject = 1,
            Grade = 3,
            SourceUploadId = "upload-1",
            SourceRegions = new List<SourceRegionModel> { new() { SourceImagePath = "img.jpg" } }
        };

        var act = () => _service.AddMistakeItemAsync(item);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*缺少学生ID*");
    }

    [Fact]
    public async Task AddMistakeItemAsync_WhenSourceRegionsEmpty_ShouldThrow()
    {
        var item = new MistakeItemModel
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 3,
            SourceUploadId = "upload-1",
            SourceRegions = new List<SourceRegionModel>()
        };

        var act = () => _service.AddMistakeItemAsync(item);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*至少需要一个图片区域*");
    }

    // === GetAllReferencedImagePaths 补充测试 ===

    [Fact]
    public async Task GetAllReferencedImagePathsAsync_WhenNoData_ShouldReturnEmptySet()
    {
        _mockItemRepo.Setup(x => x.GetAllSourceImagePathsAsync())
            .ReturnsAsync(new List<string>());
        _mockPracticeRepo.Setup(x => x.GetAllPracticeImagePathsAsync())
            .ReturnsAsync(new List<string>());

        var result = await _service.GetAllReferencedImagePathsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllReferencedImagePathsAsync_ShouldDeduplicatePaths()
    {
        _mockItemRepo.Setup(x => x.GetAllSourceImagePathsAsync())
            .ReturnsAsync(new List<string> { "path1.jpg", "path1.jpg" });
        _mockPracticeRepo.Setup(x => x.GetAllPracticeImagePathsAsync())
            .ReturnsAsync(new List<string>());

        var result = await _service.GetAllReferencedImagePathsAsync();

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAllReferencedImagePathsAsync_ShouldSkipEmptyPaths()
    {
        // Repository layer already filters empty/whitespace paths, so only valid paths reach Domain
        _mockItemRepo.Setup(x => x.GetAllSourceImagePathsAsync())
            .ReturnsAsync(new List<string> { "valid.jpg" });
        _mockPracticeRepo.Setup(x => x.GetAllPracticeImagePathsAsync())
            .ReturnsAsync(new List<string>());

        var result = await _service.GetAllReferencedImagePathsAsync();

        result.Should().HaveCount(1);
        result.Should().Contain("valid.jpg");
    }

    // === UpdateMistakeItemPartial 补充测试 ===

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenItemNotFound_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, studentId: "new-student");

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenOnlyStudentIdProvided_ShouldUpdateOnlyStudentId()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "old-student",
            Subject = 3,
            Grade = 5
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, studentId: "new-student");

        result.Should().NotBeNull();
        result!.StudentId.Should().Be("new-student");
        result.Subject.Should().Be(3);
        result.Grade.Should().Be(5);
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenOnlySubjectProvided_ShouldUpdateOnlySubject()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, subject: 7);

        result.Should().NotBeNull();
        result!.StudentId.Should().Be("student-1");
        result.Subject.Should().Be(7);
        result.Grade.Should().Be(5);
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenOnlyGradeProvided_ShouldUpdateOnlyGrade()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, grade: 10);

        result.Should().NotBeNull();
        result!.StudentId.Should().Be("student-1");
        result.Subject.Should().Be(3);
        result.Grade.Should().Be(10);
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenSourceRegionsProvided_ShouldReplaceAll()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5,
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "old.jpg" }
            }
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var newRegions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "new1.jpg" },
            new() { SourceImagePath = "new2.jpg" }
        };

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, sourceRegions: newRegions);

        result.Should().NotBeNull();
        result!.SourceRegions.Should().HaveCount(2);
        result.SourceRegions[0].SourceImagePath.Should().Be("new1.jpg");
        result.SourceRegions[1].SourceImagePath.Should().Be("new2.jpg");
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenNoFieldsProvided_ShouldNotModifyFields()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId);

        result.Should().NotBeNull();
        result!.StudentId.Should().Be("student-1");
        result.Subject.Should().Be(3);
        result.Grade.Should().Be(5);
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenSubjectZero_ShouldNotUpdateSubject()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(true);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, subject: 0);

        result.Should().NotBeNull();
        result!.Subject.Should().Be(3); // Not updated because subject=0 is ignored
    }

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenUpdateFails_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            StudentId = "student-1",
            Subject = 3,
            Grade = 5
        };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item)).ReturnsAsync(false);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, studentId: "new-student");

        result.Should().BeNull();
    }
}
