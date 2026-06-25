using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Services;
using Ruoyu.Study.Student.Contract.Protos;
using Xunit;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for ReanalyzeMistakeItem gRPC handler covering validation, success,
/// VL failure, and the rule that root_cause is never updated on VL failure.
/// </summary>
public class ReanalyzeMistakeItemTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IOssService> _mockOssService;
    private readonly Mock<IVlAnalyzer> _mockVlAnalyzer;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly MistakeServiceImpl _service;

    public ReanalyzeMistakeItemTests()
    {
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockStudentClient = new Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
        _mockOssService = new Mock<IOssService>();
        _mockVlAnalyzer = new Mock<IVlAnalyzer>();
        _mockLogger = new Mock<ILogger<MistakeServiceImpl>>();

        _service = new MistakeServiceImpl(
            _mockDomainService.Object,
            _mockStudentClient.Object,
            _mockOssService.Object,
            _mockVlAnalyzer.Object,
            _mockLogger.Object);
    }

    private static MistakeItemModel BuildItem(Guid id, string studentId, string previousRootCause, int reviewStatus = (int)ReviewStatus.PendingReview)
    {
        return new MistakeItemModel
        {
            Id = id,
            StudentId = studentId,
            Subject = 1,
            Grade = 7,
            SourceUploadId = "upload-1",
            SourceRegions = new List<SourceRegionModel>
            {
                new SourceRegionModel { SourceImagePath = "uploads/img1.jpg" }
            },
            ReviewStatus = reviewStatus,
            RootCause = previousRootCause
        };
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_EmptyId_ThrowsInvalidArgument()
    {
        var request = new ReanalyzeMistakeItemRequest { Id = "", ReviewerId = "t1" };

        var act = () => _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create(
            "TestMethod", "localhost", DateTime.UtcNow.AddHours(1),
            new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_InvalidGuid_ThrowsInvalidArgument()
    {
        var request = new ReanalyzeMistakeItemRequest { Id = "not-a-guid", ReviewerId = "t1" };

        var act = () => _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create(
            "TestMethod", "localhost", DateTime.UtcNow.AddHours(1),
            new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_NonExistentId_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = false, ErrorMessage = "错题不存在" });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1" };

        var act = () => _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create(
            "TestMethod", "localhost", DateTime.UtcNow.AddHours(1),
            new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_RejectedStatus_ReturnsFailedWithMessage()
    {
        var id = Guid.NewGuid();
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult
            {
                Success = false,
                ErrorMessage = "只能对 PendingReview 或 Confirmed 状态的错题重新分析"
            });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1" };

        var response = await _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Contain("只能对 PendingReview 或 Confirmed");
        _mockVlAnalyzer.Verify(
            v => v.ReAnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_NoImages_ReturnsFailed()
    {
        var id = Guid.NewGuid();
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = false, ErrorMessage = "错题无图片，无法重新分析" });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1" };

        var response = await _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        response.Success.Should().BeFalse();
        _mockVlAnalyzer.Verify(
            v => v.ReAnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_OnSuccess_UpdatesRootCauseAndReturnsDto()
    {
        var id = Guid.NewGuid();
        var item = BuildItem(id, "student-1", "原根因");
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", "教师备注", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = true, Item = item });
        _mockVlAnalyzer
            .Setup(v => v.ReAnalyzeAsync("student-1", It.IsAny<List<string>>(), "原根因", "教师备注", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlReanalysisResult { RootCause = "AI 重新生成的根因" });

        var request = new ReanalyzeMistakeItemRequest
        {
            Id = id.ToString(),
            ReviewerId = "t1",
            TeacherDescription = "教师备注"
        };

        var response = await _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        response.Success.Should().BeTrue();
        response.Item.Should().NotBeNull();
        response.Item.Id.Should().Be(id.ToString());
        response.Item.RootCause.Should().Be("AI 重新生成的根因");
        item.RootCause.Should().Be("AI 重新生成的根因", "Domain layer's item should be mutated after successful VL");
        _mockDomainService.Verify(
            s => s.PersistReanalyzedRootCauseAsync(item, "原根因", "t1", "教师备注", "AI 重新生成的根因", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_OnSuccess_WithEmptyTeacherNote_PassesNull()
    {
        var id = Guid.NewGuid();
        var item = BuildItem(id, "student-1", "原根因");
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = true, Item = item });
        _mockVlAnalyzer
            .Setup(v => v.ReAnalyzeAsync("student-1", It.IsAny<List<string>>(), "原根因", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlReanalysisResult { RootCause = "新根因" });

        var request = new ReanalyzeMistakeItemRequest
        {
            Id = id.ToString(),
            ReviewerId = "t1",
            TeacherDescription = "" // empty string
        };

        var response = await _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        response.Success.Should().BeTrue();
        // empty string is normalized to null
        _mockDomainService.Verify(
            s => s.PersistReanalyzedRootCauseAsync(item, "原根因", "t1", It.IsAny<string?>(), "新根因", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_OnVlFailure_KeepsOriginalRootCauseAndThrowsInternal()
    {
        var id = Guid.NewGuid();
        var item = BuildItem(id, "student-1", "原根因");
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", "备注", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = true, Item = item });
        _mockVlAnalyzer
            .Setup(v => v.ReAnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("VL API timeout"));

        var request = new ReanalyzeMistakeItemRequest
        {
            Id = id.ToString(),
            ReviewerId = "t1",
            TeacherDescription = "备注"
        };

        var act = () => _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.Internal);
        // root_cause should NOT be modified on VL failure
        item.RootCause.Should().Be("原根因", "VL failure must not mutate root_cause");
        // LogReanalyzeFailureAsync is called for audit
        _mockDomainService.Verify(
            s => s.LogReanalyzeFailureAsync(item, "t1", "备注", "VL API timeout", It.IsAny<CancellationToken>()),
            Times.Once);
        // PersistReanalyzedRootCauseAsync should NOT be called
        _mockDomainService.Verify(
            s => s.PersistReanalyzedRootCauseAsync(It.IsAny<MistakeItemModel>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_OnVlSkipped_ReturnsFailureMessage()
    {
        var id = Guid.NewGuid();
        var item = BuildItem(id, "student-1", "原根因");
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = true, Item = item });
        _mockVlAnalyzer
            .Setup(v => v.ReAnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlReanalysisResult { Skipped = true });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1" };

        var response = await _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Contain("AI 未返回根因");
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_DoesNotChangeReviewStatus()
    {
        var id = Guid.NewGuid();
        var item = BuildItem(id, "student-1", "原根因", reviewStatus: (int)ReviewStatus.Confirmed);
        _mockDomainService
            .Setup(s => s.ReanalyzeMistakeItemAsync(id, "t1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MistakeReanalysisResult { Success = true, Item = item });
        _mockVlAnalyzer
            .Setup(v => v.ReAnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlReanalysisResult { RootCause = "新根因" });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1" };

        await _service.ReanalyzeMistakeItem(request, TestServerCallContext.Create("TestMethod", "localhost", DateTime.UtcNow.AddHours(1), new Metadata(), CancellationToken.None, "peer", null, null, _ => Task.CompletedTask, () => new WriteOptions(), _ => { }));

        // review_status must remain Confirmed (not reset to PendingReview)
        item.ReviewStatus.Should().Be((int)ReviewStatus.Confirmed);
    }
}
