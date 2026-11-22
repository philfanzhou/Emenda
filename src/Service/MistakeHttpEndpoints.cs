using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Emenda.Common.Oss;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Service.Dtos;
using MistakeItemDto = Emenda.Service.Dtos.MistakeItemDto;
using MistakeItemPageResult = Emenda.Service.Dtos.MistakeItemPageResult;
using PendingReviewUploadDto = Emenda.Service.Dtos.PendingReviewUploadDto;

namespace Emenda.Service;

/// <summary>
/// Mistake lifecycle HTTP endpoints. Business logic is delegated to IMistakeDomainService;
/// this class only adapts the protocol (JSON request/response envelopes).
/// </summary>
public static partial class MistakeHttpEndpoints
{
    /// <summary>
    /// Maps the mistake lifecycle endpoints (the /api group). Product caller
    /// authentication is enforced by middleware before these handlers run.
    /// </summary>
    public static IEndpointRouteBuilder MapMistakeHttpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");
        group.MapGet("/mistakes/{id:guid}", GetMistakeItem);
        group.MapGet("/mistakes/by-upload/{sourceUploadId}", GetMistakeItemsByUpload);
        group.MapGet("/mistakes", GetMistakeItemList);
        group.MapGet("/mistakes/pending-reviews", GetPendingReviewUploads);
        group.MapPost("/mistakes/{id:guid}/review", ReviewMistakeItem);
        group.MapPut("/mistakes/{id:guid}", UpdateMistakeItem);
        group.MapDelete("/mistakes/{id:guid}", DeleteMistakeItem);
        group.MapPost("/mistakes/{id:guid}/practices", AddMistakePractice);
        group.MapGet("/mistakes/{id:guid}/practices", GetMistakePractices);
        group.MapPost("/mistakes/{id:guid}/process-logs", LogMistakeProcess);
        group.MapGet("/mistakes/{id:guid}/process-logs", GetMistakeProcessHistory);
        group.MapPost("/mistakes/presigned-url", GetPresignedUrl);
        return app;
    }

    private static IResult ErrorKind(string kind, int status) =>
        Results.Json(new { success = false, errorKind = kind }, statusCode: status);

    // ===== Endpoint methods =====


    // GET /api/mistakes/{id:guid}
    private static async Task<IResult> GetMistakeItem(
        Guid id,
        ClaimsPrincipal user,
        IMistakeDomainService mistakeService)
    {
        var item = await mistakeService.GetMistakeItemAsync(id);
        if (item == null)
            return MistakeHttpResponse.NotFound("错题条目不存在");

        // Conversation-backed items are caller-scoped; only an authenticated
        // product caller may read them.
        if (item.ConversationSource != null && !IsProductCaller(user))
            return ErrorKind("authentication_required", 401);

        return MistakeHttpResponse.Ok(MapMistakeItemDto(item));
    }

    // GET /api/mistakes/by-upload/{sourceUploadId}
    private static async Task<IResult> GetMistakeItemsByUpload(
        string sourceUploadId,
        IMistakeDomainService mistakeService)
    {
        var items = await mistakeService.GetMistakeItemsByUploadAsync(sourceUploadId);
        return MistakeHttpResponse.Ok(new
        {
            items = items.Select(MapMistakeItemDto).ToList()
        });
    }

    // GET /api/mistakes
    private static async Task<IResult> GetMistakeItemList(
        [FromQuery] string? studentId,
        [FromQuery] int subject,
        [FromQuery] int grade,
        [FromQuery] int reviewStatus,
        [FromQuery] int page,
        [FromQuery] int size,
        IMistakeDomainService mistakeService)
    {
        var normalizedPage = page > 0 ? page : 1;
        var normalizedSize = size > 0 ? Math.Min(size, 100) : 10;

        var (items, totalCount) = await mistakeService.GetMistakeItemListAsync(
            studentId ?? string.Empty,
            subject,
            grade,
            reviewStatus,
            normalizedPage,
            normalizedSize);

        return MistakeHttpResponse.Ok(BuildPageResult(items, totalCount, normalizedPage, normalizedSize));
    }

    private static bool IsProductCaller(ClaimsPrincipal user) => user.Identities.Any(identity => identity.IsAuthenticated && identity.AuthenticationType == "ProductCaller");

    private static MistakeItemPageResult BuildPageResult(
        List<MistakeItemModel> items, int totalCount, int page, int size)
    {
        var result = new MistakeItemPageResult
        {
            PageMeta = new PageMetaDto
            {
                Page = page,
                Size = size,
                TotalCount = totalCount,
                TotalPages = (totalCount + size - 1) / size
            }
        };
        result.Items.AddRange(items.Select(MapMistakeItemDto));
        return result;
    }

    // GET /api/mistakes/pending-reviews
    private static async Task<IResult> GetPendingReviewUploads(
        [FromQuery] int page,
        [FromQuery] int size,
        [FromQuery] string? subjects,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var normalizedPage = page > 0 ? page : 1;
        var normalizedSize = size > 0 ? Math.Min(size, 100) : 10;

        List<int>? subjectList = null;
        if (!string.IsNullOrWhiteSpace(subjects))
        {
            subjectList = subjects
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1)
                .Where(n => n > 0)
                .ToList();
            if (subjectList.Count == 0) subjectList = null;
        }

        var (uploads, totalCount) = await mistakeService.GetPendingReviewUploadsAsync(normalizedPage, normalizedSize, subjectList, ct);

        var result = new PendingReviewUploadListResponse
        {
            PageMeta = new PageMetaDto
            {
                Page = normalizedPage,
                Size = normalizedSize,
                TotalCount = totalCount,
                TotalPages = (totalCount + normalizedSize - 1) / normalizedSize
            }
        };
        result.Uploads.AddRange(uploads.Select(u => new PendingReviewUploadDto
        {
            SourceUploadId = u.SourceUploadId,
            StudentId = u.StudentId,
            Subject = u.Subject,
            Grade = u.Grade,
            PendingCount = u.PendingCount,
            TotalCount = u.TotalCount,
            EarliestCreatedAt = u.EarliestCreatedAt == DateTimeOffset.MinValue
                ? string.Empty
                : u.EarliestCreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        }));
        return MistakeHttpResponse.Ok(result);
    }

    // POST /api/mistakes/{id:guid}/review
    // Rejection is persisted locally (status + process log); images stay in place.
    private static async Task<IResult> ReviewMistakeItem(
        Guid id,
        [FromBody] ReviewMistakeItemRequest request,
        ClaimsPrincipal user,
        IMistakeDomainService mistakeService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (request.ReviewStatus != ReviewStatus.Confirmed && request.ReviewStatus != ReviewStatus.Rejected)
            return MistakeHttpResponse.BadRequest("审核结果只能是确认或退回");

        var beforeReview = await mistakeService.GetMistakeItemAsync(id);
        if (beforeReview?.SourceUploadId?.StartsWith("extract:", StringComparison.Ordinal) == true)
            return ErrorKind("extraction_review_endpoint_required", 409);
        var conversation = beforeReview?.ConversationSource != null;
        if (conversation && !IsProductCaller(user))
            return ErrorKind("authentication_required", 401);

        try
        {
            var item = await mistakeService.ReviewMistakeItemAsync(
                id,
                (int)request.ReviewStatus,
                request.ReviewerId,
                conversation ? request.RootCause ?? beforeReview!.RootCause : request.RootCause,
                request.Grade > 0 ? request.Grade : null,
                request.Subject > 0 ? request.Subject : null,
                conversation ? request.ReturnReason : null);

            if (item == null)
                return MistakeHttpResponse.Ok(new ReviewMistakeItemResponse { Success = false, ErrorMessage = "错题不存在" });

            logger.LogInformation("ReviewMistakeItem: {Status} MistakeItem {ItemId}", request.ReviewStatus, item.Id);
            return MistakeHttpResponse.Ok(new ReviewMistakeItemResponse
            {
                Success = true,
                SourceUploadId = item.SourceUploadId ?? string.Empty,
                StudentId = item.StudentId ?? string.Empty
            });
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }

    // PUT /api/mistakes/{id:guid}
    private static async Task<IResult> UpdateMistakeItem(
        Guid id,
        [FromBody] UpdateMistakeItemRequest request,
        IMistakeDomainService mistakeService)
    {
        var sourceRegions = request.SourceRegions.Count > 0
            ? request.SourceRegions.Select(r => new SourceRegionModel
            {
                SourceImagePath = r.SourceImagePath,
                BoundingBox = r.BoundingBox != null
                    ? new BoundingBoxModel
                    {
                        X1 = r.BoundingBox.X1,
                        Y1 = r.BoundingBox.Y1,
                        X2 = r.BoundingBox.X2,
                        Y2 = r.BoundingBox.Y2
                    }
                    : null
            }).ToList()
            : null;

        try
        {
            var updatedItem = await mistakeService.UpdateMistakeItemPartialAsync(
                id,
                studentId: string.IsNullOrWhiteSpace(request.StudentId) ? null : request.StudentId,
                subject: request.Subject > 0 ? request.Subject : null,
                grade: request.Grade > 0 ? request.Grade : null,
                sourceRegions: sourceRegions);

            if (updatedItem == null)
                return MistakeHttpResponse.NotFound("错题条目不存在");

            return MistakeHttpResponse.Ok(MapMistakeItemDto(updatedItem));
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }

    // DELETE /api/mistakes/{id:guid}
    private static async Task<IResult> DeleteMistakeItem(
        Guid id,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        try
        {
            var success = await mistakeService.DeleteMistakeItemAsync(id);
            return MistakeHttpResponse.Ok(new { success = success });
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }


    // POST /api/mistakes/{id:guid}/practices
    private static async Task<IResult> AddMistakePractice(
        Guid id,
        [FromBody] AddMistakePracticeRequest request,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var practice = new MistakePracticeModel
        {
            MistakeItemId = id,
            StudentId = request.StudentId,
            StudentAnswer = request.StudentAnswer,
            IsCorrect = request.IsCorrect,
            Comments = request.Comments
        };

        var imagePaths = request.ImagePaths?.Count > 0
            ? new List<string>(request.ImagePaths)
            : null;

        try
        {
            var result = await mistakeService.AddPracticeAsync(practice, imagePaths);
            return MistakeHttpResponse.Ok(MapPracticeDto(result));
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }

    // GET /api/mistakes/{id:guid}/practices
    private static async Task<IResult> GetMistakePractices(
        Guid id,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var practices = await mistakeService.GetPracticesAsync(id);
        var response = new MistakePracticeListResponse();
        response.Practices.AddRange(practices.Select(MapPracticeDto));
        return MistakeHttpResponse.Ok(response);
    }

    // POST /api/mistakes/{id:guid}/process-logs
    private static async Task<IResult> LogMistakeProcess(
        Guid id,
        [FromBody] LogMistakeProcessRequest request,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var success = await mistakeService.LogMistakeProcessAsync(
            id,
            request.StudentId,
            request.Action,
            request.Details ?? string.Empty);

        return MistakeHttpResponse.Ok(new { success = success });
    }

    // GET /api/mistakes/{id:guid}/process-logs
    private static async Task<IResult> GetMistakeProcessHistory(
        Guid id,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var logs = await mistakeService.GetMistakeProcessHistoryAsync(id);
        var response = new ProcessLogListResponse();
        response.Logs.AddRange(logs.Select(MapProcessLogDto));
        return MistakeHttpResponse.Ok(response);
    }

    private static bool IsExtractionObjectPath(string objectPath)
    {
        var segments = new List<string>();
        foreach (var segment in objectPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return segments.Count >= 2 && string.Equals(segments[0], "mistakes", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "extractions", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConversationObjectPath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in Uri.UnescapeDataString(path).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return path.StartsWith("mistakes/conversation/", StringComparison.OrdinalIgnoreCase)
            || segments.Count >= 2 && string.Equals(segments[0], "mistakes", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "conversation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanonicalConversationObjectPath(string path, string caller)
    {
        if (path.Length > 300) return false;
        var parts = path.Split('/');
        var callerHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(caller)));
        if (parts.Length != 6 || parts[0] != "mistakes" || parts[1] != "conversation" || parts[2] != callerHash
            || !Hash(parts[3]) || !Guid.TryParseExact(parts[4], "D", out var conversation) || conversation.ToString("D") != parts[4]) return false;
        var image = parts[5].Split('.');
        return image.Length == 2 && Hash(image[0]) && image[1] is "jpg" or "png" or "webp";
        static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    // POST /api/mistakes/presigned-url
    private static async Task<IResult> GetPresignedUrl(
        [FromBody] GetPresignedUrlRequest request,
        ClaimsPrincipal user,
        IOssService ossService,
        IMistakeDomainService mistakeService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (string.IsNullOrWhiteSpace(request.ObjectPath))
            return MistakeHttpResponse.BadRequest("object_path is required");
        if (IsConversationObjectPath(request.ObjectPath))
        {
            var product = user.Identities.FirstOrDefault(identity => identity.IsAuthenticated && identity.AuthenticationType == "ProductCaller");
            var caller = product?.FindFirst("emenda:caller")?.Value;
            if (string.IsNullOrWhiteSpace(caller) || !IsCanonicalConversationObjectPath(request.ObjectPath, caller)
                || !await mistakeService.IsConversationObjectReferencedAsync(request.ObjectPath, caller, ct))
                return ErrorKind("conversation_image_forbidden", 403);
            if (!string.IsNullOrWhiteSpace(request.Size) && !string.Equals(request.Size, "full", StringComparison.OrdinalIgnoreCase))
                return ErrorKind("conversation_full_only", 400);
            var seconds = request.ExpirySeconds > 0 ? request.ExpirySeconds : 3600;
            try
            {
                if (!await ossService.ObjectExistsAsync(request.ObjectPath).WaitAsync(ct))
                    return MistakeHttpResponse.NotFound("Conversation image is unavailable");
                var url = await ossService.GetPresignedUrlAsync(request.ObjectPath, seconds).WaitAsync(ct);
                return MistakeHttpResponse.Ok(new PresignedUrlResponse { Url = url, ExpirySeconds = seconds });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return ErrorKind("conversation_image_unavailable", 503); }
        }
        if (request.ObjectPath.StartsWith("mistakes/extractions/", StringComparison.Ordinal) || IsExtractionObjectPath(request.ObjectPath))
            return ErrorKind("extraction_read_endpoint_required", 403);
        if (!request.ObjectPath.StartsWith("mistakes/", StringComparison.Ordinal))
            return MistakeHttpResponse.BadRequest("object_path must start with mistakes/");

        var expirySeconds = request.ExpirySeconds > 0 ? request.ExpirySeconds : 3600;
        var size = string.IsNullOrWhiteSpace(request.Size) ? "full" : request.Size.ToLowerInvariant();

        try
        {
            var exists = await ossService.ObjectExistsAsync(request.ObjectPath).ConfigureAwait(false);
            if (!exists)
                return MistakeHttpResponse.NotFound($"Object not found: {request.ObjectPath}");

            // 原图请求：直接返回
            if (size == "full")
            {
                var url = await ossService.GetPresignedUrlAsync(request.ObjectPath, expirySeconds).ConfigureAwait(false);
                return MistakeHttpResponse.Ok(new PresignedUrlResponse { Url = url, ExpirySeconds = expirySeconds });
            }

            // 缩略图请求：使用 ThumbnailHelper
            var (thumbnailUrl, expiry) = await ThumbnailHelper.GetOrGenerateThumbnailAsync(
                request.ObjectPath, size, ossService, expirySeconds, logger).ConfigureAwait(false);
            return MistakeHttpResponse.Ok(new PresignedUrlResponse { Url = thumbnailUrl, ExpirySeconds = expiry });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetPresignedUrl: failed for path {Path}", request.ObjectPath);
            return MistakeHttpResponse.Internal($"Failed to generate presigned URL: {ex.Message}");
        }
    }

    // ===== Mapping helpers (Domain models → DTOs) =====

    private static MistakeItemDto MapMistakeItemDto(MistakeItemModel item)
    {
        var dto = new MistakeItemDto
        {
            ConversationSource = item.ConversationSource == null ? null : System.Text.Json.JsonSerializer.Deserialize<Emenda.HttpContracts.ConversationQuestionSnapshot>(item.ConversationSource, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            ConversationActive = item.ConversationActive,
            Id = item.Id.ToString(),
            StudentId = item.StudentId ?? string.Empty,
            Subject = item.Subject,
            Grade = item.Grade,
            SourceUploadId = item.SourceUploadId ?? string.Empty,
            ReviewStatus = (ReviewStatus)item.ReviewStatus,
            ReviewerId = item.ReviewerId ?? string.Empty,
            ReviewedAt = item.ReviewedAt.HasValue
                ? item.ReviewedAt.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                : string.Empty,
            RootCause = item.RootCause ?? string.Empty,
            QuestionId = item.QuestionId ?? string.Empty,
            CreatedAt = item.CreatedAt == DateTimeOffset.MinValue
                ? string.Empty
                : item.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            UpdatedAt = item.UpdatedAt.HasValue
                ? item.UpdatedAt.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                : string.Empty
        };

        foreach (var region in item.SourceRegions ?? new())
        {
            var regionDto = new SourceRegionDto
            {
                SourceImagePath = region.SourceImagePath ?? string.Empty
            };

            if (region.BoundingBox != null)
            {
                regionDto.BoundingBox = new BoundingBoxDto
                {
                    X1 = region.BoundingBox.X1,
                    Y1 = region.BoundingBox.Y1,
                    X2 = region.BoundingBox.X2,
                    Y2 = region.BoundingBox.Y2
                };
            }

            dto.SourceRegions.Add(regionDto);
        }

        return dto;
    }

    private static MistakePracticeDto MapPracticeDto(MistakePracticeModel practice)
    {
        var dto = new MistakePracticeDto
        {
            Id = practice.Id.ToString(),
            MistakeItemId = practice.MistakeItemId.ToString(),
            StudentId = practice.StudentId ?? string.Empty,
            PracticeDate = practice.PracticeDate == DateTimeOffset.MinValue
                ? string.Empty
                : practice.PracticeDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            StudentAnswer = practice.StudentAnswer ?? string.Empty,
            IsCorrect = practice.IsCorrect,
            Comments = practice.Comments ?? string.Empty
        };
        dto.ImagePaths.AddRange(practice.ImagePaths ?? new List<string>());
        return dto;
    }

    private static ProcessLogDto MapProcessLogDto(ProcessLogModel log)
    {
        return new ProcessLogDto
        {
            Id = log.Id.ToString(),
            MistakeItemId = log.MistakeItemId.ToString(),
            Action = (ProcessAction)log.Action,
            Details = log.Details ?? string.Empty,
            CreatedAt = log.CreatedAt == DateTimeOffset.MinValue
                ? string.Empty
                : log.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            StudentId = log.StudentId ?? string.Empty
        };
    }


}
