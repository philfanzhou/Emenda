using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Emenda.Domain.Errors;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;
using Emenda.Domain.Services;
using Xunit;

namespace Emenda.Domain.Tests;

public class ReanalyzeJobServiceTests
{
    private readonly Mock<IReanalyzeJobRepository> _mockJobRepo;
    private readonly Mock<IMistakeItemRepository> _mockItemRepo;
    private readonly Mock<IProcessLogRepository> _mockLogRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ILogger<ReanalyzeJobService>> _mockLogger;
    private readonly ReanalyzeJobService _service;

    public ReanalyzeJobServiceTests()
    {
        _mockJobRepo = new Mock<IReanalyzeJobRepository>();
        _mockItemRepo = new Mock<IMistakeItemRepository>();
        _mockLogRepo = new Mock<IProcessLogRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<ReanalyzeJobService>>();

        _service = new ReanalyzeJobService(
            _mockJobRepo.Object,
            _mockItemRepo.Object,
            _mockLogRepo.Object,
            _mockUow.Object,
            _mockLogger.Object);
    }

    private static MistakeItemModel BuildItem(Guid id, int reviewStatus = (int)ReviewStatus.PendingReview, string? rootCause = "旧根因", int regionCount = 1)
    {
        var regions = Enumerable.Range(0, regionCount)
            .Select(i => new SourceRegionModel { SourceImagePath = $"uploads/img{i}.jpg" })
            .ToList();
        return new MistakeItemModel
        {
            Id = id,
            StudentId = "student-1",
            Subject = 1,
            Grade = 7,
            SourceUploadId = "upload-1",
            SourceRegions = regions,
            ReviewStatus = reviewStatus,
            RootCause = rootCause,
        };
    }

    // ===== StartJobAsync: validation paths =====

    [Fact]
    public async Task StartJobAsync_WithEmptyItemId_ReturnsInvalidArgument()
    {
        var result = await _service.StartJobAsync(Guid.Empty, "t1", "s1", null);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ReanalyzeJobErrorCode.InvalidArgument);
        result.ErrorMessage.Should().Contain("错题 ID 不能为空");
        _mockJobRepo.Verify(r => r.AddAsync(It.IsAny<ReanalyzeJobModel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartJobAsync_WithEmptyReviewerId_ReturnsInvalidArgument()
    {
        var id = Guid.NewGuid();
        var result = await _service.StartJobAsync(id, " ", "s1", null);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ReanalyzeJobErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task StartJobAsync_WithEmptyStudentId_ReturnsInvalidArgument()
    {
        var id = Guid.NewGuid();
        var result = await _service.StartJobAsync(id, "t1", "", null);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ReanalyzeJobErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task StartJobAsync_WithNonExistentItem_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _mockItemRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MistakeItemModel?)null);

        var result = await _service.StartJobAsync(id, "t1", "s1", null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ReanalyzeJobErrorCode.NotFound);
        result.ErrorMessage.Should().Be("错题不存在");
    }

    [Fact]
    public async Task StartJobAsync_WithRejectedItem_ReturnsFailedPrecondition()
    {
        var id = Guid.NewGuid();
        _mockItemRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildItem(id, reviewStatus: (int)ReviewStatus.Rejected));

        var result = await _service.StartJobAsync(id, "t1", "s1", null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ReanalyzeJobErrorCode.FailedPrecondition);
        result.ErrorMessage.Should().Contain("PendingReview 或 Confirmed");
    }

    [Fact]
    public async Task StartJobAsync_WithEmptySourceRegions_ReturnsFailedPrecondition()
    {
        var id = Guid.NewGuid();
        _mockItemRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildItem(id, regionCount: 0));

        var result = await _service.StartJobAsync(id, "t1", "s1", null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ReanalyzeJobErrorCode.FailedPrecondition);
        result.ErrorMessage.Should().Be("错题无图片，无法重新分析");
    }

    [Fact]
    public async Task StartJobAsync_WithValidItem_CreatesPendingJob()
    {
        var id = Guid.NewGuid();
        _mockItemRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildItem(id, rootCause: "v1"));

        var result = await _service.StartJobAsync(id, "t1", "s1", "note");

        result.Success.Should().BeTrue();
        result.Job.Should().NotBeNull();
        result.Job!.Status.Should().Be(ReanalyzeJobState.Pending);
        result.Job.TeacherDescription.Should().Be("note");
        result.Job.PreviousRootCause.Should().Be("v1");
        result.Job.ReviewerId.Should().Be("t1");
        result.Job.StudentId.Should().Be("s1");
        result.Job.ItemId.Should().Be(id);
        _mockJobRepo.Verify(r => r.AddAsync(It.IsAny<ReanalyzeJobModel>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartJobAsync_WithEmptyNote_NormalizesToNull()
    {
        var id = Guid.NewGuid();
        _mockItemRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildItem(id));

        var result = await _service.StartJobAsync(id, "t1", "s1", "   ");

        result.Success.Should().BeTrue();
        result.Job!.TeacherDescription.Should().BeNull();
    }

    [Fact]
    public async Task StartJobAsync_SnapshotsPreviousRootCause()
    {
        var id = Guid.NewGuid();
        var item = BuildItem(id, rootCause: "v1");
        _mockItemRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var result = await _service.StartJobAsync(id, "t1", "s1", null);

        result.Job!.PreviousRootCause.Should().Be("v1");
    }

    [Fact]
    public async Task ClaimJobsAsync_ClampsAtTwoAndDelegatesAtomicClaim()
    {
        _mockJobRepo.Setup(r => r.ClaimAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReanalyzeJobModel> { new(), new() });
        var jobs = await _service.ClaimJobsAsync(50);
        jobs.Should().HaveCount(2);
        _mockJobRepo.Verify(r => r.ClaimAsync(2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TerminalAndHeartbeatForwardOwnerAndVersion()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        _mockJobRepo.Setup(r => r.RenewAsync(id, owner, 3, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockJobRepo.Setup(r => r.CompleteAsync(id, owner, 3, "new", 12, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockJobRepo.Setup(r => r.FailAsync(id, owner, 3, "vl_failed", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await _service.RenewLeaseAsync(id, owner, 3)).Should().BeTrue();
        (await _service.MarkCompletedAsync(id, owner, 3, "new", 12)).Should().BeTrue();
        (await _service.MarkFailedAsync(id, owner, 3, "vl_failed")).Should().BeTrue();
    }

    // ===== Cross-teacher isolation =====

    [Fact]
    public async Task GetJobsAsync_FiltersByReviewerId()
    {
        var jobA = new ReanalyzeJobModel { Id = Guid.NewGuid(), ReviewerId = "t1" };
        var jobB = new ReanalyzeJobModel { Id = Guid.NewGuid(), ReviewerId = "t2" };
        _mockJobRepo.Setup(r => r.GetByIdsAsync(It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReanalyzeJobModel> { jobA, jobB });

        var result = await _service.GetJobsAsync("t1", new List<Guid> { jobA.Id, jobB.Id });
        result.Should().HaveCount(1);
        result[0].ReviewerId.Should().Be("t1");
    }

    [Fact]
    public async Task GetActiveJobsAsync_DelegatesToRepository()
    {
        var pending = new ReanalyzeJobModel { Id = Guid.NewGuid(), ReviewerId = "t1", Status = ReanalyzeJobState.Pending };
        var running = new ReanalyzeJobModel { Id = Guid.NewGuid(), ReviewerId = "t1", Status = ReanalyzeJobState.Running };
        _mockJobRepo.Setup(r => r.GetActiveByReviewerAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReanalyzeJobModel> { pending, running });

        var result = await _service.GetActiveJobsAsync("t1");
        result.Should().HaveCount(2);
    }

    // ===== Cleanup =====

    [Fact]
    public async Task DeleteExpiredJobsAsync_PassesCutoffToRepository()
    {
        _mockJobRepo.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var count = await _service.DeleteExpiredJobsAsync(cutoff);
        count.Should().Be(5);
    }
}
