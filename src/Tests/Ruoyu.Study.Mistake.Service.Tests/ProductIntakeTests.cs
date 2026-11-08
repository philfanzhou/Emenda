using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.Intake;
using Ruoyu.Study.Mistake.Service.Services;
using Ruoyu.Study.Mistake.Service.StudentIntegration;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Product-owned intake acceptance tests: validation and idempotency of
/// <see cref="ProductIntakeService"/>, the extracted
/// <see cref="LegacyUploadProcessor"/> pipeline, and the minimal-API surface.
/// Everything is mocked; no PostgreSQL or OSS instance is required.
/// </summary>
public sealed class ProductIntakeTests
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ===== Validation =====

    [Fact]
    public async Task MissingStudentId_ReturnsInvalidRequest()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(harness.Request(studentId: " "));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.StudentIdRequired);
        harness.Oss.VerifyNoOtherCalls();
        harness.Domain.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StudentIdLongerThan128Characters_ReturnsInvalidRequest()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(harness.Request(studentId: new string('s', 129)));

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        harness.Oss.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("contains space")]
    [InlineData("slash/inside")]
    [InlineData("key!")]
    public async Task MalformedSourceKey_ReturnsInvalidRequest(string sourceKey)
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(harness.Request(sourceKey: sourceKey));

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.SourceKeyInvalid);
        harness.Oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoImages_ReturnsInvalidRequest()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(harness.Request(imageCount: 0));

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ImagesRequired);
        harness.Oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MoreThanTwentyImages_ReturnsInvalidRequest()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(harness.Request(imageCount: 21));

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.TooManyImages);
        harness.Oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImageLargerThan15MiB_ReturnsInvalidRequest()
    {
        var harness = new Harness();
        var request = harness.Request();
        request.Images[0].Content = new byte[ProductIntakeLimits.MaxImageBytes + 1];

        var result = await harness.Service.CreateAsync(request);

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ImageSizeInvalid);
        harness.Oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmptyImage_ReturnsInvalidRequest()
    {
        var harness = new Harness();
        var request = harness.Request();
        request.Images[0].Content = [];

        var result = await harness.Service.CreateAsync(request);

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ImageSizeInvalid);
        harness.Oss.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    public async Task UnsupportedContentType_ReturnsInvalidRequest(string contentType)
    {
        var harness = new Harness();
        var request = harness.Request();
        request.Images[0].ContentType = contentType;

        var result = await harness.Service.CreateAsync(request);

        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ContentTypeInvalid);
        harness.Oss.VerifyNoOtherCalls();
    }

    // ===== Processing =====

    [Fact]
    public async Task ValidRequest_StoresImagesAndReturnsProcessedItems()
    {
        var harness = new Harness(vlConfigured: true);
        harness.Analyzer
            .Setup(a => a.AnalyzeAsync("student-1", It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlAnalysisResult
            {
                Groups =
                [
                    new VlImageGroup { ImageIndices = [0], Subject = 2, Grade = 8, RootCause = "algebra" },
                    new VlImageGroup { ImageIndices = [1], Subject = 3, Grade = 9, RootCause = "geometry" }
                ]
            });
        var request = harness.Request(imageCount: 2, sourceKey: "batch-001");
        request.Images[0].ContentType = "image/png";
        request.Images[1].ContentType = "image/jpeg";

        var result = await harness.Service.CreateAsync(request);

        result.Success.Should().BeTrue();
        result.SourceId.Should().Be("batch-001");
        result.Items.Should().HaveCount(2);
        result.Items.Select(item => item.Subject).Should().BeEquivalentTo([2, 3]);
        result.Items.Select(item => item.Grade).Should().BeEquivalentTo([8, 9]);
        result.Items.Should().OnlyContain(item => item.ReviewStatus == (int)ReviewStatus.PendingReview);
        result.Items.SelectMany(item => item.ImagePaths)
            .Should().OnlyContain(path => path.StartsWith("mistakes/", StringComparison.Ordinal));

        harness.UploadBuckets.Should().OnlyContain(bucket => bucket == OssBucket.Uploads);
        harness.UploadBuckets.Should().HaveCount(2);
        harness.Copies.Should().HaveCount(2);
        harness.Copies.Should().OnlyContain(copy =>
            copy.Source.StartsWith("uploads/", StringComparison.Ordinal) &&
            copy.Destination == "mistakes/" + copy.Source.Substring("uploads/".Length));
        harness.Deletes.Should().BeEquivalentTo(harness.Copies.Select(copy => copy.Source));
    }

    [Fact]
    public async Task ExistingSourceKey_ReturnsExistingItemsWithoutTouchingOss()
    {
        var harness = new Harness();
        var existingId = Guid.NewGuid();
        harness.StoredItems.Add(new MistakeItemModel
        {
            Id = existingId,
            StudentId = "student-1",
            Subject = 4,
            Grade = 10,
            SourceUploadId = "batch-001",
            ReviewStatus = (int)ReviewStatus.Confirmed,
            SourceRegions = [new SourceRegionModel { SourceImagePath = "mistakes/2026/11/09/a/image.png" }]
        });
        var request = harness.Request(sourceKey: "batch-001");

        var result = await harness.Service.CreateAsync(request);

        result.Success.Should().BeTrue();
        result.SourceId.Should().Be("batch-001");
        var item = result.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be(existingId);
        item.Subject.Should().Be(4);
        item.Grade.Should().Be(10);
        item.ReviewStatus.Should().Be((int)ReviewStatus.Confirmed);
        item.ImagePaths.Should().Equal("mistakes/2026/11/09/a/image.png");
        harness.Oss.VerifyNoOtherCalls();
        harness.Domain.Verify(service => service.GetMistakeItemsByUploadAsync("batch-001"), Times.Once);
    }

    [Fact]
    public async Task MissingSourceKey_GeneratesIdempotentSourceId()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(harness.Request());

        result.Success.Should().BeTrue();
        result.SourceId.Should().NotBeNullOrWhiteSpace();
        result.SourceId.Should().MatchRegex("^[A-Za-z0-9._-]{1,64}$");
        var stored = harness.StoredItems.Should().ContainSingle().Subject;
        stored.SourceUploadId.Should().Be(result.SourceId);
        // Two service-level checks (idempotency probe + result read) plus the
        // processor's own idempotency check for the same source key.
        harness.Domain.Verify(service => service.GetMistakeItemsByUploadAsync(result.SourceId!), Times.Exactly(3));
    }

    [Fact]
    public async Task VlNotConfigured_UsesDefaultSingleGroup()
    {
        var harness = new Harness();
        var request = harness.Request(imageCount: 2);

        var result = await harness.Service.CreateAsync(request);

        result.Success.Should().BeTrue();
        harness.Analyzer.VerifyNoOtherCalls();
        harness.Submits.Should().ContainSingle();
        harness.Submits[0].Subject.Should().Be(1);
        harness.Submits[0].Grade.Should().Be(7);
        harness.Submits[0].RootCause.Should().Be("Mistake upload (VL skipped)");
        harness.Submits[0].ImagePaths.Should().HaveCount(2);
    }

    [Fact]
    public async Task VlFailure_ReturnsVlFailed()
    {
        var harness = new Harness(vlConfigured: true);
        harness.Analyzer
            .Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("upstream unavailable"));

        var result = await harness.Service.CreateAsync(harness.Request());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.VlFailed);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.VlFailed);
        harness.Submits.Should().BeEmpty();
        harness.Domain.Verify(service => service.SubmitMistakeUploadAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<string>>(),
            It.IsAny<string?>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessingProducesNoItems_ReturnsProcessingFailed()
    {
        var harness = new Harness();
        harness.Domain
            .Setup(service => service.SubmitMistakeUploadAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<string>>(),
                It.IsAny<string?>(), It.IsAny<string>()))
            .ReturnsAsync(new List<MistakeItemModel>());

        var result = await harness.Service.CreateAsync(harness.Request());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.ProcessingFailed);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ProcessingFailed);
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsProcessingFailed()
    {
        var harness = new Harness();
        harness.Domain
            .Setup(service => service.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var result = await harness.Service.CreateAsync(harness.Request());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.ProcessingFailed);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ProcessingFailed);
    }

    [Fact]
    public async Task ReservedTargetPath_ReturnsReservedTarget()
    {
        var harness = new Harness();
        harness.Oss
            .Setup(oss => oss.UploadAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OssBucket>(), It.IsAny<string?>()))
            .ReturnsAsync("uploads/source-targets/source-1/1/original");

        var result = await harness.Service.CreateAsync(harness.Request());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ProductIntakeErrorCodes.ReservedTarget);
        result.ErrorMessage.Should().Be(ProductIntakeMessages.ReservedTarget);
        harness.Submits.Should().BeEmpty();
    }

    // ===== LegacyUploadProcessor =====

    [Fact]
    public async Task ProcessorNotifiesSourceWithProcessedPaths()
    {
        var harness = new Harness();
        var record = Record("record-1",
            ("uploads/2026/11/09/a/one.png", "mistake"),
            ("uploads/2026/11/09/a/two.png", "mistake"),
            ("uploads/2026/11/09/a/homework.png", "homework"));
        var notifications = new List<(string RecordId, IReadOnlyList<string> Paths)>();

        var status = await harness.Processor.ProcessAsync(record,
            (recordId, paths, _) =>
            {
                notifications.Add((recordId, paths));
                return Task.CompletedTask;
            });

        status.Should().Be(LegacyUploadProcessingStatus.Completed);
        var notification = notifications.Should().ContainSingle().Subject;
        notification.RecordId.Should().Be("record-1");
        notification.Paths.Should().Equal(
            "uploads/2026/11/09/a/one.png", "uploads/2026/11/09/a/two.png");
        harness.Submits.Should().ContainSingle();
        harness.Submits[0].ImagePaths.Should().NotContain("uploads/2026/11/09/a/homework.png");
    }

    [Fact]
    public async Task ProcessorSkipsRecordWithoutMistakeImages()
    {
        var harness = new Harness();
        var record = Record("record-1", ("uploads/2026/11/09/a/homework.png", "homework"));

        var status = await harness.Processor.ProcessAsync(record,
            (_, _, _) => throw new InvalidOperationException("must not notify"));

        status.Should().Be(LegacyUploadProcessingStatus.NoMistakeImages);
        harness.Submits.Should().BeEmpty();
        harness.Oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessorIdempotencyNormalizesMistakePathsAndNotifiesOnly()
    {
        var harness = new Harness();
        harness.StoredItems.Add(new MistakeItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            SourceUploadId = "record-1",
            SourceRegions =
            [
                new SourceRegionModel { SourceImagePath = "mistakes/2026/11/09/a/one.png" }
            ]
        });
        var record = Record("record-1", ("uploads/2026/11/09/a/one.png", "mistake"));
        var notifications = new List<IReadOnlyList<string>>();

        var status = await harness.Processor.ProcessAsync(record,
            (_, paths, _) =>
            {
                notifications.Add(paths);
                return Task.CompletedTask;
            });

        status.Should().Be(LegacyUploadProcessingStatus.AllImagesAlreadyProcessed);
        notifications.Should().ContainSingle();
        notifications[0].Should().Equal("uploads/2026/11/09/a/one.png");
        harness.Submits.Should().BeEmpty();
        harness.Copies.Should().BeEmpty();
        harness.Domain.Verify(service => service.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()), Times.Never);
    }

    [Fact]
    public async Task ProcessorRejectsReservedSourceTargetPaths()
    {
        var harness = new Harness();
        var record = Record("record-1", ("uploads/source-targets/source-1/1/original", "mistake"));

        Func<Task> process = () => harness.Processor.ProcessAsync(record);

        var exception = await process.Should().ThrowAsync<SourceIntakeException>();
        exception.Which.ErrorKind.Should().Be("reserved_target");
    }

    [Fact]
    public async Task ProcessorSkipsVlWhenAnalyzerIsNotConfigured()
    {
        var harness = new Harness();

        await harness.Processor.ProcessAsync(Record("record-1", ("uploads/2026/11/09/a/one.png", "mistake")));

        harness.Analyzer.Verify(a => a.AnalyzeAsync(
            It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ===== DI registration and HTTP surface =====

    [Fact]
    public void AddProductIntakeRegistersScopedServices()
    {
        var services = new ServiceCollection();

        services.AddProductIntake();

        services.Single(descriptor => descriptor.ServiceType == typeof(IProductIntakeService))
            .Should().Match<ServiceDescriptor>(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Scoped &&
                descriptor.ImplementationType == typeof(ProductIntakeService));
        services.Single(descriptor => descriptor.ServiceType == typeof(LegacyUploadProcessor))
            .Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task EndpointRejectsNonMultipartRequest()
    {
        var intake = new Mock<IProductIntakeService>();
        await using var app = await StartEndpointHostAsync(intake.Object);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(ProductIntakeEndpoints.Route,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ProductIntakeErrorCodes.InvalidRequest);
        json.RootElement.GetProperty("message").GetString()
            .Should().Be(ProductIntakeMessages.MultipartRequired);
        intake.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("images")]
    [InlineData("images[]")]
    public async Task EndpointReturnsCreatedItemsForMultipartRequest(string imageFieldName)
    {
        var itemId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var intake = new Mock<IProductIntakeService>();
        intake.Setup(service => service.CreateAsync(It.IsAny<ProductIntakeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProductIntakeResult.Completed("source-1",
            [
                new ProductIntakeItem
                {
                    Id = itemId,
                    Subject = 2,
                    Grade = 8,
                    ReviewStatus = 1,
                    ImagePaths = ["mistakes/2026/11/09/a/one.png"]
                }
            ]));
        await using var app = await StartEndpointHostAsync(intake.Object);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-1"), "studentId");
        form.Add(new StringContent("batch-001"), "sourceKey");
        form.Add(new ByteArrayContent(PngBytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } },
            imageFieldName, "one.png");

        using var response = await client.PostAsync(ProductIntakeEndpoints.Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("sourceId").GetString().Should().Be("source-1");
        var item = json.RootElement.GetProperty("items")[0];
        item.GetProperty("id").GetString().Should().Be(itemId.ToString());
        item.GetProperty("subject").GetInt32().Should().Be(2);
        item.GetProperty("grade").GetInt32().Should().Be(8);
        item.GetProperty("reviewStatus").GetInt32().Should().Be(1);
        item.GetProperty("imagePaths")[0].GetString().Should().Be("mistakes/2026/11/09/a/one.png");
        intake.Verify(service => service.CreateAsync(
            It.Is<ProductIntakeRequest>(request =>
                request.StudentId == "student-1" &&
                request.SourceKey == "batch-001" &&
                request.Images.Count == 1 &&
                request.Images[0].ContentType == "image/png" &&
                request.Images[0].Content.Length == PngBytes.Length),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ProductIntakeErrorCodes.InvalidRequest, 400)]
    [InlineData(ProductIntakeErrorCodes.ReservedTarget, 409)]
    [InlineData(ProductIntakeErrorCodes.ProcessingFailed, 500)]
    [InlineData(ProductIntakeErrorCodes.VlFailed, 500)]
    public async Task EndpointMapsIntakeErrorCodes(string errorCode, int expectedStatus)
    {
        var intake = new Mock<IProductIntakeService>();
        intake.Setup(service => service.CreateAsync(It.IsAny<ProductIntakeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProductIntakeResult.Failed(errorCode, "stable message"));
        await using var app = await StartEndpointHostAsync(intake.Object);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-1"), "studentId");
        form.Add(new ByteArrayContent(PngBytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } },
            "images", "one.png");

        using var response = await client.PostAsync(ProductIntakeEndpoints.Route, form);

        ((int)response.StatusCode).Should().Be(expectedStatus);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        json.RootElement.GetProperty("message").GetString().Should().Be("stable message");
    }

    [Fact]
    public async Task EndpointRejectsMoreThanTwentyFiles()
    {
        var intake = new Mock<IProductIntakeService>();
        await using var app = await StartEndpointHostAsync(intake.Object);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("student-1"), "studentId");
        for (var index = 0; index < 21; index++)
        {
            form.Add(new ByteArrayContent(PngBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") }
            }, "images[]", $"image-{index}.png");
        }

        using var response = await client.PostAsync(ProductIntakeEndpoints.Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("message").GetString().Should().Be(ProductIntakeMessages.TooManyImages);
        intake.VerifyNoOtherCalls();
    }

    private static async Task<WebApplication> StartEndpointHostAsync(IProductIntakeService intake)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(intake);
        var app = builder.Build();
        app.MapProductIntakeEndpoints();
        await app.StartAsync();
        return app;
    }

    private static ProcessingUploadRecord Record(string id, params (string Path, string Type)[] images) => new()
    {
        Id = id,
        StudentId = "student-1",
        ImageEntries = images
            .Select(image => new UploadImageEntry { Path = image.Path, Type = image.Type })
            .ToList()
    };

    private sealed class Harness
    {
        public Mock<IOssService> Oss { get; } = new();
        public Mock<IVlAnalyzer> Analyzer { get; } = new();
        public Mock<IMistakeDomainService> Domain { get; } = new();
        public VlAnalyzerOptions VlOptions { get; } = new();
        public List<MistakeItemModel> StoredItems { get; } = new();
        public List<OssBucket> UploadBuckets { get; } = new();
        public List<(string Source, string Destination)> Copies { get; } = new();
        public List<string> Deletes { get; } = new();
        public List<(string StudentId, int Subject, int Grade, List<string> ImagePaths, string? RootCause, string SourceUploadId)> Submits { get; } = new();
        public LegacyUploadProcessor Processor { get; }
        public ProductIntakeService Service { get; }

        public Harness(bool vlConfigured = false)
        {
            if (vlConfigured) VlOptions.ApiKey = "test-api-key";

            var uploadIndex = 0;
            Oss.Setup(oss => oss.UploadAsync(
                    It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<OssBucket>(), It.IsAny<string?>()))
                .ReturnsAsync((byte[] content, string fileName, string contentType, OssBucket bucket, string? folder) =>
                {
                    UploadBuckets.Add(bucket);
                    uploadIndex++;
                    return $"uploads/2026/11/09/{uploadIndex:D2}/{fileName}";
                });
            Oss.Setup(oss => oss.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Callback((string source, string destination) => Copies.Add((source, destination)))
                .Returns(Task.CompletedTask);
            Oss.Setup(oss => oss.DeleteAsync(It.IsAny<string>()))
                .ReturnsAsync((string path) =>
                {
                    Deletes.Add(path);
                    return true;
                });

            Domain.Setup(service => service.GetMistakeItemsByUploadAsync(It.IsAny<string>()))
                .ReturnsAsync(() => StoredItems.ToList());
            Domain.Setup(service => service.SubmitMistakeUploadAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<string>>(),
                    It.IsAny<string?>(), It.IsAny<string>()))
                .ReturnsAsync((string studentId, int subject, int grade, List<string> imagePaths,
                    string? rootCause, string sourceUploadId) =>
                {
                    Submits.Add((studentId, subject, grade, imagePaths, rootCause, sourceUploadId));
                    var item = new MistakeItemModel
                    {
                        Id = Guid.NewGuid(),
                        StudentId = studentId,
                        Subject = subject,
                        Grade = grade,
                        RootCause = rootCause,
                        SourceUploadId = sourceUploadId,
                        ReviewStatus = (int)ReviewStatus.PendingReview,
                        SourceRegions = imagePaths
                            .Select(path => new SourceRegionModel { SourceImagePath = path })
                            .ToList()
                    };
                    StoredItems.Add(item);
                    return new List<MistakeItemModel> { item };
                });
            Domain.Setup(service => service.UpdateMistakeItemAsync(It.IsAny<MistakeItemModel>()))
                .ReturnsAsync(true);

            Processor = new LegacyUploadProcessor(
                Domain.Object, Oss.Object, Analyzer.Object,
                Options.Create(VlOptions), NullLogger<LegacyUploadProcessor>.Instance);
            Service = new ProductIntakeService(
                Oss.Object, Domain.Object, Processor, NullLogger<ProductIntakeService>.Instance);
        }

        public ProductIntakeRequest Request(int imageCount = 1, string studentId = "student-1", string? sourceKey = null) => new()
        {
            StudentId = studentId,
            SourceKey = sourceKey,
            Images = Enumerable.Range(0, imageCount)
                .Select(index => new ProductIntakeImage
                {
                    FileName = $"image-{index}.png",
                    ContentType = "image/png",
                    Content = PngBytes
                })
                .ToList()
        };
    }
}