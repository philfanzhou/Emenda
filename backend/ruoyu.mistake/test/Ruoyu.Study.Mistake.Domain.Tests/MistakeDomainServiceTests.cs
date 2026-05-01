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
    private readonly Mock<IMistakeDetailRepository> _mockDetailRepo;
    private readonly Mock<IMistakePracticeRepository> _mockPracticeRepo;
    private readonly Mock<IProcessLogRepository> _mockLogRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IOssService> _mockOssService;
    private readonly MistakeDomainService _service;

    public MistakeDomainServiceTests()
    {
        _mockMistakeRepo = new Mock<IMistakeRepository>();
        _mockDetailRepo = new Mock<IMistakeDetailRepository>();
        _mockPracticeRepo = new Mock<IMistakePracticeRepository>();
        _mockLogRepo = new Mock<IProcessLogRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockOssService = new Mock<IOssService>();

        _service = new MistakeDomainService(
            _mockMistakeRepo.Object,
            _mockDetailRepo.Object,
            _mockPracticeRepo.Object,
            _mockLogRepo.Object,
            _mockUow.Object,
            _mockOssService.Object);
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
        var expectedPath = "mistakes/2026/04/14/uuid/m_uuid_0.jpg";

        _mockOssService.Setup(x => x.UploadAsync(
                It.IsAny<byte[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<OssBucket>()))
            .ReturnsAsync(expectedPath);

        var (resultMistake, resultDetail) = await _service.UploadMistakeAsync(mistake, images);

        resultMistake.Should().NotBeNull();
        resultMistake.Id.Should().NotBeEmpty();
        resultMistake.Status.Should().Be(1);
        resultDetail.Should().NotBeNull();
        resultDetail.OriginalImages.Should().ContainSingle().Which.Should().Be(expectedPath);

        _mockMistakeRepo.Verify(x => x.AddAsync(It.Is<MistakeModel>(m => m == resultMistake)), Times.Once);
        _mockDetailRepo.Verify(x => x.AddAsync(It.Is<MistakeDetailModel>(d => d.MistakeId == resultMistake.Id)), Times.Once);
        _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(l => l.MistakeId == resultMistake.Id && l.Action == 1)), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteMistakeAsync_WhenMistakeIsPending_ShouldDeleteImagesAndRemoveFromDb()
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

        _mockMistakeRepo.Setup(x => x.GetByIdAsync(mistakeId)).ReturnsAsync(mistake);
        _mockDetailRepo.Setup(x => x.GetByMistakeIdAsync(mistakeId)).ReturnsAsync(detail);
        _mockOssService.Setup(x => x.DeleteManyAsync(detail.OriginalImages)).ReturnsAsync(2);

        var result = await _service.DeleteMistakeAsync(mistakeId);

        result.Should().BeTrue();
        _mockOssService.Verify(x => x.DeleteManyAsync(detail.OriginalImages), Times.Once);
        _mockDetailRepo.Verify(x => x.DeleteByMistakeIdAsync(mistakeId), Times.Once);
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
        _mockOssService.Verify(x => x.DeleteManyAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
        _mockMistakeRepo.Verify(x => x.Remove(It.IsAny<MistakeModel>()), Times.Never);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Never);
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

        var newMistake1 = new MistakeModel { Subject = 1, Grade = 1 };
        var newMistake2 = new MistakeModel { Subject = 1, Grade = 1 };
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

        await Task.Delay(200);
        _mockOssService.Verify(x => x.DeleteManyAsync(It.Is<IEnumerable<string>>(l => l.Contains("original.jpg"))), Times.AtLeastOnce);
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
}
