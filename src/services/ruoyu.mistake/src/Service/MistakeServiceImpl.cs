using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Services;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Service;

public class MistakeServiceImpl : MistakeGrpcService.MistakeGrpcServiceBase
{
    private readonly IMistakeDomainService _mistakeService;
    private readonly IReanalyzeJobService _reanalyzeJobService;
    private readonly StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient _studentClient;
    private readonly IOssService _ossService;
    private readonly IVlAnalyzer _vlAnalyzer;
    private readonly ILogger<MistakeServiceImpl> _logger;

    public MistakeServiceImpl(
        IMistakeDomainService mistakeService,
        IReanalyzeJobService reanalyzeJobService,
        StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient studentClient,
        IOssService ossService,
        IVlAnalyzer vlAnalyzer,
        ILogger<MistakeServiceImpl> logger)
    {
        _mistakeService = mistakeService;
        _reanalyzeJobService = reanalyzeJobService;
        _studentClient = studentClient;
        _ossService = ossService;
        _vlAnalyzer = vlAnalyzer;
        _logger = logger;
    }

    public override async Task<SubmitMistakeUploadResponse> SubmitMistakeUpload(SubmitMistakeUploadRequest request, ServerCallContext context)
    {
        _logger.LogDebug("SubmitMistakeUpload called: studentId={StudentId}, subject={Subject}, grade={Grade}, sourceUploadId={SourceUploadId}, imagePaths={ImagePaths}",
            request.StudentId, request.Subject, request.Grade, request.SourceUploadId, string.Join(" | ", request.ImagePaths.Select((p, i) => $"[{i}]={p}")));

        if (string.IsNullOrWhiteSpace(request.StudentId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "缺少学生ID"));

        if (request.Subject <= 0 || request.Subject > 9)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "学科值无效（有效范围：1-9）"));

        if (request.Grade <= 0 || request.Grade > 12)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "年级值无效（有效范围：1-12）"));

        if (string.IsNullOrWhiteSpace(request.SourceUploadId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "缺少上传记录ID"));

        if (request.ImagePaths.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "至少需要提供一张图片"));

        try
        {
            var imagePaths = new List<string>(request.ImagePaths);
            var items = await _mistakeService.SubmitMistakeUploadAsync(
                request.StudentId, request.Subject, request.Grade,
                imagePaths, request.RootCause, request.SourceUploadId);

            var response = new SubmitMistakeUploadResponse { Success = true };
            response.CreatedItemIds.AddRange(items.Select(i => i.Id.ToString()));

            _logger.LogDebug("SubmitMistakeUpload success: created {ItemCount} items, ids={ItemIds}",
                items.Count, string.Join(",", items.Select(i => i.Id.ToString())));

            return response;
        }
        catch (MistakeValidationException ex)
        {
            _logger.LogWarning("SubmitMistakeUpload validation failed: {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<MistakeItemDto> GetMistakeItem(IdRequest request, ServerCallContext context)
    {
        var item = await _mistakeService.GetMistakeItemAsync(Guid.Parse(request.Id));
        if (item == null)
            throw new RpcException(new Status(StatusCode.NotFound, "错题条目不存在"));

        return MapMistakeItemDto(item);
    }

    public override async Task<MistakeItemListResponse> GetMistakeItemsByUpload(GetMistakeItemsByUploadRequest request, ServerCallContext context)
    {
        var items = await _mistakeService.GetMistakeItemsByUploadAsync(request.SourceUploadId);
        var response = new MistakeItemListResponse();
        response.Items.AddRange(items.Select(MapMistakeItemDto));
        return response;
    }

    public override async Task<MistakeItemPageResult> GetMistakeItemList(GetMistakeItemListRequest request, ServerCallContext context)
    {
        var (items, totalCount) = await _mistakeService.GetMistakeItemListAsync(
            request.StudentId,
            request.Subject,
            request.Grade,
            (int)request.ReviewStatus,
            request.Page,
            request.Size);

        var result = new MistakeItemPageResult
        {
            PageMeta = new PageMeta
            {
                Page = request.Page,
                Size = request.Size,
                TotalCount = totalCount,
                TotalPages = (totalCount + request.Size - 1) / request.Size
            }
        };
        result.Items.AddRange(items.Select(MapMistakeItemDto));
        return result;
    }

    public override async Task<PendingReviewUploadListResponse> GetPendingReviewUploads(GetPendingReviewUploadsRequest request, ServerCallContext context)
    {
        List<int>? subjects = null;
        if (request.Subjects.Count > 0)
        {
            subjects = new List<int>(request.Subjects);
        }

        var (uploads, totalCount) = await _mistakeService.GetPendingReviewUploadsAsync(request.Page, request.Size, subjects);

        var result = new PendingReviewUploadListResponse
        {
            PageMeta = new PageMeta
            {
                Page = request.Page,
                Size = request.Size,
                TotalCount = totalCount,
                TotalPages = (totalCount + request.Size - 1) / request.Size
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
        return result;
    }

    public override async Task<ReviewMistakeItemResponse> ReviewMistakeItem(ReviewMistakeItemRequest request, ServerCallContext context)
    {
        if (request.ReviewStatus != ReviewStatus.Confirmed && request.ReviewStatus != ReviewStatus.Rejected)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "审核结果只能是确认或驳回"));

        try
        {
            int? grade = request.Grade > 0 ? request.Grade : null;
            int? subject = request.Subject > 0 ? request.Subject : null;
            var item = await _mistakeService.ReviewMistakeItemAsync(
                Guid.Parse(request.Id),
                (int)request.ReviewStatus,
                request.ReviewerId,
                request.RootCause,
                grade,
                subject);

            if (item == null)
                return new ReviewMistakeItemResponse { Success = false, ErrorMessage = "错题不存在" };

            if (request.ReviewStatus == ReviewStatus.Confirmed)
            {
                // Confirm flow: images are already in mistakes/ from the polling worker.
                // No migration or Student record update needed here.
                _logger.LogInformation("ReviewMistakeItem: Confirmed MistakeItem {ItemId}", item.Id);
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
                            await _ossService.CopyObjectAsync(mistakePath, uploadPath).ConfigureAwait(false);
                            await _ossService.DeleteAsync(mistakePath).ConfigureAwait(false);
                            migratedBackPaths.Add(uploadPath);
                            _logger.LogDebug("ReviewMistakeItem: Migrated rejected image back {MistakePath} -> {UploadPath}",
                                mistakePath, uploadPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ReviewMistakeItem: Failed to migrate rejected image back {Path}", mistakePath);
                    }
                }

                // Delete MistakeItem (cascades to practice and process_log)
                await _mistakeService.DeleteMistakeItemAsync(item.Id);
                _logger.LogInformation("ReviewMistakeItem: Rejected and deleted MistakeItem {ItemId}", item.Id);

                // Create a returned upload record in Student so the student can re-upload.
                // return_reason is passed through to student service and written to
                // image_upload_record.return_reason (NOT concatenated into comments, to avoid
                // confusing the mistake root_cause with the teacher's return reason).
                if (migratedBackPaths.Count > 0 && !string.IsNullOrWhiteSpace(item.StudentId))
                {
                    try
                    {
                        var createReq = new StudentProto.CreateReturnedRecordRequest
                        {
                            StudentId = item.StudentId,
                            Comments = "Rejected",
                            ReturnReason = request.ReturnReason ?? string.Empty
                        };
                        foreach (var path in migratedBackPaths)
                        {
                            createReq.ImageEntries.Add(new StudentProto.ImageEntry
                            {
                                Path = path,
                                Type = "mistake"
                            });
                        }
                        await _studentClient.CreateReturnedRecordAsync(createReq).ConfigureAwait(false);
                        _logger.LogInformation("ReviewMistakeItem: Created returned record for student {StudentId} with {Count} images",
                            item.StudentId, migratedBackPaths.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ReviewMistakeItem: Failed to create returned record for student {StudentId}",
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

            return response;
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<ReanalyzeMistakeItemResponse> ReanalyzeMistakeItem(
        ReanalyzeMistakeItemRequest request, ServerCallContext context)
    {
        _logger.LogInformation(
            "ReanalyzeMistakeItem (async job) called: id={ItemId}, reviewerId={ReviewerId}, studentId={StudentId}, hasTeacherNote={HasNote}",
            request.Id, request.ReviewerId, request.StudentId, !string.IsNullOrWhiteSpace(request.TeacherDescription));

        // 1. Validate request shape
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "错题 ID 不能为空"));
        }
        if (!Guid.TryParse(request.Id, out var itemGuid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "错题 ID 格式无效"));
        }
        if (string.IsNullOrWhiteSpace(request.ReviewerId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "未识别教师身份"));
        }
        if (string.IsNullOrWhiteSpace(request.StudentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "缺少学生ID"));
        }

        // 2. Enqueue the job. The ReanalyzeJobService performs item validation,
        //    snapshots previous root_cause, and persists a Pending job in a single
        //    transaction. It does NOT call VL — that is done by the hosted worker.
        var result = await _reanalyzeJobService.StartJobAsync(
            itemGuid,
            request.ReviewerId,
            request.StudentId,
            request.TeacherDescription,
            context.CancellationToken);

        if (!result.Success)
        {
            var code = result.ErrorCode switch
            {
                "NotFound" => StatusCode.NotFound,
                "FailedPrecondition" => StatusCode.FailedPrecondition,
                "InvalidArgument" => StatusCode.InvalidArgument,
                _ => StatusCode.Internal
            };
            // NotFound and InvalidArgument are surfaced as gRPC exceptions so the
            // BFF can map them to HTTP 404/400. FailedPrecondition is a 422
            // business validation that we return as success=false.
            if (code == StatusCode.NotFound || code == StatusCode.InvalidArgument)
            {
                throw new RpcException(new Status(code, result.ErrorMessage ?? "重新分析失败"));
            }
            return new ReanalyzeMistakeItemResponse
            {
                Success = false,
                ErrorMessage = result.ErrorMessage ?? "重新分析失败"
            };
        }

        return new ReanalyzeMistakeItemResponse
        {
            Success = true,
            JobId = result.Job.Id.ToString()
        };
    }

    public override async Task<GetReanalyzeJobsResponse> GetReanalyzeJobs(
        GetReanalyzeJobsRequest request, ServerCallContext context)
    {
        var reviewerId = request.ReviewerId;
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "未识别教师身份"
            };
        }

        if (request.JobIds == null || request.JobIds.Count == 0)
        {
            return new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "缺少 ids 参数"
            };
        }
        if (request.JobIds.Count > 100)
        {
            return new GetReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "ids 数量必须在 1-100 之间"
            };
        }

        var ids = new List<Guid>(request.JobIds.Count);
        foreach (var raw in request.JobIds)
        {
            if (Guid.TryParse(raw, out var id)) ids.Add(id);
        }

        var jobs = await _reanalyzeJobService.GetJobsAsync(reviewerId, ids, context.CancellationToken);
        return new GetReanalyzeJobsResponse
        {
            Success = true,
            Jobs = { jobs.Select(MapJobDto) }
        };
    }

    public override async Task<GetActiveReanalyzeJobsResponse> GetActiveReanalyzeJobs(
        GetActiveReanalyzeJobsRequest request, ServerCallContext context)
    {
        var reviewerId = request.ReviewerId;
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return new GetActiveReanalyzeJobsResponse
            {
                Success = false,
                ErrorMessage = "未识别教师身份"
            };
        }
        var jobs = await _reanalyzeJobService.GetActiveJobsAsync(reviewerId, context.CancellationToken);
        return new GetActiveReanalyzeJobsResponse
        {
            Success = true,
            Jobs = { jobs.Select(MapJobDto) }
        };
    }

    private static ReanalyzeJobDto MapJobDto(Domain.Models.ReanalyzeJobModel job) => new()
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

    public override async Task<MistakeItemDto> AddMistakeItem(AddMistakeItemRequest request, ServerCallContext context)
    {
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
            var created = await _mistakeService.AddMistakeItemAsync(item);
            return MapMistakeItemDto(created);
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<MistakeItemDto> UpdateMistakeItem(UpdateMistakeItemRequest request, ServerCallContext context)
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
            var updatedItem = await _mistakeService.UpdateMistakeItemPartialAsync(
                Guid.Parse(request.Id),
                studentId: string.IsNullOrWhiteSpace(request.StudentId) ? null : request.StudentId,
                subject: request.Subject > 0 ? request.Subject : null,
                grade: request.Grade > 0 ? request.Grade : null,
                sourceRegions: sourceRegions);

            if (updatedItem == null)
                throw new RpcException(new Status(StatusCode.NotFound, "错题条目不存在"));

            return MapMistakeItemDto(updatedItem);
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<BoolResponse> DeleteMistakeItem(IdRequest request, ServerCallContext context)
    {
        try
        {
            var success = await _mistakeService.DeleteMistakeItemAsync(Guid.Parse(request.Id));
            return new BoolResponse { Success = success };
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    [Obsolete("目标流程中不存在独立的 CompleteUploadReview 步骤，每条审核通过时立即迁移。仅保留供 LegacyClean 运维兜底使用。")]
    public override async Task<CompleteUploadReviewResponse> CompleteUploadReview(CompleteUploadReviewRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _mistakeService.CompleteUploadReviewAsync(
                request.SourceUploadId, request.ReviewerId);

            if (!result.Success)
            {
                return new CompleteUploadReviewResponse { Success = false, ErrorMessage = result.ErrorMessage };
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
                        await _ossService.CopyObjectAsync(oldPath, newPath).ConfigureAwait(false);
                        await _ossService.DeleteAsync(oldPath).ConfigureAwait(false);
                        pathMapping[oldPath] = newPath;
                        migratedImagePaths.Add(oldPath);
                        _logger.LogDebug("CompleteUploadReview: Migrated {OldPath} -> {NewPath}", oldPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "CompleteUploadReview: Failed to migrate {Path}", oldPath);
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
                        _logger.LogDebug(
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
                    await _mistakeService.UpdateMistakeItemAsync(item).ConfigureAwait(false);
                }
            }

            var response = new CompleteUploadReviewResponse
            {
                Success = true,
                StudentId = result.StudentId ?? ""
            };
            response.RemovedImagePaths.AddRange(imagePathsToRemoveFromRecord);

            return response;
        }
        catch (MistakeValidationException ex)
        {
            return new CompleteUploadReviewResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<MistakePracticeDto> AddMistakePractice(AddMistakePracticeRequest request, ServerCallContext context)
    {
        var practice = new MistakePracticeModel
        {
            MistakeItemId = Guid.Parse(request.MistakeItemId),
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
            var result = await _mistakeService.AddPracticeAsync(practice, imagePaths);
            return MapPracticeDto(result);
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<MistakePracticeListResponse> GetMistakePractices(IdRequest request, ServerCallContext context)
    {
        var practices = await _mistakeService.GetPracticesAsync(Guid.Parse(request.Id));
        var response = new MistakePracticeListResponse();
        response.Practices.AddRange(practices.Select(MapPracticeDto));
        return response;
    }

    public override async Task<BoolResponse> LogMistakeProcess(LogMistakeProcessRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.LogMistakeProcessAsync(
            Guid.Parse(request.MistakeItemId),
            request.StudentId,
            request.Action,
            request.Details);

        return new BoolResponse { Success = success };
    }

    public override async Task<ProcessLogListResponse> GetMistakeProcessHistory(IdRequest request, ServerCallContext context)
    {
        var logs = await _mistakeService.GetMistakeProcessHistoryAsync(Guid.Parse(request.Id));
        var response = new ProcessLogListResponse();
        response.Logs.AddRange(logs.Select(MapProcessLogDto));
        return response;
    }

    public override async Task<PresignedUrlResponse> GetPresignedUrl(GetPresignedUrlRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "object_path is required"));

        var expirySeconds = request.ExpirySeconds > 0 ? request.ExpirySeconds : 3600;
        var size = string.IsNullOrWhiteSpace(request.Size) ? "full" : request.Size.ToLowerInvariant();

        try
        {
            var exists = await _ossService.ObjectExistsAsync(request.ObjectPath).ConfigureAwait(false);
            if (!exists)
                throw new RpcException(new Status(StatusCode.NotFound, $"Object not found: {request.ObjectPath}"));

            // 原图请求：直接返回
            if (size == "full")
            {
                var url = await _ossService.GetPresignedUrlAsync(request.ObjectPath, expirySeconds).ConfigureAwait(false);
                return new PresignedUrlResponse { Url = url, ExpirySeconds = expirySeconds };
            }

            // 缩略图请求：使用 ThumbnailHelper
            var (thumbnailUrl, expiry) = await ThumbnailHelper.GetOrGenerateThumbnailAsync(
                request.ObjectPath, size, _ossService, expirySeconds, _logger).ConfigureAwait(false);
            return new PresignedUrlResponse { Url = thumbnailUrl, ExpirySeconds = expiry };
        }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPresignedUrl: failed for path {Path}", request.ObjectPath);
            throw new RpcException(new Status(StatusCode.Internal, $"Failed to generate presigned URL: {ex.Message}"));
        }
    }

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
            var regionDto = new SourceRegion
            {
                SourceImagePath = region.SourceImagePath ?? string.Empty
            };

            if (region.BoundingBox != null)
            {
                regionDto.BoundingBox = new BoundingBox
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
