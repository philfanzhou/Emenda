using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Mistake.Domain.Errors;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Mistake.Domain.Tests;

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

    // ===== MarkRunningAsync =====

    [Fact]
    public async Task MarkRunningAsync_TransitionsPendingToRunning_SetsStartedAt()
    {
        var job = new ReanalyzeJobModel { Id = Guid.NewGuid(), Status = ReanalyzeJobState.Pending };
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var ok = await _service.MarkRunningAsync(job.Id);

        ok.Should().BeTrue();
        _mockJobRepo.Verify(r => r.UpdateStatusAsync(
            It.Is<ReanalyzeJobModel>(j => j.Status == ReanalyzeJobState.Running && j.StartedAt != null),
            It.IsAny<CancellationToken>()), Times.Once);
        // MarkRunningAsync is a single-entity write that commits immediately
        // so subsequent claim cycles see the row as Running (not Pending).
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkRunningAsync_WithNonPendingJob_ReturnsFalse()
    {
        var job = new ReanalyzeJobModel { Id = Guid.NewGuid(), Status = ReanalyzeJobState.Completed };
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var ok = await _service.MarkRunningAsync(job.Id);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task MarkRunningAsync_WithMissingJob_ReturnsFalse()
    {
        _mockJobRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReanalyzeJobModel?)null);
        var ok = await _service.MarkRunningAsync(Guid.NewGuid());
        ok.Should().BeFalse();
    }

    // ===== MarkCompletedAsync =====

    [Fact]
    public async Task MarkCompletedAsync_TransitionsRunningToCompleted_UpdatesRootCauseAndLog()
    {
        var itemId = Guid.NewGuid();
        var job = new ReanalyzeJobModel
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Status = ReanalyzeJobState.Running,
            PreviousRootCause = "old",
            TeacherDescription = "note"
        };
        var item = BuildItem(itemId, rootCause: "old");
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        // The "deferred" semantics: the mock mutates the entity in place to
        // mimic what EF Core would do at SaveChanges time. This is what
        // production would observe.
        _mockItemRepo
            .Setup(r => r.SetRootCauseDeferredAsync(itemId, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, DateTimeOffset, CancellationToken>((_, newRoot, updatedAt, _) =>
            {
                item.RootCause = newRoot;
                item.UpdatedAt = updatedAt;
            })
            .Returns(Task.CompletedTask);

        var ok = await _service.MarkCompletedAsync(job.Id, "new", 1234);

        ok.Should().BeTrue();
        item.RootCause.Should().Be("new");
        // MarkCompletedAsync uses deferred update + single SaveChanges to keep
        // item + job + process_log atomic.
        _mockItemRepo.Verify(r => r.SetRootCauseDeferredAsync(itemId, "new", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockJobRepo.Verify(r => r.UpdateStatusAsync(
            It.Is<ReanalyzeJobModel>(j => j.Status == ReanalyzeJobState.Completed
                && j.NewRootCause == "new"
                && j.VlDurationMs == 1234
                && j.CompletedAt != null),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockLogRepo.Verify(r => r.AddAsync(
            It.Is<ProcessLogModel>(p => p.Action == (int)ProcessAction.Reanalyzed && p.Details!.Contains("new")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkCompletedAsync_WhenItemDeleted_MarksJobFailed()
    {
        var itemId = Guid.NewGuid();
        var job = new ReanalyzeJobModel { Id = Guid.NewGuid(), ItemId = itemId, Status = ReanalyzeJobState.Running };
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync((MistakeItemModel?)null);

        var ok = await _service.MarkCompletedAsync(job.Id, "new", 1000);

        ok.Should().BeFalse();
        _mockJobRepo.Verify(r => r.UpdateStatusAsync(
            It.Is<ReanalyzeJobModel>(j => j.Status == ReanalyzeJobState.Failed && j.ErrorMessage == "错题已删除"),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockLogRepo.Verify(r => r.AddAsync(It.IsAny<ProcessLogModel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkCompletedAsync_DoesNotChangeReviewStatus()
    {
        var itemId = Guid.NewGuid();
        var job = new ReanalyzeJobModel { Id = Guid.NewGuid(), ItemId = itemId, Status = ReanalyzeJobState.Running };
        var item = BuildItem(itemId, reviewStatus: (int)ReviewStatus.Confirmed);
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        await _service.MarkCompletedAsync(job.Id, "new", 1000);
        item.ReviewStatus.Should().Be((int)ReviewStatus.Confirmed);
    }

    [Fact]
    public async Task MarkCompletedAsync_WithNonRunningJob_ReturnsFalse()
    {
        var job = new ReanalyzeJobModel { Id = Guid.NewGuid(), Status = ReanalyzeJobState.Pending };
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var ok = await _service.MarkCompletedAsync(job.Id, "x", 0);
        ok.Should().BeFalse();
    }

    // ===== MarkFailedAsync =====

    [Fact]
    public async Task MarkFailedAsync_DoesNotChangeRootCause_WritesLog()
    {
        var itemId = Guid.NewGuid();
        var job = new ReanalyzeJobModel
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Status = ReanalyzeJobState.Running,
            PreviousRootCause = "old"
        };
        var item = BuildItem(itemId, rootCause: "old");
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var ok = await _service.MarkFailedAsync(job.Id, "VL 500");

        ok.Should().BeTrue();
        item.RootCause.Should().Be("old");
        _mockJobRepo.Verify(r => r.UpdateStatusAsync(
            It.Is<ReanalyzeJobModel>(j => j.Status == ReanalyzeJobState.Failed && j.ErrorMessage == "VL 500"),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockLogRepo.Verify(r => r.AddAsync(
            It.Is<ProcessLogModel>(p => p.Details!.Contains("FAILED: VL 500")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ===== Atomicity: single SaveChanges for the multi-write path =====

    [Fact]
    public async Task MarkCompletedAsync_CommitsAllThreeTablesInOneSaveChanges()
    {
        var itemId = Guid.NewGuid();
        var job = new ReanalyzeJobModel
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Status = ReanalyzeJobState.Running,
            PreviousRootCause = "old"
        };
        var item = BuildItem(itemId, rootCause: "old");
        _mockJobRepo.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _mockItemRepo
            .Setup(r => r.SetRootCauseDeferredAsync(itemId, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, DateTimeOffset, CancellationToken>((_, newRoot, updatedAt, _) =>
            {
                item.RootCause = newRoot;
                item.UpdatedAt = updatedAt;
            })
            .Returns(Task.CompletedTask);

        await _service.MarkCompletedAsync(job.Id, "new", 1000);

        // Exactly ONE SaveChanges for the multi-table write path.
        // (MarkRunningAsync may also call SaveChanges but it runs first
        // before the test's MarkCompleted path.)
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
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

    // ===== Crash recovery =====

    [Fact]
    public async Task RecoverStaleRunningJobsAsync_MarksAllRunningAsFailed()
    {
        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();
        var stale1 = new ReanalyzeJobModel { Id = Guid.NewGuid(), Status = ReanalyzeJobState.Running, ItemId = itemId1 };
        var stale2 = new ReanalyzeJobModel { Id = Guid.NewGuid(), Status = ReanalyzeJobState.Running, ItemId = itemId2 };
        _mockJobRepo.Setup(r => r.GetStaleRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReanalyzeJobModel> { stale1, stale2 });
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildItem(itemId1));
        _mockItemRepo.Setup(r => r.GetByIdAsync(itemId2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildItem(itemId2));

        var count = await _service.RecoverStaleRunningJobsAsync();

        count.Should().Be(2);
        _mockJobRepo.Verify(r => r.UpdateStatusAsync(
            It.Is<ReanalyzeJobModel>(j => j.Status == ReanalyzeJobState.Failed && j.ErrorMessage!.Contains("服务重启")),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RecoverStaleRunningJobsAsync_WithNone_ReturnsZero()
    {
        _mockJobRepo.Setup(r => r.GetStaleRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReanalyzeJobModel>());

        var count = await _service.RecoverStaleRunningJobsAsync();
        count.Should().Be(0);
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
