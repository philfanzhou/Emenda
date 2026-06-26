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
using Ruoyu.Study.Student.Contract.Protos;
using Xunit;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;
using MistakeIdRequest = Ruoyu.Study.Mistake.Contract.Protos.IdRequest;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for MistakeServiceImpl covering the remaining RPC methods not covered by
/// MistakeServiceImplAdditionalTests or MistakeServiceImplOssMigrationTests:
/// - SubmitMistakeUpload
/// - GetMistakeItem
/// - GetMistakeItemList
/// - GetPendingReviewUploads
/// - AddMistakeItem
/// - DeleteMistakeItem
/// - AddMistakePractice
/// - GetMistakePractices
/// - LogMistakeProcess
/// - GetMistakeProcessHistory
/// </summary>
public class MistakeServiceImplRpcTests
{
    private readonly Mock<IMistakeDomainService> _mockDomainService;
    private readonly Mock<IReanalyzeJobService> _mockReanalyzeJobService;
    private readonly Mock<ILogger<MistakeServiceImpl>> _mockLogger;
    private readonly Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient> _mockStudentClient;
    private readonly Mock<IOssService> _mockOssService;
    private readonly Mock<Ruoyu.Study.Mistake.Service.Services.IVlAnalyzer> _mockVlAnalyzer;
    private readonly MistakeServiceImpl _service;

    public MistakeServiceImplRpcTests()
    {
        _mockDomainService = new Mock<IMistakeDomainService>();
        _mockReanalyzeJobService = new Mock<IReanalyzeJobService>();
        _mockLogger = new Mock<ILogger<MistakeServiceImpl>>();
        _mockStudentClient = new Mock<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();
        _mockOssService = new Mock<IOssService>();
        _mockVlAnalyzer = new Mock<Ruoyu.Study.Mistake.Service.Services.IVlAnalyzer>();

        _service = new MistakeServiceImpl(
            _mockDomainService.Object,
            _mockReanalyzeJobService.Object,
            _mockStudentClient.Object,
            _mockOssService.Object,
            _mockVlAnalyzer.Object,
            _mockLogger.Object);
    }

    private static ServerCallContext CreateTestContext()
    {
        return TestServerCallContext.Create(
            "TestMethod",
            "localhost",
            DateTime.UtcNow.AddHours(1),
            new Metadata(),
            CancellationToken.None,
            "peer",
            null,
            null,
            _ => Task.CompletedTask,
            () => new WriteOptions(),
            _ => { });
    }

    private static MistakeItemModel CreateSampleItem(Guid? id = null, string studentId = "student-1",
        int subject = 3, int grade = 5, string sourceUploadId = "upload-1",
        int reviewStatus = 1, List<SourceRegionModel>? sourceRegions = null)
    {
        return new MistakeItemModel
        {
            Id = id ?? Guid.NewGuid(),
            StudentId = studentId,
            Subject = subject,
            Grade = grade,
            SourceUploadId = sourceUploadId,
            ReviewStatus = reviewStatus,
            SourceRegions = sourceRegions ?? new List<SourceRegionModel>
            {
                new() { SourceImagePath = "mistakes/test/image.jpg" }
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    #region SubmitMistakeUpload

    [Fact]
    public async Task SubmitMistakeUpload_WithEmptyStudentId_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "",
            Subject = 1,
            Grade = 1,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("学生ID");
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithWhitespaceStudentId_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "   ",
            Subject = 1,
            Grade = 1,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithSubjectZero_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 0,
            Grade = 1,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("学科");
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithSubjectTooHigh_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 10,
            Grade = 1,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithGradeZero_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 0,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("年级");
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithGradeTooHigh_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 13,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithEmptySourceUploadId_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 1,
            SourceUploadId = ""
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("上传记录ID");
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithNoImagePaths_ThrowsInvalidArgument()
    {
        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 1,
            SourceUploadId = "upload-1"
        };

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("图片");
    }

    [Fact]
    public async Task SubmitMistakeUpload_WithValidInput_ReturnsCreatedIds()
    {
        var item1 = CreateSampleItem(id: Guid.NewGuid());
        var item2 = CreateSampleItem(id: Guid.NewGuid());
        _mockDomainService
            .Setup(x => x.SubmitMistakeUploadAsync(
                "student-1", 3, 5,
                It.IsAny<List<string>>(),
                "root cause here",
                "upload-1"))
            .ReturnsAsync(new List<MistakeItemModel> { item1, item2 });

        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 3,
            Grade = 5,
            SourceUploadId = "upload-1",
            RootCause = "root cause here"
        };
        request.ImagePaths.Add("path1.jpg");
        request.ImagePaths.Add("path2.jpg");

        var response = await _service.SubmitMistakeUpload(request, CreateTestContext());

        response.Success.Should().BeTrue();
        response.CreatedItemIds.Should().HaveCount(2);
        response.CreatedItemIds.Should().Contain(item1.Id.ToString());
        response.CreatedItemIds.Should().Contain(item2.Id.ToString());
    }

    [Fact]
    public async Task SubmitMistakeUpload_WhenDomainValidationFails_ThrowsInvalidArgument()
    {
        _mockDomainService
            .Setup(x => x.SubmitMistakeUploadAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new MistakeValidationException("domain validation error"));

        var request = new SubmitMistakeUploadRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 1,
            SourceUploadId = "upload-1"
        };
        request.ImagePaths.Add("path1.jpg");

        var act = () => _service.SubmitMistakeUpload(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Be("domain validation error");
    }

    #endregion

    #region GetMistakeItem

    [Fact]
    public async Task GetMistakeItem_WhenItemExists_ReturnsMappedDto()
    {
        var itemId = Guid.NewGuid();
        var item = CreateSampleItem(id: itemId, studentId: "s1", subject: 2, grade: 3);
        item.SourceRegions = new List<SourceRegionModel>
        {
            new()
            {
                SourceImagePath = "uploads/img1.jpg",
                BoundingBox = new BoundingBoxModel { X1 = 10, Y1 = 20, X2 = 100, Y2 = 200 }
            }
        };
        _mockDomainService.Setup(x => x.GetMistakeItemAsync(itemId)).ReturnsAsync(item);

        var response = await _service.GetMistakeItem(new MistakeIdRequest { Id = itemId.ToString() }, CreateTestContext());

        response.Id.Should().Be(itemId.ToString());
        response.StudentId.Should().Be("s1");
        response.Subject.Should().Be(2);
        response.Grade.Should().Be(3);
        response.SourceRegions.Should().HaveCount(1);
        response.SourceRegions[0].SourceImagePath.Should().Be("uploads/img1.jpg");
        response.SourceRegions[0].BoundingBox.Should().NotBeNull();
        response.SourceRegions[0].BoundingBox.X1.Should().Be(10);
        response.SourceRegions[0].BoundingBox.Y2.Should().Be(200);
    }

    [Fact]
    public async Task GetMistakeItem_WhenItemNotFound_ThrowsNotFound()
    {
        var itemId = Guid.NewGuid();
        _mockDomainService.Setup(x => x.GetMistakeItemAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

        var act = () => _service.GetMistakeItem(new MistakeIdRequest { Id = itemId.ToString() }, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Contain("不存在");
    }

    [Fact]
    public async Task GetMistakeItem_WithInvalidGuid_ThrowsFormatException()
    {
        var act = () => _service.GetMistakeItem(new MistakeIdRequest { Id = "not-a-guid" }, CreateTestContext());

        await act.Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public async Task GetMistakeItem_WithNullReviewedAt_ReturnsEmptyString()
    {
        var item = CreateSampleItem();
        item.ReviewedAt = null;
        _mockDomainService.Setup(x => x.GetMistakeItemAsync(item.Id)).ReturnsAsync(item);

        var response = await _service.GetMistakeItem(new MistakeIdRequest { Id = item.Id.ToString() }, CreateTestContext());

        response.ReviewedAt.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMistakeItem_WithReviewedAt_ReturnsFormattedTimestamp()
    {
        var item = CreateSampleItem();
        item.ReviewedAt = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero);
        _mockDomainService.Setup(x => x.GetMistakeItemAsync(item.Id)).ReturnsAsync(item);

        var response = await _service.GetMistakeItem(new MistakeIdRequest { Id = item.Id.ToString() }, CreateTestContext());

        response.ReviewedAt.Should().Be("2026-06-15T10:30:00.000Z");
    }

    #endregion

    #region GetMistakeItemList

    [Fact]
    public async Task GetMistakeItemList_ReturnsPagedResultWithCorrectMetadata()
    {
        var items = new List<MistakeItemModel>
        {
            CreateSampleItem(id: Guid.NewGuid()),
            CreateSampleItem(id: Guid.NewGuid())
        };
        _mockDomainService
            .Setup(x => x.GetMistakeItemListAsync("student-1", 1, 5, 0, 2, 10))
            .ReturnsAsync((items, 25));

        var request = new GetMistakeItemListRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 5,
            ReviewStatus = 0,
            Page = 2,
            Size = 10
        };

        var response = await _service.GetMistakeItemList(request, CreateTestContext());

        response.Items.Should().HaveCount(2);
        response.PageMeta.Page.Should().Be(2);
        response.PageMeta.Size.Should().Be(10);
        response.PageMeta.TotalCount.Should().Be(25);
        response.PageMeta.TotalPages.Should().Be(3); // (25 + 10 - 1) / 10 = 3
    }

    [Fact]
    public async Task GetMistakeItemList_WithEmptyResult_ReturnsEmptyList()
    {
        _mockDomainService
            .Setup(x => x.GetMistakeItemListAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<MistakeItemModel>(), 0));

        var request = new GetMistakeItemListRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 5,
            ReviewStatus = 0,
            Page = 1,
            Size = 10
        };

        var response = await _service.GetMistakeItemList(request, CreateTestContext());

        response.Items.Should().BeEmpty();
        response.PageMeta.TotalCount.Should().Be(0);
        response.PageMeta.TotalPages.Should().Be(0);
    }

    [Fact]
    public async Task GetMistakeItemList_WithExactPageDivision_CalculatesTotalPagesCorrectly()
    {
        _mockDomainService
            .Setup(x => x.GetMistakeItemListAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<MistakeItemModel>(), 20));

        var request = new GetMistakeItemListRequest
        {
            StudentId = "student-1",
            Subject = 1,
            Grade = 5,
            ReviewStatus = 0,
            Page = 1,
            Size = 10
        };

        var response = await _service.GetMistakeItemList(request, CreateTestContext());

        response.PageMeta.TotalPages.Should().Be(2); // 20 / 10 = 2
    }

    #endregion

    #region GetPendingReviewUploads

    [Fact]
    public async Task GetPendingReviewUploads_WithoutSubjectsFilter_ReturnsAllSubjects()
    {
        var uploads = new List<PendingReviewUploadModel>
        {
            new()
            {
                SourceUploadId = "upload-1", StudentId = "s1", Subject = 1, Grade = 5,
                PendingCount = 3, TotalCount = 5,
                EarliestCreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
            }
        };
        _mockDomainService
            .Setup(x => x.GetPendingReviewUploadsAsync(1, 10, null))
            .ReturnsAsync((uploads, 1));

        var request = new GetPendingReviewUploadsRequest { Page = 1, Size = 10 };

        var response = await _service.GetPendingReviewUploads(request, CreateTestContext());

        response.Uploads.Should().HaveCount(1);
        response.Uploads[0].SourceUploadId.Should().Be("upload-1");
        response.Uploads[0].PendingCount.Should().Be(3);
        response.Uploads[0].EarliestCreatedAt.Should().Be("2026-06-01T00:00:00.000Z");
        response.PageMeta.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetPendingReviewUploads_WithSubjectsFilter_PassesFilterToDomain()
    {
        _mockDomainService
            .Setup(x => x.GetPendingReviewUploadsAsync(1, 10, It.IsAny<List<int>>()))
            .ReturnsAsync((new List<PendingReviewUploadModel>(), 0));

        var request = new GetPendingReviewUploadsRequest { Page = 1, Size = 10 };
        request.Subjects.Add(1);
        request.Subjects.Add(3);

        var response = await _service.GetPendingReviewUploads(request, CreateTestContext());

        response.Uploads.Should().BeEmpty();
        _mockDomainService.Verify(x => x.GetPendingReviewUploadsAsync(1, 10, It.Is<List<int>>(l => l.Count == 2 && l.Contains(1) && l.Contains(3))), Times.Once);
    }

    [Fact]
    public async Task GetPendingReviewUploads_WithMinDateTime_ReturnsEmptyString()
    {
        _mockDomainService
            .Setup(x => x.GetPendingReviewUploadsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<int>?>()))
            .ReturnsAsync((new List<PendingReviewUploadModel>
            {
                new()
                {
                    SourceUploadId = "upload-1", StudentId = "s1",
                    EarliestCreatedAt = DateTimeOffset.MinValue
                }
            }, 1));

        var request = new GetPendingReviewUploadsRequest { Page = 1, Size = 10 };

        var response = await _service.GetPendingReviewUploads(request, CreateTestContext());

        response.Uploads[0].EarliestCreatedAt.Should().BeEmpty();
    }

    #endregion

    #region AddMistakeItem

    [Fact]
    public async Task AddMistakeItem_WithValidInput_ReturnsCreatedDto()
    {
        var createdItem = CreateSampleItem(id: Guid.NewGuid(), studentId: "s1", subject: 2, grade: 3);
        createdItem.SourceRegions = new List<SourceRegionModel>
        {
            new()
            {
                SourceImagePath = "uploads/img.jpg",
                BoundingBox = new BoundingBoxModel { X1 = 1, Y1 = 2, X2 = 3, Y2 = 4 }
            }
        };
        _mockDomainService
            .Setup(x => x.AddMistakeItemAsync(It.Is<MistakeItemModel>(m =>
                m.StudentId == "s1" && m.Subject == 2 && m.Grade == 3)))
            .ReturnsAsync(createdItem);

        var request = new AddMistakeItemRequest
        {
            StudentId = "s1",
            Subject = 2,
            Grade = 3,
            SourceUploadId = "upload-1"
        };
        var region = new Ruoyu.Study.Mistake.Contract.Protos.SourceRegion
        {
            SourceImagePath = "uploads/img.jpg",
            BoundingBox = new BoundingBox { X1 = 1, Y1 = 2, X2 = 3, Y2 = 4 }
        };
        request.SourceRegions.Add(region);

        var response = await _service.AddMistakeItem(request, CreateTestContext());

        response.Id.Should().Be(createdItem.Id.ToString());
        response.StudentId.Should().Be("s1");
        response.SourceRegions.Should().HaveCount(1);
        response.SourceRegions[0].BoundingBox.X1.Should().Be(1);
    }

    [Fact]
    public async Task AddMistakeItem_WithNullBoundingBox_MapsToNullBoundingBox()
    {
        var createdItem = CreateSampleItem(id: Guid.NewGuid());
        createdItem.SourceRegions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "path.jpg", BoundingBox = null }
        };
        _mockDomainService
            .Setup(x => x.AddMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ReturnsAsync(createdItem);

        var request = new AddMistakeItemRequest
        {
            StudentId = "s1",
            Subject = 1,
            Grade = 1,
            SourceUploadId = "upload-1"
        };
        request.SourceRegions.Add(new Ruoyu.Study.Mistake.Contract.Protos.SourceRegion
        {
            SourceImagePath = "path.jpg"
            // BoundingBox not set = null
        });

        var response = await _service.AddMistakeItem(request, CreateTestContext());

        response.SourceRegions[0].BoundingBox.Should().BeNull();
    }

    [Fact]
    public async Task AddMistakeItem_WhenDomainValidationFails_ThrowsInvalidArgument()
    {
        _mockDomainService
            .Setup(x => x.AddMistakeItemAsync(It.IsAny<MistakeItemModel>()))
            .ThrowsAsync(new MistakeValidationException("invalid item"));

        var request = new AddMistakeItemRequest
        {
            StudentId = "",
            Subject = 0,
            Grade = 0,
            SourceUploadId = ""
        };

        var act = () => _service.AddMistakeItem(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Be("invalid item");
    }

    #endregion

    #region DeleteMistakeItem

    [Fact]
    public async Task DeleteMistakeItem_WhenSuccessful_ReturnsTrue()
    {
        var itemId = Guid.NewGuid();
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId)).ReturnsAsync(true);

        var response = await _service.DeleteMistakeItem(new MistakeIdRequest { Id = itemId.ToString() }, CreateTestContext());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteMistakeItem_WhenItemNotFound_ReturnsFalse()
    {
        var itemId = Guid.NewGuid();
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId)).ReturnsAsync(false);

        var response = await _service.DeleteMistakeItem(new MistakeIdRequest { Id = itemId.ToString() }, CreateTestContext());

        response.Success.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteMistakeItem_WhenDomainValidationFails_ThrowsInvalidArgument()
    {
        var itemId = Guid.NewGuid();
        _mockDomainService.Setup(x => x.DeleteMistakeItemAsync(itemId))
            .ThrowsAsync(new MistakeValidationException("cannot delete"));

        var act = () => _service.DeleteMistakeItem(new MistakeIdRequest { Id = itemId.ToString() }, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region AddMistakePractice

    [Fact]
    public async Task AddMistakePractice_WithValidInput_ReturnsCreatedDto()
    {
        var mistakeItemId = Guid.NewGuid();
        var practiceId = Guid.NewGuid();
        var createdPractice = new MistakePracticeModel
        {
            Id = practiceId,
            MistakeItemId = mistakeItemId,
            StudentId = "s1",
            StudentAnswer = "my answer",
            IsCorrect = true,
            Comments = "good",
            PracticeDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };
        createdPractice.ImagePaths.Add("path1.jpg");

        _mockDomainService
            .Setup(x => x.AddPracticeAsync(
                It.Is<MistakePracticeModel>(p => p.MistakeItemId == mistakeItemId && p.StudentId == "s1"),
                It.IsAny<List<string>?>()))
            .ReturnsAsync(createdPractice);

        var request = new AddMistakePracticeRequest
        {
            MistakeItemId = mistakeItemId.ToString(),
            StudentId = "s1",
            StudentAnswer = "my answer",
            IsCorrect = true,
            Comments = "good"
        };
        request.ImagePaths.Add("path1.jpg");

        var response = await _service.AddMistakePractice(request, CreateTestContext());

        response.Id.Should().Be(practiceId.ToString());
        response.MistakeItemId.Should().Be(mistakeItemId.ToString());
        response.StudentId.Should().Be("s1");
        response.StudentAnswer.Should().Be("my answer");
        response.IsCorrect.Should().BeTrue();
        response.Comments.Should().Be("good");
        response.PracticeDate.Should().Be("2026-06-01T00:00:00.000Z");
        response.ImagePaths.Should().ContainSingle().Which.Should().Be("path1.jpg");
    }

    [Fact]
    public async Task AddMistakePractice_WithoutImagePaths_PassesNullToDomain()
    {
        var mistakeItemId = Guid.NewGuid();
        var createdPractice = new MistakePracticeModel
        {
            Id = Guid.NewGuid(),
            MistakeItemId = mistakeItemId,
            StudentId = "s1"
        };

        _mockDomainService
            .Setup(x => x.AddPracticeAsync(It.IsAny<MistakePracticeModel>(), null))
            .ReturnsAsync(createdPractice);

        var request = new AddMistakePracticeRequest
        {
            MistakeItemId = mistakeItemId.ToString(),
            StudentId = "s1"
        };
        // No image paths added

        var response = await _service.AddMistakePractice(request, CreateTestContext());

        response.Id.Should().Be(createdPractice.Id.ToString());
        _mockDomainService.Verify(x => x.AddPracticeAsync(It.IsAny<MistakePracticeModel>(), null), Times.Once);
    }

    [Fact]
    public async Task AddMistakePractice_WhenDomainValidationFails_ThrowsInvalidArgument()
    {
        _mockDomainService
            .Setup(x => x.AddPracticeAsync(It.IsAny<MistakePracticeModel>(), It.IsAny<List<string>?>()))
            .ThrowsAsync(new MistakeValidationException("not confirmed"));

        var request = new AddMistakePracticeRequest
        {
            MistakeItemId = Guid.NewGuid().ToString(),
            StudentId = "s1"
        };

        var act = () => _service.AddMistakePractice(request, CreateTestContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Be("not confirmed");
    }

    #endregion

    #region GetMistakePractices

    [Fact]
    public async Task GetMistakePractices_ReturnsMappedList()
    {
        var mistakeItemId = Guid.NewGuid();
        var practices = new List<MistakePracticeModel>
        {
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId, StudentId = "s1",
                StudentAnswer = "ans1", IsCorrect = true,
                PracticeDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId, StudentId = "s1",
                StudentAnswer = "ans2", IsCorrect = false,
                PracticeDate = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)
            }
        };
        _mockDomainService.Setup(x => x.GetPracticesAsync(mistakeItemId)).ReturnsAsync(practices);

        var response = await _service.GetMistakePractices(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Practices.Should().HaveCount(2);
        response.Practices[0].StudentAnswer.Should().Be("ans1");
        response.Practices[0].IsCorrect.Should().BeTrue();
        response.Practices[1].StudentAnswer.Should().Be("ans2");
        response.Practices[1].IsCorrect.Should().BeFalse();
    }

    [Fact]
    public async Task GetMistakePractices_WhenNoPractices_ReturnsEmptyList()
    {
        var mistakeItemId = Guid.NewGuid();
        _mockDomainService.Setup(x => x.GetPracticesAsync(mistakeItemId)).ReturnsAsync(new List<MistakePracticeModel>());

        var response = await _service.GetMistakePractices(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Practices.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMistakePractices_WithMinDateTimePracticeDate_ReturnsEmptyString()
    {
        var mistakeItemId = Guid.NewGuid();
        var practices = new List<MistakePracticeModel>
        {
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId,
                PracticeDate = DateTimeOffset.MinValue
            }
        };
        _mockDomainService.Setup(x => x.GetPracticesAsync(mistakeItemId)).ReturnsAsync(practices);

        var response = await _service.GetMistakePractices(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Practices[0].PracticeDate.Should().BeEmpty();
    }

    #endregion

    #region LogMistakeProcess

    [Fact]
    public async Task LogMistakeProcess_WhenSuccessful_ReturnsTrue()
    {
        var mistakeItemId = Guid.NewGuid();
        _mockDomainService
            .Setup(x => x.LogMistakeProcessAsync(mistakeItemId, "s1", ProcessAction.Practiced, "details"))
            .ReturnsAsync(true);

        var request = new LogMistakeProcessRequest
        {
            MistakeItemId = mistakeItemId.ToString(),
            StudentId = "s1",
            Action = ProcessAction.Practiced,
            Details = "details"
        };

        var response = await _service.LogMistakeProcess(request, CreateTestContext());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogMistakeProcess_WhenFails_ReturnsFalse()
    {
        var mistakeItemId = Guid.NewGuid();
        _mockDomainService
            .Setup(x => x.LogMistakeProcessAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ProcessAction>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var request = new LogMistakeProcessRequest
        {
            MistakeItemId = mistakeItemId.ToString(),
            StudentId = "s1",
            Action = ProcessAction.Mastered,
            Details = "mastered"
        };

        var response = await _service.LogMistakeProcess(request, CreateTestContext());

        response.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LogMistakeProcess_WithNullDetails_PassesToDomain()
    {
        var mistakeItemId = Guid.NewGuid();
        _mockDomainService
            .Setup(x => x.LogMistakeProcessAsync(mistakeItemId, "s1", ProcessAction.Created, It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new LogMistakeProcessRequest
        {
            MistakeItemId = mistakeItemId.ToString(),
            StudentId = "s1",
            Action = ProcessAction.Created
        };

        var response = await _service.LogMistakeProcess(request, CreateTestContext());

        response.Success.Should().BeTrue();
        _mockDomainService.Verify(x => x.LogMistakeProcessAsync(mistakeItemId, "s1", ProcessAction.Created, It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region GetMistakeProcessHistory

    [Fact]
    public async Task GetMistakeProcessHistory_ReturnsMappedLogs()
    {
        var mistakeItemId = Guid.NewGuid();
        var logs = new List<ProcessLogModel>
        {
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId, StudentId = "s1",
                Action = (int)ProcessAction.Created, Details = "created",
                CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId, StudentId = "s1",
                Action = (int)ProcessAction.Practiced, Details = "practiced",
                CreatedAt = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)
            }
        };
        _mockDomainService.Setup(x => x.GetMistakeProcessHistoryAsync(mistakeItemId)).ReturnsAsync(logs);

        var response = await _service.GetMistakeProcessHistory(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Logs.Should().HaveCount(2);
        response.Logs[0].Action.Should().Be(ProcessAction.Created);
        response.Logs[0].Details.Should().Be("created");
        response.Logs[0].CreatedAt.Should().Be("2026-06-01T00:00:00.000Z");
        response.Logs[1].Action.Should().Be(ProcessAction.Practiced);
        response.Logs[1].CreatedAt.Should().Be("2026-06-02T00:00:00.000Z");
    }

    [Fact]
    public async Task GetMistakeProcessHistory_WhenNoLogs_ReturnsEmptyList()
    {
        var mistakeItemId = Guid.NewGuid();
        _mockDomainService.Setup(x => x.GetMistakeProcessHistoryAsync(mistakeItemId)).ReturnsAsync(new List<ProcessLogModel>());

        var response = await _service.GetMistakeProcessHistory(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMistakeProcessHistory_WithMinDateTimeCreatedAt_ReturnsEmptyString()
    {
        var mistakeItemId = Guid.NewGuid();
        var logs = new List<ProcessLogModel>
        {
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId,
                CreatedAt = DateTimeOffset.MinValue
            }
        };
        _mockDomainService.Setup(x => x.GetMistakeProcessHistoryAsync(mistakeItemId)).ReturnsAsync(logs);

        var response = await _service.GetMistakeProcessHistory(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Logs[0].CreatedAt.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMistakeProcessHistory_WithNullStudentId_ReturnsEmptyString()
    {
        var mistakeItemId = Guid.NewGuid();
        var logs = new List<ProcessLogModel>
        {
            new()
            {
                Id = Guid.NewGuid(), MistakeItemId = mistakeItemId,
                StudentId = null,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };
        _mockDomainService.Setup(x => x.GetMistakeProcessHistoryAsync(mistakeItemId)).ReturnsAsync(logs);

        var response = await _service.GetMistakeProcessHistory(new MistakeIdRequest { Id = mistakeItemId.ToString() }, CreateTestContext());

        response.Logs[0].StudentId.Should().BeEmpty();
    }

    #endregion
}
