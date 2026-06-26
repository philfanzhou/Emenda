using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// Tests for VlUploadAnalyzer covering HTTP request/response handling,
/// group parsing, deduplication, and default value fallback.
/// </summary>
public class VlUploadAnalyzerTests
{
    private readonly Mock<IOssService> _ossService;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly VlAnalyzerOptions _options;
    private readonly Mock<ILogger<VlUploadAnalyzer>> _logger;

    public VlUploadAnalyzerTests()
    {
        _ossService = new Mock<IOssService>();
        _httpClientFactory = new Mock<IHttpClientFactory>();
        _options = new VlAnalyzerOptions
        {
            ApiKey = "test-key",
            Model = "test-model",
            ApiBaseUrl = "http://localhost:8000",
            Temperature = 0.1,
            TimeoutSeconds = 30,
            MaxRetries = 0,
            RetryDelayMs = 0
        };
        _logger = new Mock<ILogger<VlUploadAnalyzer>>();

        // Default: return a valid small JPEG from OSS
        _ossService.Setup(x => x.DownloadAsync(It.IsAny<string>()))
            .ReturnsAsync(() =>
            {
                using var image = new Image<Rgba32>(100, 100);
                var ms = new MemoryStream();
                image.SaveAsJpeg(ms);
                ms.Position = 0;
                return (Stream)ms;
            });
    }

    private VlUploadAnalyzer CreateAnalyzer(HttpResponseMessage httpResponse)
    {
        var handler = new MockHttpMessageHandler(httpResponse);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.Setup(x => x.CreateClient("VlAnalyzer"))
            .Returns(httpClient);

        return new VlUploadAnalyzer(
            _ossService.Object,
            _httpClientFactory.Object,
            Options.Create(_options),
            _logger.Object);
    }

    #region AnalyzeAsync - Normal Response

    [Fact]
    public async Task AnalyzeAsync_WhenValidResponse_ShouldParseGroupsCorrectly()
    {
        // Arrange
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": ""[\n  { \""imageIndices\"": [0], \""subject\"": 3, \""grade\"": 8, \""rootCause\"": \""English vocabulary\"" },\n  { \""imageIndices\"": [1], \""subject\"": 1, \""grade\"": 7, \""rootCause\"": \""Chinese reading\"" }\n]"",
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg", "img2.jpg" });

        // Assert
        result.Skipped.Should().BeFalse();
        result.Groups.Should().HaveCount(2);
        result.Groups[0].ImageIndices.Should().Equal(0);
        result.Groups[0].Subject.Should().Be(3);
        result.Groups[0].Grade.Should().Be(8);
        result.Groups[0].RootCause.Should().Be("English vocabulary");
        result.Groups[1].ImageIndices.Should().Equal(1);
        result.Groups[1].Subject.Should().Be(1);
        result.Groups[1].Grade.Should().Be(7);
        result.Groups[1].RootCause.Should().Be("Chinese reading");
    }

    #endregion

    #region AnalyzeAsync - Empty Images

    [Fact]
    public async Task AnalyzeAsync_WhenEmptyImages_ShouldReturnSkipped()
    {
        // Arrange
        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string>());

        // Assert
        result.Skipped.Should().BeTrue();
        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenNullImages_ShouldReturnSkipped()
    {
        // Arrange
        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", null!);

        // Assert
        result.Skipped.Should().BeTrue();
    }

    #endregion

    #region AnalyzeAsync - HTTP Error

    [Fact]
    public async Task AnalyzeAsync_WhenHttpRequestFails_ShouldThrowAfterRetries()
    {
        // Arrange: Mock OSS to return valid image, but HTTP client throws
        var handler = new FailingHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        _httpClientFactory.Setup(x => x.CreateClient("VlAnalyzer"))
            .Returns(httpClient);

        var analyzer = new VlUploadAnalyzer(
            _ossService.Object,
            _httpClientFactory.Object,
            Options.Create(_options),
            _logger.Object);

        // Act
        var act = () => analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert: HttpRequestException is thrown when HTTP handler fails
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region AnalyzeAsync - Overlapping Indices (TryParseGroups deduplication)

    [Fact]
    public async Task AnalyzeAsync_WhenOverlappingIndices_ShouldDeduplicate()
    {
        // Arrange: Group 0 uses [0,1], Group 1 uses [1,2] -> Group 1 keeps [2]
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": ""[\n  { \""imageIndices\"": [0, 1], \""subject\"": 1, \""grade\"": 7, \""rootCause\"": \""Group A\"" },\n  { \""imageIndices\"": [1, 2], \""subject\"": 2, \""grade\"": 8, \""rootCause\"": \""Group B\"" }\n]"",
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg", "img2.jpg", "img3.jpg" });

        // Assert
        result.Groups.Should().HaveCount(2);
        result.Groups[0].ImageIndices.Should().Equal(0, 1);
        result.Groups[0].Subject.Should().Be(1);
        // Index 1 was overlapping, so Group 1 only keeps index 2
        result.Groups[1].ImageIndices.Should().Equal(2);
        result.Groups[1].Subject.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenAllIndicesOverlap_ShouldSkipFullyOverlappingGroup()
    {
        // Arrange: Group 0 uses [0], Group 1 uses [0] -> Group 1 is skipped
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": ""[\n  { \""imageIndices\"": [0], \""subject\"": 1, \""grade\"": 7, \""rootCause\"": \""Group A\"" },\n  { \""imageIndices\"": [0], \""subject\"": 2, \""grade\"": 8, \""rootCause\"": \""Group B\"" }\n]"",
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert: Only Group 0 kept, Group 1 fully overlapped and skipped
        result.Groups.Should().HaveCount(1);
        result.Groups[0].ImageIndices.Should().Equal(0);
        result.Groups[0].Subject.Should().Be(1);
    }

    #endregion

    #region TryParseGroups - Invalid Subject/Grade Defaults

    [Fact]
    public async Task AnalyzeAsync_WhenSubjectAndGradeAreInvalid_ShouldDefaultToSubject1Grade7()
    {
        // Arrange: subject=0 (invalid), grade=15 (invalid) -> defaults to subject=1, grade=7
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": ""[\n  { \""imageIndices\"": [0], \""subject\"": 0, \""grade\"": 15, \""rootCause\"": \""Invalid values\"" }\n]"",
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert
        result.Groups.Should().HaveCount(1);
        result.Groups[0].Subject.Should().Be(1);  // Default for invalid subject
        result.Groups[0].Grade.Should().Be(7);     // Default for invalid grade
    }

    [Fact]
    public async Task AnalyzeAsync_WhenSubjectIsNegative_ShouldDefaultTo1()
    {
        // Arrange: subject=-1 (invalid)
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": ""[\n  { \""imageIndices\"": [0], \""subject\"": -1, \""grade\"": 5, \""rootCause\"": \""Negative subject\"" }\n]"",
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert
        result.Groups[0].Subject.Should().Be(1);
        result.Groups[0].Grade.Should().Be(5); // Valid grade kept
    }

    [Fact]
    public async Task AnalyzeAsync_WhenGradeIsZero_ShouldDefaultTo7()
    {
        // Arrange: grade=0 (invalid)
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": ""[\n  { \""imageIndices\"": [0], \""subject\"": 3, \""grade\"": 0, \""rootCause\"": \""Zero grade\"" }\n]"",
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert
        result.Groups[0].Subject.Should().Be(3); // Valid subject kept
        result.Groups[0].Grade.Should().Be(7);   // Default for invalid grade
    }

    #endregion

    #region AnalyzeAsync - Content Format Variations

    [Fact]
    public async Task AnalyzeAsync_WhenContentIsArray_ShouldParseGroups()
    {
        // Arrange - content as array of content parts
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": [
                        {""type"": ""text"", ""text"": ""[\n  { \""imageIndices\"": [0, 1], \""subject\"": 1, \""grade\"": 7, \""rootCause\"": \""Chinese reading\"" }\n]""}
                    ],
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg", "img2.jpg" });

        // Assert
        result.Groups.Should().HaveCount(1);
        result.Groups[0].ImageIndices.Should().Equal(0, 1);
        result.Groups[0].Subject.Should().Be(1);
        result.Groups[0].Grade.Should().Be(7);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenContentIsNull_ShouldReturnEmptyGroups()
    {
        // Arrange
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [{
                ""finish_reason"": ""stop"",
                ""index"": 0,
                ""message"": {
                    ""content"": null,
                    ""role"": ""assistant""
                }
            }],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert
        result.Groups.Should().BeEmpty();
        result.Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenEmptyChoices_ShouldReturnEmptyGroups()
    {
        // Arrange
        var apiResponse = @"{
            ""id"": ""test-id"",
            ""choices"": [],
            ""model"": ""test-model"",
            ""object"": ""chat.completion""
        }";

        var analyzer = CreateAnalyzer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(apiResponse, Encoding.UTF8, "application/json")
        });

        // Act
        var result = await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert
        result.Groups.Should().BeEmpty();
        result.Skipped.Should().BeFalse();
    }

    #endregion

    #region Request Body - max_tokens

    /// <summary>
    /// Captures the outgoing request body so tests can assert the JSON shape
    /// (specifically that <c>max_tokens</c> is set, which is required for
    /// reasoning VL models like mimo-v2.5 / step-3.7-flash to leave room
    /// for the actual <c>content</c> response). See HumanReview HR-12.
    /// </summary>
    private class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public string? CapturedRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""choices"": [{
                        ""finish_reason"": ""stop"",
                        ""index"": 0,
                        ""message"": { ""role"": ""assistant"", ""content"": ""[]"" }
                    }]
                }", Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task AnalyzeAsync_RequestBody_ShouldIncludeMaxTokens()
    {
        // Arrange
        var handler = new CapturingHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        _httpClientFactory.Setup(x => x.CreateClient("VlAnalyzer")).Returns(httpClient);
        var analyzer = new VlUploadAnalyzer(
            _ossService.Object,
            _httpClientFactory.Object,
            Options.Create(_options),
            _logger.Object);

        // Act
        await analyzer.AnalyzeAsync("student-1", new List<string> { "img1.jpg" });

        // Assert
        handler.CapturedRequestBody.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.TryGetProperty("max_tokens", out var maxTokensProp).Should().BeTrue(
            "request body must include max_tokens for reasoning VL models (mimo, stepfun)");
        maxTokensProp.GetInt32().Should().Be(4096);
    }

    #endregion

    #region Mock Helpers

    /// <summary>
    /// Mock HttpMessageHandler that returns a predefined response.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    /// <summary>
    /// Mock HttpMessageHandler that always throws.
    /// </summary>
    private class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }

    #endregion
}
