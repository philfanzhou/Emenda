using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Mistake.Components.Oss;
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
    private readonly Mock<IImageFingerprintRepository> _mockFingerprintRepo;
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
        _mockFingerprintRepo = new Mock<IImageFingerprintRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockOssService = new Mock<IOssService>();
        _mockLogger = new Mock<ILogger<MistakeDomainService>>();

        _service = new MistakeDomainService(
            _mockMistakeRepo.Object,
            _mockDetailRepo.Object,
            _mockPracticeRepo.Object,
            _mockLogRepo.Object,
            _mockFingerprintRepo.Object,
            _mockUow.Object,
            _mockOssService.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task UploadMistakeAsync_ShouldUploadImagesAndSaveToDb()
    {
        var mistake = new MistakeModel
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 1
        };
        var images = new List<byte[]> { new byte[] { 1, 2, 3 } };
        var expectedPath = "mistakes/1/student-1/2026/05/01/{mistakeId}/original_0.jpg";

        _mockOssService.Setup(x => x.UploadAsync(
                It.IsAny<byte[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<OssBucket>(),
                It.IsAny<string?>()))
            .ReturnsAsync(expectedPath);

        _mockFingerprintRepo.Setup(x => x.GetByHashesAndStudentIdAsync(It.IsAny<List<string>>(), It.IsAny<string>()))
            .ReturnsAsync(new List<ImageFingerprintModel>());

        var result = await _service.UploadMistakeAsync(mistake, images);

        result.Success.Should().BeTrue();
        result.Mistake.Should().NotBeNull();
        result.Mistake!.Id.Should().NotBeEmpty();
        result.Mistake.Status.Should().Be(1);
        result.Detail.Should().NotBeNull();
        result.Detail!.OriginalImages.Should().ContainSingle().Which.Should().Be(expectedPath);

        _mockMistakeRepo.Verify(x => x.AddAsync(It.Is<MistakeModel>(m => m == result.Mistake)), Times.Once);
        _mockDetailRepo.Verify(x => x.AddAsync(It.Is<MistakeDetailModel>(d => d.MistakeId == result.Mistake.Id)), Times.Once);
        _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(l => l.MistakeId == result.Mistake.Id && l.Action == 1)), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
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
        _mockFingerprintRepo.Verify(x => x.RemoveByOssPathAsync(imagePath), Times.Once);
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
    public async Task SplitMistakeAsync_ShouldDeleteOriginalAndCreateNewMistakes()
    {
        var originalId = Guid.NewGuid();
        var originalMistake = new MistakeModel
        {
            Id = originalId,
            Status = 2,
            Subject = 1,
            Grade = 1
        };
        var originalDetail = new MistakeDetailModel
        {
            MistakeId = originalId,
            OriginalImages = new List<string> { "original.jpg" }
        };

        var newMistake1 = new MistakeModel { Subject = 1, Grade = 1, StudentId = "s1" };
        var newMistake2 = new MistakeModel { Subject = 1, Grade = 1, StudentId = "s1" };
        var newMistakes = new List<(MistakeModel mistake, List<byte[]>? images, string? comments)>
        {
            (newMistake1, null, "Part 1"),
            (newMistake2, null, "Part 2")
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(originalId)).ReturnsAsync(originalMistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(originalId)).ReturnsAsync(originalDetail);
        _mockPracticeRepo.Setup(x => x.GetByMistakeIdAsync(originalId)).ReturnsAsync(new List<MistakePracticeModel>());

        var result = await _service.SplitMistakeAsync(originalId, newMistakes);

        result.Should().BeTrue();
        _mockMistakeRepo.Verify(x => x.Remove(originalMistake), Times.Once);
        _mockMistakeRepo.Verify(x => x.AddAsync(It.IsAny<MistakeModel>()), Times.Exactly(2));
        _mockDetailRepo.Verify(x => x.AddAsync(It.IsAny<MistakeDetailModel>()), Times.Exactly(2));
        _mockLogRepo.Verify(x => x.AddAsync(It.IsAny<ProcessLogModel>()), Times.Exactly(2));
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);

        _mockOssService.Verify(x => x.DeleteAsync("original.jpg"), Times.Once);
        _mockDetailRepo.Verify(x => x.DeleteByMistakeIdAsync(originalId), Times.Once);
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
    public async Task UploadMistakeAsync_WhenImageAlreadyExists_ShouldReturnDuplicates()
    {
        var mistake = new MistakeModel
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 1
        };
        var imageBytes = new byte[] { 1, 2, 3 };
        var images = new List<byte[]> { imageBytes };
        var existingMistakeId = Guid.NewGuid();
        var expectedHash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();

        _mockFingerprintRepo.Setup(x => x.GetByHashesAndStudentIdAsync(It.IsAny<List<string>>(), "student-1"))
            .ReturnsAsync(new List<ImageFingerprintModel>
            {
                new() { Hash = expectedHash, StudentId = "student-1", MistakeId = existingMistakeId, CreatedAt = DateTime.UtcNow }
            });

        var result = await _service.UploadMistakeAsync(mistake, images);

        result.Success.Should().BeFalse();
        result.Duplicates.Should().NotBeEmpty();
        result.Duplicates[0].MistakeId.Should().Be(existingMistakeId);
        _mockOssService.Verify(x => x.UploadAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OssBucket>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task UploadMistakeAsync_WhenSameImageInBatch_ShouldReturnDuplicate()
    {
        var mistake = new MistakeModel
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 1
        };
        var sameImage = new byte[] { 1, 2, 3 };
        var images = new List<byte[]> { sameImage, sameImage };

        var result = await _service.UploadMistakeAsync(mistake, images);

        result.Success.Should().BeFalse();
        result.Duplicates.Should().NotBeEmpty();
    }
}
