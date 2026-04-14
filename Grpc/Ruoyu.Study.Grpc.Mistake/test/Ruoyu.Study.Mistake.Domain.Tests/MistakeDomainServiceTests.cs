using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
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
    private readonly Mock<IMistakePracticeRepository> _mockPracticeRepo;
    private readonly Mock<IProcessLogRepository> _mockLogRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IOssService> _mockOssService;
    private readonly MistakeDomainService _service;

    public MistakeDomainServiceTests()
    {
        _mockMistakeRepo = new Mock<IMistakeRepository>();
        _mockPracticeRepo = new Mock<IMistakePracticeRepository>();
        _mockLogRepo = new Mock<IProcessLogRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockOssService = new Mock<IOssService>();

        _service = new MistakeDomainService(
            _mockMistakeRepo.Object,
            _mockPracticeRepo.Object,
            _mockLogRepo.Object,
            _mockUow.Object,
            _mockOssService.Object);
    }

    [Fact]
    public async Task UploadMistakeAsync_ShouldUploadImagesAndSaveToDb()
    {
        // Arrange
        var mistake = new MistakeModel
        {
            StudentId = "student-1",
            Content = "Test content"
        };
        var images = new List<byte[]> { new byte[] { 1, 2, 3 } };
        var expectedPath = "mistakes/2026/04/14/uuid/m_uuid_0.jpg";

        _mockOssService.Setup(x => x.UploadAsync(
                It.IsAny<byte[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<OssBucket>()))
            .ReturnsAsync(expectedPath);

        // Act
        var result = await _service.UploadMistakeAsync(mistake, images);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBeEmpty();
        result.Status.Should().Be(1); // MISTAKE_STATUS_PENDING
        result.ImagePaths.Should().ContainSingle().Which.Should().Be(expectedPath);

        _mockMistakeRepo.Verify(x => x.AddAsync(It.Is<MistakeModel>(m => m == result)), Times.Once);
        _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(l => l.MistakeId == result.Id && l.Action == 1)), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeAsync_WhenMistakeIsPending_ShouldDeleteImagesAndRemoveFromDb()
    {
        // Arrange
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 1, // MISTAKE_STATUS_PENDING
            ImagePaths = new List<string> { "path1.jpg", "path2.jpg" }
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockOssService.Setup(x => x.DeleteManyAsync(mistake.ImagePaths)).ReturnsAsync(2);

        // Act
        var result = await _service.DeleteMistakeAsync(mistakeId);

        // Assert
        result.Should().BeTrue();
        _mockOssService.Verify(x => x.DeleteManyAsync(mistake.ImagePaths), Times.Once);
        _mockMistakeRepo.Verify(x => x.Remove(mistake), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeAsync_WhenMistakeIsProcessed_ShouldReturnFalseAndNotDelete()
    {
        // Arrange
        var mistakeId = Guid.NewGuid();
        var mistake = new MistakeModel
        {
            Id = mistakeId,
            Status = 2, // MISTAKE_STATUS_PROCESSED
            ImagePaths = new List<string> { "path1.jpg" }
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);

        // Act
        var result = await _service.DeleteMistakeAsync(mistakeId);

        // Assert
        result.Should().BeFalse();
        _mockOssService.Verify(x => x.DeleteManyAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
        _mockMistakeRepo.Verify(x => x.Remove(It.IsAny<MistakeModel>()), Times.Never);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task SplitMistakeAsync_ShouldDeleteOriginalAndCreateNewMistakes()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var originalMistake = new MistakeModel
        {
            Id = originalId,
            Status = 2, // MISTAKE_STATUS_PROCESSED
            ImagePaths = new List<string> { "original.jpg" }
        };

        var newMistake1 = new MistakeModel { Content = "Part 1" };
        var newMistake2 = new MistakeModel { Content = "Part 2" };
        var newMistakes = new List<(MistakeModel, List<byte[]>?)>
        {
            (newMistake1, null),
            (newMistake2, null)
        };

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(originalId)).ReturnsAsync(originalMistake);
        _mockPracticeRepo.Setup(x => x.GetByMistakeIdAsync(originalId)).ReturnsAsync(new List<MistakePracticeModel>());

        // Act
        var result = await _service.SplitMistakeAsync(originalId, newMistakes);

        // Assert
        result.Should().BeTrue();
        _mockOssService.Verify(x => x.DeleteManyAsync(originalMistake.ImagePaths), Times.Once);
        _mockMistakeRepo.Verify(x => x.Remove(originalMistake), Times.Once);

        _mockMistakeRepo.Verify(x => x.AddAsync(It.Is<MistakeModel>(m => m.Content == "Part 1")), Times.Once);
        _mockMistakeRepo.Verify(x => x.AddAsync(It.Is<MistakeModel>(m => m.Content == "Part 2")), Times.Once);

        // Verify that 2 logs were created for the 2 new mistakes
        _mockLogRepo.Verify(x => x.AddAsync(It.IsAny<ProcessLogModel>()), Times.Exactly(2));

        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }
}