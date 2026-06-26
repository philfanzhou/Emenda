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
using Ruoyu.Study.Mistake.Domain.Errors;
using Ruoyu.Study.Mistake.Service.Services;
using Ruoyu.Study.Student.Contract.Protos;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for the async ReanalyzeMistakeItem gRPC handler. The endpoint no longer
/// calls VL inline; it enqueues a job and returns jobId. The VL execution is
/// covered by ReanalyzeJobServiceTests.
/// </summary>
public class ReanalyzeMistakeItemTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<IReanalyzeJobService> _mockReanalyzeJobService;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IOssService> _mockOssService;
    private readonly Mock<IVlAnalyzer> _mockVlAnalyzer;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly MistakeServiceImpl _service;

    public ReanalyzeMistakeItemTests()
    {
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockReanalyzeJobService = new Mock<IReanalyzeJobService>();
        _mockStudentClient = new Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
        _mockOssService = new Mock<IOssService>();
        _mockVlAnalyzer = new Mock<IVlAnalyzer>();
        _mockLogger = new Mock<ILogger<MistakeServiceImpl>>();

        _service = new MistakeServiceImpl(
            _mockDomainService.Object,
            _mockReanalyzeJobService.Object,
            _mockStudentClient.Object,
            _mockOssService.Object,
            _mockVlAnalyzer.Object,
            _mockLogger.Object);
    }

    private static ServerCallContext NewContext() => TestServerCallContext.Create(
        "TestMethod", "localhost", DateTime.UtcNow.AddHours(1),
        new Metadata(), CancellationToken.None, "peer", null, null,
        _ => Task.CompletedTask, () => new WriteOptions(), _ => { });

    [Fact]
    public async Task ReanalyzeMistakeItem_EmptyId_ThrowsInvalidArgument()
    {
        var request = new ReanalyzeMistakeItemRequest { Id = "", ReviewerId = "t1", StudentId = "s1" };

        var act = () => _service.ReanalyzeMistakeItem(request, NewContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_InvalidGuid_ThrowsInvalidArgument()
    {
        var request = new ReanalyzeMistakeItemRequest { Id = "not-a-guid", ReviewerId = "t1", StudentId = "s1" };

        var act = () => _service.ReanalyzeMistakeItem(request, NewContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_EmptyReviewerId_ThrowsInvalidArgument()
    {
        var request = new ReanalyzeMistakeItemRequest { Id = Guid.NewGuid().ToString(), ReviewerId = "", StudentId = "s1" };

        var act = () => _service.ReanalyzeMistakeItem(request, NewContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_EmptyStudentId_ThrowsInvalidArgument()
    {
        var request = new ReanalyzeMistakeItemRequest { Id = Guid.NewGuid().ToString(), ReviewerId = "t1", StudentId = "" };

        var act = () => _service.ReanalyzeMistakeItem(request, NewContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_NotFoundFromService_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _mockReanalyzeJobService
            .Setup(s => s.StartJobAsync(id, "t1", "s1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.NotFound,
                ErrorMessage = "错题不存在"
            });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1", StudentId = "s1" };

        var act = () => _service.ReanalyzeMistakeItem(request, NewContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Contain("错题不存在");
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_FailedPrecondition_ReturnsSuccessFalse()
    {
        var id = Guid.NewGuid();
        _mockReanalyzeJobService
            .Setup(s => s.StartJobAsync(id, "t1", "s1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartReanalyzeJobResult
            {
                Success = false,
                ErrorCode = ReanalyzeJobErrorCode.FailedPrecondition,
                ErrorMessage = "只能对 PendingReview 或 Confirmed 状态的错题重新分析"
            });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1", StudentId = "s1" };

        var response = await _service.ReanalyzeMistakeItem(request, NewContext());

        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Contain("PendingReview 或 Confirmed");
        response.JobId.Should().BeEmpty();
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_OnSuccess_ReturnsJobId()
    {
        var id = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        _mockReanalyzeJobService
            .Setup(s => s.StartJobAsync(id, "t1", "s1", "note", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartReanalyzeJobResult
            {
                Success = true,
                Job = new ReanalyzeJobModel
                {
                    Id = jobId,
                    ItemId = id,
                    ReviewerId = "t1",
                    StudentId = "s1",
                    TeacherDescription = "note",
                    Status = ReanalyzeJobState.Pending,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

        var request = new ReanalyzeMistakeItemRequest
        {
            Id = id.ToString(),
            ReviewerId = "t1",
            StudentId = "s1",
            TeacherDescription = "note"
        };

        var response = await _service.ReanalyzeMistakeItem(request, NewContext());

        response.Success.Should().BeTrue();
        response.JobId.Should().Be(jobId.ToString());
        // The legacy Item field is no longer set on async path
    }

    [Fact]
    public async Task ReanalyzeMistakeItem_DoesNotCallVlInline()
    {
        var id = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        _mockReanalyzeJobService
            .Setup(s => s.StartJobAsync(id, "t1", "s1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartReanalyzeJobResult
            {
                Success = true,
                Job = new ReanalyzeJobModel
                {
                    Id = jobId,
                    ItemId = id,
                    ReviewerId = "t1",
                    StudentId = "s1",
                    Status = ReanalyzeJobState.Pending,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

        var request = new ReanalyzeMistakeItemRequest { Id = id.ToString(), ReviewerId = "t1", StudentId = "s1" };
        await _service.ReanalyzeMistakeItem(request, NewContext());

        _mockVlAnalyzer.Verify(
            v => v.ReAnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
