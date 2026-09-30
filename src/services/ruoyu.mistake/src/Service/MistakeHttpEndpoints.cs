using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Dtos;
using Ruoyu.Study.Mistake.Service.StudentIntegration;
using MistakeItemDto = Ruoyu.Study.Mistake.Service.Dtos.MistakeItemDto;
using ReanalyzeJobDto = Ruoyu.Study.Mistake.Service.Dtos.ReanalyzeJobDto;
using MistakeItemPageResult = Ruoyu.Study.Mistake.Service.Dtos.MistakeItemPageResult;
using PendingReviewUploadDto = Ruoyu.Study.Mistake.Service.Dtos.PendingReviewUploadDto;

namespace Ruoyu.Study.Mistake.Service;

/// <summary>
/// Mistake HTTP 端点（18 个，由 gRPC MistakeServiceImpl 18 RPC 1:1 迁移）。
/// 业务逻辑委托给 IMistakeDomainService / IReanalyzeJobService，与 MistakeServiceImpl 完全一致，
/// 仅做协议适配（proto → JSON）。
/// </summary>
public static partial class MistakeHttpEndpoints
{
    /// <summary>
    /// Maps all 18 business endpoints (the /api group).
    /// When <paramref name="requireAuthenticatedApi"/> is true (the
    /// IdentityService:MistakeApiStrictAuth startup snapshot), the /api group
    /// requires an authenticated principal (shared-audience or Admin AppId
    /// audience scheme, no role check); the default false keeps the anonymous
    /// compatibility window identical to the baseline. The root endpoint and
    /// future health endpoints stay anonymous in both modes.
    /// </summary>
    public static IEndpointRouteBuilder MapMistakeHttpEndpoints(
        this IEndpointRouteBuilder app, bool requireAuthenticatedApi = false)
    {
        var group = app.MapGroup("/api");

        if (requireAuthenticatedApi)
            group.RequireAuthorization(MistakeApiAuthorization.PolicyName);

        // ===== MistakeItem endpoints =====
        // 1. SubmitMistakeUpload — POST /api/mistakes/upload
        group.MapPost("/mistakes/upload", SubmitMistakeUpload);
        // 2. GetMistakeItem — GET /api/mistakes/{id:guid}
        group.MapGet("/mistakes/{id:guid}", GetMistakeItem);
        // 3. GetMistakeItemsByUpload — GET /api/mistakes/by-upload/{sourceUploadId}
        group.MapGet("/mistakes/by-upload/{sourceUploadId}", GetMistakeItemsByUpload);
        // 4. GetMistakeItemList — GET /api/mistakes
        group.MapGet("/mistakes", GetMistakeItemList);
        // 5. GetPendingReviewUploads — GET /api/mistakes/pending-reviews
        group.MapGet("/mistakes/pending-reviews", GetPendingReviewUploads);
        // 6. ReviewMistakeItem — POST /api/mistakes/{id:guid}/review
        group.MapPost("/mistakes/{id:guid}/review", ReviewMistakeItem);
        // 7. ReanalyzeMistakeItem — POST /api/mistakes/{id:guid}/reanalyze
        group.MapPost("/mistakes/{id:guid}/reanalyze", ReanalyzeMistakeItem);
        // 8. GetReanalyzeJobs — POST /api/mistakes/reanalyze/jobs
        group.MapPost("/mistakes/reanalyze/jobs", GetReanalyzeJobs);
        // 9. GetActiveReanalyzeJobs — GET /api/mistakes/reanalyze/active
        group.MapGet("/mistakes/reanalyze/active", GetActiveReanalyzeJobs);
        // 10. AddMistakeItem — POST /api/mistakes
        group.MapPost("/mistakes", AddMistakeItem);
        // 11. UpdateMistakeItem — PUT /api/mistakes/{id:guid}
        group.MapPut("/mistakes/{id:guid}", UpdateMistakeItem);
        // 12. DeleteMistakeItem — DELETE /api/mistakes/{id:guid}
        group.MapDelete("/mistakes/{id:guid}", DeleteMistakeItem);
        // 13. CompleteUploadReview — POST /api/mistakes/complete-review (Obsolete)
#pragma warning disable CS0618
        group.MapPost("/mistakes/complete-review", CompleteUploadReview);
#pragma warning restore CS0618
        // 14. AddMistakePractice — POST /api/mistakes/{id:guid}/practices
        group.MapPost("/mistakes/{id:guid}/practices", AddMistakePractice);
        // 15. GetMistakePractices — GET /api/mistakes/{id:guid}/practices
        group.MapGet("/mistakes/{id:guid}/practices", GetMistakePractices);
        // 16. LogMistakeProcess — POST /api/mistakes/{id:guid}/process-logs
        group.MapPost("/mistakes/{id:guid}/process-logs", LogMistakeProcess);
        // 17. GetMistakeProcessHistory — GET /api/mistakes/{id:guid}/process-logs
        group.MapGet("/mistakes/{id:guid}/process-logs", GetMistakeProcessHistory);
        // 18. GetPresignedUrl — POST /api/mistakes/presigned-url
        group.MapPost("/mistakes/presigned-url", GetPresignedUrl);

        return app;
    }

    // ===== Endpoint methods =====

    // 1. POST /api/mistakes/upload
    private static async Task<IResult> SubmitMistakeUpload(
        [FromBody] SubmitMistakeUploadRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (resourceAuthz.Enabled)
        {
            var decision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/upload", user, MistakeResourceAccessGate.AdminOnly);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

        logger.LogDebug("SubmitMistakeUpload called: studentId={StudentId}, subject={Subject}, grade={Grade}, sourceUploadId={SourceUploadId}, imagePaths={ImagePaths}",
            request.StudentId, request.Subject, request.Grade, request.SourceUploadId, string.Join(" | ", request.ImagePaths.Select((p, i) => $"[{i}]={p}")));

        if (string.IsNullOrWhiteSpace(request.StudentId))
            return MistakeHttpResponse.BadRequest("缺少学生ID");

        if (request.Subject <= 0 || request.Subject > 9)
            return MistakeHttpResponse.BadRequest("学科值无效（有效范围：1-9）");

        if (request.Grade <= 0 || request.Grade > 12)
            return MistakeHttpResponse.BadRequest("年级值无效（有效范围：1-12）");

        if (string.IsNullOrWhiteSpace(request.SourceUploadId))
            return MistakeHttpResponse.BadRequest("缺少上传记录ID");

        if (request.ImagePaths.Count == 0)
            return MistakeHttpResponse.BadRequest("至少需要提供一张图片");

        try
        {
            var imagePaths = new List<string>(request.ImagePaths);
            var items = await mistakeService.SubmitMistakeUploadAsync(
                request.StudentId, request.Subject, request.Grade,
                imagePaths, request.RootCause, request.SourceUploadId);

            var response = new SubmitMistakeUploadResponse { Success = true };
            response.CreatedItemIds.AddRange(items.Select(i => i.Id.ToString()));

            logger.LogDebug("SubmitMistakeUpload success: created {ItemCount} items, ids={ItemIds}",
                items.Count, string.Join(",", items.Select(i => i.Id.ToString())));

            return MistakeHttpResponse.Ok(response);
        }
        catch (MistakeValidationException ex)
        {
            logger.LogWarning("SubmitMistakeUpload validation failed: {Message}", ex.Message);
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }

    // 2. GET /api/mistakes/{id:guid}
    private static async Task<IResult> GetMistakeItem(
        Guid id,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var item = await mistakeService.GetMistakeItemAsync(id);
        if (item == null)
            return MistakeHttpResponse.NotFound("错题条目不存在");

        if (resourceAuthz.Enabled)
        {
            var decision = await resourceAuthz.DecideItemOwnershipAsync(
                "GET /api/mistakes/{id}", user, id.ToString(), item.StudentId, ct);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

        return MistakeHttpResponse.Ok(MapMistakeItemDto(item));
    }

    // 3. GET /api/mistakes/by-upload/{sourceUploadId}
    private static async Task<IResult> GetMistakeItemsByUpload(
        string sourceUploadId,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var items = await mistakeService.GetMistakeItemsByUploadAsync(sourceUploadId);

        if (resourceAuthz.Enabled)
        {
            var decision = await resourceAuthz.DecideItemsOwnershipAsync(
                "GET /api/mistakes/by-upload/{sourceUploadId}", user, sourceUploadId,
                items.Select(i => i.StudentId), ct);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

        return MistakeHttpResponse.Ok(new
        {
            items = items.Select(MapMistakeItemDto).ToList()
        });
    }

    // 4. GET /api/mistakes
    private static async Task<IResult> GetMistakeItemList(
        [FromQuery] string? studentId,
        [FromQuery] int subject,
        [FromQuery] int grade,
        [FromQuery] int reviewStatus,
        [FromQuery] int page,
        [FromQuery] int size,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        var normalizedPage = page > 0 ? page : 1;
        var normalizedSize = size > 0 ? Math.Min(size, 100) : 10;

        // Learner default scope: with resource authorization on and no
        // studentId parameter, the query is filtered by the caller's allowed
        // students so no cross-student row can leak (TotalCount included).
        List<MistakeItemModel> items;
        int totalCount;
        if (resourceAuthz.Enabled)
        {
            var scopeDecision = await resourceAuthz.DecideListScopeAsync(
                "GET /api/mistakes", user, studentId, ct);
            if (!scopeDecision.Allowed)
                return scopeDecision.ToHttpResponse();
            if (scopeDecision.ScopeStudentIds is { } allowedStudents)
            {
                (items, totalCount) = await mistakeService.GetMistakeItemListByStudentsAsync(
                    allowedStudents, subject, grade, reviewStatus, normalizedPage, normalizedSize);
                return MistakeHttpResponse.Ok(BuildPageResult(items, totalCount, normalizedPage, normalizedSize));
            }
        }

        (items, totalCount) = await mistakeService.GetMistakeItemListAsync(
            studentId ?? string.Empty,
            subject,
            grade,
            reviewStatus,
            normalizedPage,
            normalizedSize);

        return MistakeHttpResponse.Ok(BuildPageResult(items, totalCount, normalizedPage, normalizedSize));
    }

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

    // 5. GET /api/mistakes/pending-reviews
    private static async Task<IResult> GetPendingReviewUploads(
        [FromQuery] int page,
        [FromQuery] int size,
        [FromQuery] string? subjects,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService)
    {
        if (resourceAuthz.Enabled)
        {
            var decision = resourceAuthz.DecideRoleGate(
                "GET /api/mistakes/pending-reviews", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

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

        var (uploads, totalCount) = await mistakeService.GetPendingReviewUploadsAsync(normalizedPage, normalizedSize, subjectList);

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

    // 6. POST /api/mistakes/{id:guid}/review
    private static async Task<IResult> ReviewMistakeItem(
        Guid id,
        [FromBody] ReviewMistakeItemRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        IUploadSourceGateway studentClient,
        IOssService ossService,
        StudentCapabilityOptions studentCapability,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (resourceAuthz.Enabled)
        {
            var roleDecision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/{id}/review", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideReviewerBinding(
                "POST /api/mistakes/{id}/review", user, request.ReviewerId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();
        }

        if (request.ReviewStatus != ReviewStatus.Confirmed && request.ReviewStatus != ReviewStatus.Rejected)
            return MistakeHttpResponse.BadRequest("审核结果只能是确认或退回");

        // Fail closed before any domain, OSS or Student change: the reject flow
        // migrates images, deletes the item and creates a Student returned
        // record, none of which is safe when the Student capability is off.
        if (!studentCapability.Enabled && request.ReviewStatus == ReviewStatus.Rejected)
        {
            logger.LogWarning(
                "ReviewMistakeItem: rejected review for item {ItemId} rejected before any side effect because the Student capability is disabled",
                id);
            return MistakeHttpResponse.ServiceUnavailable("Student 集成未启用，退回审核不可用（Mistake:Student:Enabled=false）");
        }

        try
        {
            int? grade = request.Grade > 0 ? request.Grade : null;
            int? subject = request.Subject > 0 ? request.Subject : null;
            var item = await mistakeService.ReviewMistakeItemAsync(
                id,
                (int)request.ReviewStatus,
                request.ReviewerId,
                request.RootCause,
                grade,
                subject);

            if (item == null)
                return MistakeHttpResponse.Ok(new ReviewMistakeItemResponse { Success = false, ErrorMessage = "错题不存在" });

            if (request.ReviewStatus == ReviewStatus.Confirmed)
            {
                // Confirm flow: images are already in mistakes/ from the polling worker.
                // No migration or Student record update needed here.
                logger.LogInformation("ReviewMistakeItem: Confirmed MistakeItem {ItemId}", item.Id);
            }
            else if (request.ReviewStatus == ReviewStatus.Rejected)
            {
                // Reject flow: migrate images back from mistakes/ to uploads/ and create a returned record
                var rejectedImagePaths = item.SourceRegions
                    .Where(r => !string.IsNullOrWhiteSpace(r.SourceImagePath))
                    .Select(r => r.SourceImagePath)
                    .ToList();

                // Migrate images back: mistakes/ -> uploads/
                var migratedBackPaths = new List<string>();
                foreach (var mistakePath in rejectedImagePaths)
                {
                    try
                    {
                        if (mistakePath.StartsWith("mistakes/", StringComparison.OrdinalIgnoreCase))
                        {
                            var uploadPath = "uploads/" + mistakePath.Substring("mistakes/".Length);
                            await ossService.CopyObjectAsync(mistakePath, uploadPath).ConfigureAwait(false);
                            await ossService.DeleteAsync(mistakePath).ConfigureAwait(false);
                            migratedBackPaths.Add(uploadPath);
                            logger.LogDebug("ReviewMistakeItem: Migrated rejected image back {MistakePath} -> {UploadPath}",
                                mistakePath, uploadPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "ReviewMistakeItem: Failed to migrate rejected image back {Path}", mistakePath);
                    }
                }

                // Delete MistakeItem (cascades to practice and process_log)
                await mistakeService.DeleteMistakeItemAsync(item.Id);
                logger.LogInformation("ReviewMistakeItem: Rejected and deleted MistakeItem {ItemId}", item.Id);

                // Create a returned upload record in Student so the student can re-upload.
                // return_reason is passed through to student service and written to
                // image_upload_record.return_reason (NOT concatenated into comments, to avoid
                // confusing the mistake root_cause with the teacher's return reason).
                if (migratedBackPaths.Count > 0 && !string.IsNullOrWhiteSpace(item.StudentId))
                {
                    try
                    {
                        var imageEntries = migratedBackPaths.Select(p => new UploadImageEntry
                        {
                            Path = p,
                            Type = "mistake"
                        }).ToList();
                        await studentClient.CreateReturnedRecordAsync(
                            item.StudentId,
                            imageEntries,
                            "Rejected",
                            request.ReturnReason ?? string.Empty,
                            CancellationToken.None).ConfigureAwait(false);
                        logger.LogInformation("ReviewMistakeItem: Created returned record for student {StudentId} with {Count} images",
                            item.StudentId, migratedBackPaths.Count);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "ReviewMistakeItem: Failed to create returned record for student {StudentId}",
                            item.StudentId);
                    }
                }
            }

            var response = new ReviewMistakeItemResponse
            {
                Success = true,
                SourceUploadId = item.SourceUploadId ?? string.Empty,
                StudentId = item.StudentId ?? string.Empty
            };

            return MistakeHttpResponse.Ok(response);
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }

    // 7. POST /api/mistakes/{id:guid}/reanalyze
    private static async Task<IResult> ReanalyzeMistakeItem(
        Guid id,
        [FromBody] ReanalyzeMistakeItemRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IReanalyzeJobService reanalyzeJobService,
        IMistakeDomainService mistakeService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (resourceAuthz.Enabled)
        {
            var roleDecision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/{id}/reanalyze", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideReviewerBinding(
                "POST /api/mistakes/{id}/reanalyze", user, request.ReviewerId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();

            // Student binding: the request studentId must match the stored
            // owner, and the persisted job uses the stored owner so the
            // request parameter can never re-assign the analysis target.
            var item = await mistakeService.GetMistakeItemAsync(id);
            if (item != null)
            {
                var studentDecision = resourceAuthz.DecideOwnedWriteBinding(
                    "POST /api/mistakes/{id}/reanalyze", user, id.ToString(),
                    request.StudentId, item.StudentId);
                if (!studentDecision.Allowed)
                    return studentDecision.ToHttpResponse();
                request.StudentId = item.StudentId;
            }
        }

        logger.LogInformation(
            "ReanalyzeMistakeItem (async job) called: id={ItemId}, reviewerId={ReviewerId}, studentId={StudentId}, hasTeacherNote={HasNote}",
            id, request.ReviewerId, request.StudentId, !string.IsNullOrWhiteSpace(request.TeacherDescription));

        // 1. Validate request shape
        if (id == Guid.Empty)
        {
            return MistakeHttpResponse.BadRequest("错题 ID 不能为空");
        }
        if (string.IsNullOrWhiteSpace(request.ReviewerId))
        {
            return MistakeHttpResponse.BadRequest("未识别教师身份");
        }
        if (string.IsNullOrWhiteSpace(request.StudentId))
        {
            return MistakeHttpResponse.BadRequest("缺少学生ID");
        }

        // 2. Enqueue the job. The ReanalyzeJobService performs item validation,
        //    snapshots previous root_cause, and persists a Pending job in a single
        //    transaction. It does NOT call VL — that is done by the hosted worker.
        var result = await reanalyzeJobService.StartJobAsync(
            id,
            request.ReviewerId,
            request.StudentId,
            request.TeacherDescription,
            ct);

        if (!result.Success)
        {
            // NotFound and InvalidArgument are surfaced as HTTP 404/400 so the
            // BFF can map them directly. FailedPrecondition is a 422
            // business validation that we return as success=false (preserving
            // the original gRPC behavior where only NotFound/InvalidArgument
            // threw, and others returned success=false).
            if (result.ErrorCode == "NotFound")
                return MistakeHttpResponse.NotFound(result.ErrorMessage ?? "重新分析失败");
            if (result.ErrorCode == "InvalidArgument")
                return MistakeHttpResponse.BadRequest(result.ErrorMessage ?? "重新分析失败");
            return MistakeHttpResponse.Ok(new ReanalyzeMistakeItemResponse
            {
                Success = false,
                ErrorMessage = result.ErrorMessage ?? "重新分析失败"
            });
        }

        return MistakeHttpResponse.Ok(new ReanalyzeMistakeItemResponse
        {
            Success = true,
            JobId = result.Job!.Id.ToString()
        });
    }

    // 8. POST /api/mistakes/reanalyze/jobs
    private static async Task<IResult> GetReanalyzeJobs(
        [FromBody] GetReanalyzeJobsRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IReanalyzeJobService reanalyzeJobService)
    {
        if (resourceAuthz.Enabled)
        {
            var roleDecision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/reanalyze/jobs", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideReviewerBinding(
                "POST /api/mistakes/reanalyze/jobs", user, request.ReviewerId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();
        }

        var reviewerId = request.ReviewerId;
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return MistakeHttpResponse.Ok(new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "未识别教师身份"
            });
        }

        if (request.JobIds == null || request.JobIds.Count == 0)
        {
            return MistakeHttpResponse.Ok(new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "缺少 ids 参数"
            });
        }
        if (request.JobIds.Count > 100)
        {
            return MistakeHttpResponse.Ok(new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "ids 数量必须在 1-100 之间"
            });
        }

        var ids = new List<Guid>(request.JobIds.Count);
        foreach (var raw in request.JobIds)
        {
            if (Guid.TryParse(raw, out var parsed)) ids.Add(parsed);
        }

        var jobs = await reanalyzeJobService.GetJobsAsync(reviewerId, ids, CancellationToken.None);
        return MistakeHttpResponse.Ok(new GetReanalyzeJobsResponse
        {
            Success = true,
            Jobs = jobs.Select(MapJobDto).ToList()
        });
    }

    // 9. GET /api/mistakes/reanalyze/active
    private static async Task<IResult> GetActiveReanalyzeJobs(
        [FromQuery] string? reviewerId,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IReanalyzeJobService reanalyzeJobService)
    {
        if (resourceAuthz.Enabled)
        {
            var roleDecision = resourceAuthz.DecideRoleGate(
                "GET /api/mistakes/reanalyze/active", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideReviewerBinding(
                "GET /api/mistakes/reanalyze/active", user, reviewerId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();
        }

        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return MistakeHttpResponse.Ok(new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "未识别教师身份"
            });
        }
        var jobs = await reanalyzeJobService.GetActiveJobsAsync(reviewerId, CancellationToken.None);
        return MistakeHttpResponse.Ok(new GetReanalyzeJobsResponse
        {
            Success = true,
            Jobs = jobs.Select(MapJobDto).ToList()
        });
    }

    // 10. POST /api/mistakes
    private static async Task<IResult> AddMistakeItem(
        [FromBody] AddMistakeItemRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService)
    {
        if (resourceAuthz.Enabled)
        {
            var decision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

        var item = new MistakeItemModel
        {
            StudentId = request.StudentId,
            Subject = request.Subject,
            Grade = request.Grade,
            SourceUploadId = request.SourceUploadId,
            SourceRegions = request.SourceRegions.Select(r => new SourceRegionModel
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
        };

        try
        {
            var created = await mistakeService.AddMistakeItemAsync(item);
            return MistakeHttpResponse.Ok(MapMistakeItemDto(created));
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.BadRequest(ex.Message);
        }
    }

    // 11. PUT /api/mistakes/{id:guid}
    private static async Task<IResult> UpdateMistakeItem(
        Guid id,
        [FromBody] UpdateMistakeItemRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService)
    {
        if (resourceAuthz.Enabled)
        {
            // Owner migration (studentId change) is admin-only; the gate makes
            // the whole endpoint admin-only under resource authorization.
            var decision = resourceAuthz.DecideRoleGate(
                "PUT /api/mistakes/{id}", user, MistakeResourceAccessGate.AdminOnly);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

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

    // 12. DELETE /api/mistakes/{id:guid}
    private static async Task<IResult> DeleteMistakeItem(
        Guid id,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        if (resourceAuthz.Enabled)
        {
            var item = await mistakeService.GetMistakeItemAsync(id);
            if (item == null)
                return MistakeHttpResponse.Ok(new { success = false });

            var decision = await resourceAuthz.DecideItemOwnershipAsync(
                "DELETE /api/mistakes/{id}", user, id.ToString(), item.StudentId, ct);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

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

    // 13. POST /api/mistakes/complete-review
    [Obsolete("目标流程中不存在独立的 CompleteUploadReview 步骤，每条审核通过时立即迁移。仅保留供 LegacyClean 运维兜底使用。")]
    private static async Task<IResult> CompleteUploadReview(
        [FromBody] CompleteUploadReviewRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        IOssService ossService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (resourceAuthz.Enabled)
        {
            var roleDecision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/complete-review", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideReviewerBinding(
                "POST /api/mistakes/complete-review", user, request.ReviewerId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();
        }

        try
        {
            var result = await mistakeService.CompleteUploadReviewAsync(
                request.SourceUploadId, request.ReviewerId);

            if (!result.Success)
            {
                return MistakeHttpResponse.Ok(new CompleteUploadReviewResponse { Success = false, ErrorMessage = result.ErrorMessage ?? string.Empty });
            }

            var pathMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var migratedImagePaths = new List<string>();
            var imagePathsToRemoveFromRecord = new List<string>();

            // 收集需要迁移的路径
            var pathsToMigrate = new List<string>();
            foreach (var item in result.Items)
            {
                foreach (var region in item.SourceRegions)
                {
                    if (string.IsNullOrWhiteSpace(region.SourceImagePath))
                        continue;

                    if (!imagePathsToRemoveFromRecord.Contains(region.SourceImagePath, StringComparer.OrdinalIgnoreCase))
                    {
                        imagePathsToRemoveFromRecord.Add(region.SourceImagePath);
                    }

                    if (region.SourceImagePath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase)
                        && item.ReviewStatus == (int)ReviewStatus.Confirmed
                        && !pathsToMigrate.Contains(region.SourceImagePath, StringComparer.OrdinalIgnoreCase))
                    {
                        pathsToMigrate.Add(region.SourceImagePath);
                    }
                }
            }

            // Migrate images from uploads/ to mistakes/ directly via OSS
            if (pathsToMigrate.Count > 0)
            {
                foreach (var oldPath in pathsToMigrate)
                {
                    try
                    {
                        var newPath = "mistakes/" + oldPath.Substring("uploads/".Length);
                        await ossService.CopyObjectAsync(oldPath, newPath).ConfigureAwait(false);
                        await ossService.DeleteAsync(oldPath).ConfigureAwait(false);
                        pathMapping[oldPath] = newPath;
                        migratedImagePaths.Add(oldPath);
                        logger.LogDebug("CompleteUploadReview: Migrated {OldPath} -> {NewPath}", oldPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "CompleteUploadReview: Failed to migrate {Path}", oldPath);
                    }
                }
            }

            // 更新 MistakeItem 中的路径引用
            foreach (var item in result.Items)
            {
                bool updated = false;
                foreach (var region in item.SourceRegions)
                {
                    if (string.IsNullOrWhiteSpace(region.SourceImagePath))
                        continue;

                    if (item.ReviewStatus != (int)ReviewStatus.Confirmed)
                    {
                        logger.LogDebug(
                            "Item {ItemId} is not Confirmed (status={Status}), will only remove from upload record, not migrate",
                            item.Id, item.ReviewStatus);
                        continue;
                    }

                    if (pathMapping.TryGetValue(region.SourceImagePath, out var cachedNewPath))
                    {
                        region.SourceImagePath = cachedNewPath;
                        updated = true;
                    }
                }

                if (updated)
                {
                    await mistakeService.UpdateMistakeItemAsync(item).ConfigureAwait(false);
                }
            }

            var response = new CompleteUploadReviewResponse
            {
                Success = true,
                StudentId = result.StudentId ?? ""
            };
            response.RemovedImagePaths.AddRange(imagePathsToRemoveFromRecord);

            return MistakeHttpResponse.Ok(response);
        }
        catch (MistakeValidationException ex)
        {
            return MistakeHttpResponse.Ok(new CompleteUploadReviewResponse { Success = false, ErrorMessage = ex.Message });
        }
    }

    // 14. POST /api/mistakes/{id:guid}/practices
    private static async Task<IResult> AddMistakePractice(
        Guid id,
        [FromBody] AddMistakePracticeRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        if (resourceAuthz.Enabled)
        {
            // Learner-owned write: staff and admin cannot post practices on a
            // student's behalf through this endpoint.
            var roleDecision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/{id}/practices", user, MistakeResourceAccessGate.LearnerOnly);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();

            var item = await mistakeService.GetMistakeItemAsync(id);
            if (item == null)
                return MistakeHttpResponse.Forbidden("无权访问该学生的错题资源");

            var ownershipDecision = await resourceAuthz.DecideItemOwnershipAsync(
                "POST /api/mistakes/{id}/practices", user, id.ToString(), item.StudentId, ct);
            if (!ownershipDecision.Allowed)
                return ownershipDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideOwnedWriteBinding(
                "POST /api/mistakes/{id}/practices", user, id.ToString(), request.StudentId, item.StudentId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();
        }

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

    // 15. GET /api/mistakes/{id:guid}/practices
    private static async Task<IResult> GetMistakePractices(
        Guid id,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        if (resourceAuthz.Enabled)
        {
            var item = await mistakeService.GetMistakeItemAsync(id);
            if (item == null)
                return MistakeHttpResponse.Ok(new MistakePracticeListResponse());

            var decision = await resourceAuthz.DecideItemOwnershipAsync(
                "GET /api/mistakes/{id}/practices", user, id.ToString(), item.StudentId, ct);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

        var practices = await mistakeService.GetPracticesAsync(id);
        var response = new MistakePracticeListResponse();
        response.Practices.AddRange(practices.Select(MapPracticeDto));
        return MistakeHttpResponse.Ok(response);
    }

    // 16. POST /api/mistakes/{id:guid}/process-logs
    private static async Task<IResult> LogMistakeProcess(
        Guid id,
        [FromBody] LogMistakeProcessRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        if (resourceAuthz.Enabled)
        {
            // Learner-owned write: the log's studentId must be the item owner.
            var roleDecision = resourceAuthz.DecideRoleGate(
                "POST /api/mistakes/{id}/process-logs", user, MistakeResourceAccessGate.LearnerOnly);
            if (!roleDecision.Allowed)
                return roleDecision.ToHttpResponse();

            var item = await mistakeService.GetMistakeItemAsync(id);
            if (item == null)
                return MistakeHttpResponse.Forbidden("无权访问该学生的错题资源");

            var ownershipDecision = await resourceAuthz.DecideItemOwnershipAsync(
                "POST /api/mistakes/{id}/process-logs", user, id.ToString(), item.StudentId, ct);
            if (!ownershipDecision.Allowed)
                return ownershipDecision.ToHttpResponse();
            var bindingDecision = resourceAuthz.DecideOwnedWriteBinding(
                "POST /api/mistakes/{id}/process-logs", user, id.ToString(), request.StudentId, item.StudentId);
            if (!bindingDecision.Allowed)
                return bindingDecision.ToHttpResponse();
        }

        var success = await mistakeService.LogMistakeProcessAsync(
            id,
            request.StudentId,
            request.Action,
            request.Details ?? string.Empty);

        return MistakeHttpResponse.Ok(new { success = success });
    }

    // 17. GET /api/mistakes/{id:guid}/process-logs
    private static async Task<IResult> GetMistakeProcessHistory(
        Guid id,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IMistakeDomainService mistakeService,
        CancellationToken ct)
    {
        if (resourceAuthz.Enabled)
        {
            var item = await mistakeService.GetMistakeItemAsync(id);
            if (item == null)
                return MistakeHttpResponse.Ok(new ProcessLogListResponse());

            var decision = await resourceAuthz.DecideItemOwnershipAsync(
                "GET /api/mistakes/{id}/process-logs", user, id.ToString(), item.StudentId, ct);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

        var logs = await mistakeService.GetMistakeProcessHistoryAsync(id);
        var response = new ProcessLogListResponse();
        response.Logs.AddRange(logs.Select(MapProcessLogDto));
        return MistakeHttpResponse.Ok(response);
    }

    // 18. POST /api/mistakes/presigned-url
    private static async Task<IResult> GetPresignedUrl(
        [FromBody] GetPresignedUrlRequest request,
        ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService resourceAuthz,
        IOssService ossService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("MistakeHttpEndpoints");
        if (string.IsNullOrWhiteSpace(request.ObjectPath))
            return MistakeHttpResponse.BadRequest("object_path is required");
        if (!request.ObjectPath.StartsWith("mistakes/", StringComparison.Ordinal))
            return MistakeHttpResponse.BadRequest("object_path must start with mistakes/");

        if (resourceAuthz.Enabled)
        {
            // Learner side: only object paths referenced by an owned item
            // source region or practice image path pass (Ordinal exact);
            // staff/admin keep the review-visibility prefix rule above.
            var decision = await resourceAuthz.DecidePresignedAccessAsync(
                "POST /api/mistakes/presigned-url", user, request.ObjectPath, ct);
            if (!decision.Allowed)
                return decision.ToHttpResponse();
        }

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

    private static ReanalyzeJobDto MapJobDto(ReanalyzeJobModel job) => new()
    {
        JobId = job.Id.ToString(),
        ItemId = job.ItemId.ToString(),
        StudentId = job.StudentId,
        Status = (ReanalyzeJobStatus)(int)job.Status,
        NewRootCause = job.NewRootCause ?? string.Empty,
        ErrorMessage = job.ErrorMessage ?? string.Empty,
        CreatedAt = job.CreatedAt.ToString("o"),
        StartedAt = job.StartedAt?.ToString("o") ?? string.Empty,
        CompletedAt = job.CompletedAt?.ToString("o") ?? string.Empty,
        // Pass through the nullable directly. 0 is a valid (if improbable)
        // value for VL call; the BFF decides how to render it.
        VlDurationMs = job.VlDurationMs ?? -1
    };
}
