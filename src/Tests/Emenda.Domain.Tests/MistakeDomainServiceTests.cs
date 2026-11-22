using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;
using Emenda.Domain.Services;
using Xunit;

namespace Emenda.Domain.Tests;

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
    public async Task ReviewMistakeItemAsync_WhenPending_ShouldConfirm()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel
        {
            Id = itemId,
            ReviewStatus = (int)ReviewStatus.PendingReview
        };

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", "looks good");

        result.Should().NotBeNull();
        item.ReviewStatus.Should().Be((int)ReviewStatus.Confirmed);
        item.ReviewerId.Should().Be("teacher-1");
        item.RootCause.Should().Be("looks good");
        item.ReviewedAt.Should().NotBeNull();
        _mockUow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

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

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.DeleteAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.DeleteMistakeItemAsync(itemId);

        result.Should().BeTrue();
        _mockPracticeRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockLogRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockItemRepo.Verify(x => x.DeleteAsync(itemId, It.IsAny<CancellationToken>()), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.DeleteAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.DeleteMistakeItemAsync(itemId);

        result.Should().BeTrue();
        _mockPracticeRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockLogRepo.Verify(x => x.RemoveByMistakeItemIdAsync(itemId), Times.Once);
        _mockItemRepo.Verify(x => x.DeleteAsync(itemId, It.IsAny<CancellationToken>()), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

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

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var practice = new MistakePracticeModel
        {
            MistakeItemId = itemId,
            StudentId = "student-1",
            IsCorrect = false
        };

        var result = await _service.AddPracticeAsync(practice);

        result.Should().NotBeNull();
        result.MistakeItemId.Should().Be(itemId);
        _mockUow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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

        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Rejected, "teacher-1", "需要重新上传");

        result.Should().NotBeNull();
        item.ReviewStatus.Should().Be((int)ReviewStatus.Rejected);
        item.RootCause.Should().Be("需要重新上传");
        _mockUow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }



    [Fact]
    public async Task GetPendingReviewUploadsAsync_ShouldReturnPendingItems()
    {
        var expectedUploads = new List<PendingReviewUploadModel>
        {
            new() { StudentId = "student-1", SourceUploadId = "upload-1" }
        };

        _mockItemRepo.Setup(x => x.GetPendingReviewUploadsAsync(1, 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedUploads, 1));

        var result = await _service.GetPendingReviewUploadsAsync(1, 10);

        result.Uploads.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
    }

    // === SubmitMistakeUpload 补充测试 ===










    // === GetMistakeItem 补充测试 ===

    [Fact]
    public async Task GetMistakeItemAsync_WhenExists_ShouldReturnItem()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel { Id = itemId, StudentId = "student-1" };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await _service.GetMistakeItemAsync(itemId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(itemId);
    }

    [Fact]
    public async Task GetMistakeItemAsync_WhenNotExists_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync((MistakeItemModel?)null);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(new MistakeItemModel { Id = itemId });

        var result = await _service.LogMistakeProcessAsync(itemId, "student-1", ProcessAction.Mastered, "已掌握");

        result.Should().BeTrue();
        _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(log =>
            log.MistakeItemId == itemId &&
            log.Action == (int)ProcessAction.Mastered &&
            log.Details == "已掌握"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LogMistakeProcessAsync_WhenItemNotExists_ShouldReturnFalse()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.LogMistakeProcessAsync(itemId, "student-1", ProcessAction.Mastered, "test");

        result.Should().BeFalse();
        _mockLogRepo.Verify(x => x.AddAsync(It.IsAny<ProcessLogModel>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.DeleteMistakeItemAsync(itemId);

        result.Should().BeFalse();
    }

    // === ReviewMistakeItem 补充测试 ===

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenInvalidStatus_ShouldThrow()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel { Id = itemId, ReviewStatus = (int)ReviewStatus.PendingReview };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var act = () => _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.PendingReview, "teacher-1", null);

        await act.Should().ThrowAsync<MistakeValidationException>()
            .WithMessage("*审核结果只能是确认或退回*");
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_WhenNotFound_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReviewMistakeItemAsync_ShouldUpdateGradeAndSubjectWhenProvided()
    {
        var itemId = Guid.NewGuid();
        var item = new MistakeItemModel { Id = itemId, ReviewStatus = (int)ReviewStatus.PendingReview, Grade = 3, Subject = 1 };
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await _service.ReviewMistakeItemAsync(
            itemId, (int)ReviewStatus.Confirmed, "teacher-1", null, grade: 6, subject: 5);

        result!.Grade.Should().Be(6);
        result.Subject.Should().Be(5);
    }

    // === AddMistakeItem 补充测试 ===



    // === UpdateMistakeItemPartial 补充测试 ===

    [Fact]
    public async Task UpdateMistakeItemPartialAsync_WhenItemNotFound_ShouldReturnNull()
    {
        var itemId = Guid.NewGuid();
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync((MistakeItemModel?)null);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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
        _mockItemRepo.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _service.UpdateMistakeItemPartialAsync(itemId, studentId: "new-student");

        result.Should().BeNull();
    }

    // ========== ReanalyzeMistakeItemAsync validation tests ==========








}
