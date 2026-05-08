using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Mistake.Domain.Tests;

public class MistakeDomainServiceTests
{
    private readonly Mock<IMistakeRepository> _mockMistakeRepo;
    private readonly Mock<IMistakeDetailRepository> _mockDetailRepo;
    private readonly Mock<IMistakePracticeRepository> _mockPracticeRepo;
    private readonly Mock<IProcessLogRepository> _mockLogRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IOssService> _mockOssService;
    private readonly Mock<ILogger<MistakeDomainService>> _mockLogger;
    private readonly MistakeDomainService _service;

    public MistakeDomainServiceTests()
    {
        _mockMistakeRepo = new Mock<IMistakeRepository>();
        _mockDetailRepo = new Mock<IMistakeDetailRepository>();
        _mockPracticeRepo = new Mock<IMistakePracticeRepository>();
        _mockLogRepo = new Mock<IProcessLogRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockOssService = new Mock<IOssService>();
        _mockLogger = new Mock<ILogger<MistakeDomainService>>();

        _service = new MistakeDomainService(
            _mockMistakeRepo.Object,
            _mockDetailRepo.Object,
            _mockPracticeRepo.Object,
            _mockLogRepo.Object,
            _mockUow.Object,
            _mockOssService.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task DeleteMistakeAsync_WhenMistakeIsPending_ShouldDeleteAllImagesAndRemoveFromDb()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 1,
            Subject = 1,
            Grade = 1
        };
        var detail = new MistakeDetailModel
        {
            MistakeId = mistakeId,
            OriginalImages = new List<string> { "path1.jpg", "path2.jpg" }
        };
        var practices = new List<MistakePracticeModel>
        {
            new() { MistakeId = mistakeId, ImagePaths = new List<string> { "practice1.jpg" } }
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(detail);
        _mockPracticeRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(practices);
        _mockOssService.Setup(x => x.DeleteAsync(It.IsAny<string>())).ReturnsAsync(true);

        var result = await _service.DeleteMistakeAsync(mistakeId);

        result.Should().BeTrue();
        _mockOssService.Verify(x => x.DeleteAsync("path1.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("path2.jpg"), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync("practice1.jpg"), Times.Once);
        _mockDetailRepo.Verify(x => x.DeleteByMistakeIdAsync(mistakeId), Times.Once);
        _mockPracticeRepo.Verify(x => x.RemoveByMistakeIdAsync(mistakeId), Times.Once);
        _mockLogRepo.Verify(x => x.RemoveByMistakeIdAsync(mistakeId), Times.Once);
        _mockMistakeRepo.Verify(x => x.Remove(mistake), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeAsync_WhenMistakeIsProcessed_ShouldReturnFalseAndNotDelete()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 2,
            Subject = 1,
            Grade = 1
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);

        var result = await _service.DeleteMistakeAsync(mistakeId);

        result.Should().BeFalse();
        _mockOssService.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);
        _mockMistakeRepo.Verify(x => x.Remove(It.IsAny<MistakeModel>()), Times.Never);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task DeleteMistakeAsync_WhenDetailHasNoImages_ShouldStillDeleteDetailRecord()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 1,
            Subject = 1,
            Grade = 1
        };
        var detail = new MistakeDetailModel
        {
            MistakeId = mistakeId,
            OriginalImages = new List<string>()
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(detail);
        _mockPracticeRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(new List<MistakePracticeModel>());

        var result = await _service.DeleteMistakeAsync(mistakeId);

        result.Should().BeTrue();
        _mockDetailRepo.Verify(x => x.DeleteByMistakeIdAsync(mistakeId), Times.Once);
        _mockMistakeRepo.Verify(x => x.Remove(mistake), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeImageAsync_WhenMistakeIsPending_ShouldRemoveImageAndDeleteFromOss()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 1,
            Subject = 1,
            Grade = 1
        };
        var imagePath = "mistakes/1/student-1/2026/05/01/{id}/original_0.jpg";

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.RemoveImageAsync(mistakeId, imagePath)).ReturnsAsync(true);
        _mockOssService.Setup(x => x.DeleteAsync(imagePath)).ReturnsAsync(true);

        var result = await _service.DeleteMistakeImageAsync(mistakeId, imagePath);

        result.Should().BeTrue();
        _mockDetailRepo.Verify(x => x.RemoveImageAsync(mistakeId, imagePath), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync(imagePath), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeImageAsync_WhenMistakeIsProcessed_ShouldReturnFalse()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 2,
            Subject = 1,
            Grade = 1
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);

        var result = await _service.DeleteMistakeImageAsync(mistakeId, "some/path.jpg");

        result.Should().BeFalse();
        _mockDetailRepo.Verify(x => x.RemoveImageAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _mockOssService.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMistakeImageAsync_WhenImageNotFoundInDetail_ShouldReturnFalse()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 1,
            Subject = 1,
            Grade = 1
        };
        var imagePath = "nonexistent.jpg";

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.RemoveImageAsync(mistakeId, imagePath)).ReturnsAsync(false);

        var result = await _service.DeleteMistakeImageAsync(mistakeId, imagePath);

        result.Should().BeFalse();
        _mockOssService.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetMistakeAsync_ShouldReturnBothMistakeAndDetail()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel { Id = mistakeId, StudentId = "s1", Subject = 1, Grade = 1 };
        var detail = new MistakeDetailModel { MistakeId = mistakeId, OriginalImages = new List<string> { "img.jpg" } };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(detail);

        var (resultMistake, resultDetail) = await _service.GetMistakeAsync(mistakeId);

        resultMistake.Should().NotBeNull();
        resultDetail.Should().NotBeNull();
        resultMistake!.Id.Should().Be(mistakeId);
        resultDetail!.MistakeId.Should().Be(mistakeId);
    }

    [Fact]
    public async Task MarkMistakeProcessedAsync_ShouldUpdateStatusAndQuestionId()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel { Id = mistakeId, Status = 1, Subject = 1, Grade = 1 };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);

        var result = await _service.MarkMistakeProcessedAsync(mistakeId, "q-123");

        result.Should().BeTrue();
        mistake.Status.Should().Be(2);
        mistake.QuestionId.Should().Be("q-123");
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task GetMistakeForReturnAsync_WhenMistakeExists_ShouldReturnStudentIdAndImagePaths()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            StudentId = "student-123",
            Subject = 1,
            Grade = 1
        };
        var detail = new MistakeDetailModel
        {
            MistakeId = mistakeId,
            OriginalImages = new List<string> { "path1.jpg", "path2.jpg" },
            Comments = "some comments"
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(detail);

        var (success, studentId, imagePaths, comments) = await _service.GetMistakeForReturnAsync(mistakeId);

        success.Should().BeTrue();
        studentId.Should().Be("student-123");
        imagePaths.Should().BeEquivalentTo(new List<string> { "path1.jpg", "path2.jpg" });
        comments.Should().Be("some comments");
    }

    [Fact]
    public async Task GetMistakeForReturnAsync_WhenMistakeNotFound_ShouldReturnFalse()
    {
        var mistakeId = Guid.NewGuid();

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync((MistakeModel?)null);

        var (success, studentId, imagePaths, comments) = await _service.GetMistakeForReturnAsync(mistakeId);

        success.Should().BeFalse();
        studentId.Should().BeNull();
        imagePaths.Should().BeNull();
    }

    [Fact]
    public async Task DeleteMistakeWithoutDeletingImagesAsync_WhenMistakeExists_ShouldDeleteRecordsButNotImages()
    {
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            StudentId = "student-123",
            Subject = 1,
            Grade = 1,
            Status = 1
        };
        var detail = new MistakeDetailModel
        {
            MistakeId = mistakeId,
            OriginalImages = new List<string> { "path1.jpg", "path2.jpg" }
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(detail);
        _mockPracticeRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(new List<MistakePracticeModel>());

        var result = await _service.DeleteMistakeWithoutDeletingImagesAsync(mistakeId);

        result.Should().BeTrue();
        _mockPracticeRepo.Verify(x => x.RemoveByMistakeIdAsync(mistakeId), Times.Once);
        _mockLogRepo.Verify(x => x.RemoveByMistakeIdAsync(mistakeId), Times.Once);
        _mockDetailRepo.Verify(x => x.DeleteByMistakeIdAsync(mistakeId), Times.Once);
        _mockMistakeRepo.Verify(x => x.Remove(mistake), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
        _mockOssService.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMistakeWithoutDeletingImagesAsync_WhenMistakeNotFound_ShouldReturnFalse()
    {
        var mistakeId = Guid.NewGuid();

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync((MistakeModel?)null);

        var result = await _service.DeleteMistakeWithoutDeletingImagesAsync(mistakeId);

        result.Should().BeFalse();
        _mockMistakeRepo.Verify(x => x.Remove(It.IsAny<MistakeModel>()), Times.Never);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Never);
    }
}
