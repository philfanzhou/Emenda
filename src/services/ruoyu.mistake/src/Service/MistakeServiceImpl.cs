using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Service;

public class MistakeServiceImpl : MistakeGrpcService.MistakeGrpcServiceBase
{
    private readonly IMistakeDomainService _mistakeService;
    private readonly IOssService _ossService;
    private readonly StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient _studentClient;
    private readonly ILogger<MistakeServiceImpl> _logger;

    public MistakeServiceImpl(
        IMistakeDomainService mistakeService,
        IOssService ossService,
        StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient studentClient,
        ILogger<MistakeServiceImpl> logger)
    {
        _mistakeService = mistakeService;
        _ossService = ossService;
        _studentClient = studentClient;
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
                imagePaths, request.Comments, request.SourceUploadId);

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
                request.ReviewComment,
                grade,
                subject);

            if (item == null)
                return new ReviewMistakeItemResponse { Success = false, ErrorMessage = "错题不存在" };

            var removedPaths = new List<string>();

            if (request.ReviewStatus == ReviewStatus.Confirmed)
            {
                // 逐条迁移当前这道题的图片：uploads/ → mistakes/
                // 通过 Student gRPC MigrateImagesToMistake 接口迁移，避免跨服务直接操作 OSS
                var updated = false;
                var pathsToMigrate = new List<string>();

                foreach (var region in item.SourceRegions)
                {
                    if (string.IsNullOrWhiteSpace(region.SourceImagePath))
                        continue;

                    removedPaths.Add(region.SourceImagePath);

                    if (region.SourceImagePath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                    {
                        pathsToMigrate.Add(region.SourceImagePath);
                    }
                }

                if (pathsToMigrate.Count > 0)
                {
                    try
                    {
                        var migrateRequest = new StudentProto.MigrateImagesToMistakeRequest();
                        migrateRequest.SourcePaths.AddRange(pathsToMigrate);
                        var migrateResponse = await _studentClient.MigrateImagesToMistakeAsync(migrateRequest);

                        foreach (var result in migrateResponse.Results)
                        {
                            if (result.Success)
                            {
                                // 更新 MistakeItem 中的路径引用
                                foreach (var region in item.SourceRegions)
                                {
                                    if (region.SourceImagePath == result.SourcePath)
                                    {
                                        region.SourceImagePath = result.NewPath;
                                        updated = true;
                                    }
                                }

                                _logger.LogDebug("ReviewMistakeItem: Migrated image from {OldPath} to {NewPath}", result.SourcePath, result.NewPath);
                            }
                            else
                            {
                                _logger.LogError("ReviewMistakeItem: Failed to migrate image {Path}: {Error}", result.SourcePath, result.ErrorMessage);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ReviewMistakeItem: Error calling MigrateImagesToMistake for {Count} paths", pathsToMigrate.Count);
                    }
                }

                if (updated)
                {
                    await _mistakeService.UpdateMistakeItemAsync(item).ConfigureAwait(false);
                }

                // 审核通过：从上传记录中移除已迁移的图片路径
                if (removedPaths.Count > 0 && !string.IsNullOrWhiteSpace(item.SourceUploadId) && !string.IsNullOrWhiteSpace(item.StudentId))
                {
                    try
                    {
                        var removeReq = new StudentProto.RemoveImagesFromRecordRequest
                        {
                            RecordId = item.SourceUploadId,
                            StudentId = item.StudentId
                        };
                        removeReq.ImagePaths.AddRange(removedPaths);
                        await _studentClient.RemoveImagesFromRecordAsync(removeReq);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ReviewMistakeItem: Failed to remove images from upload record, SourceUploadId: {Id}", item.SourceUploadId);
                    }
                }
            }
            else if (request.ReviewStatus == ReviewStatus.Rejected)
            {
                // 驳回后删除 MistakeItem 记录（级联删除关联的 practice 和 process_log）
                await _mistakeService.DeleteMistakeItemAsync(item.Id);
                _logger.LogInformation("ReviewMistakeItem: Rejected and deleted MistakeItem {ItemId}", item.Id);
            }

            var response = new ReviewMistakeItemResponse
            {
                Success = true,
                SourceUploadId = item.SourceUploadId ?? string.Empty,
                StudentId = item.StudentId ?? string.Empty
            };
            response.RemovedImagePaths.AddRange(removedPaths);

            // 审核后编排：检查该 UploadRecord 下是否还有待审核的 MistakeItem
            if (!string.IsNullOrWhiteSpace(item.SourceUploadId))
            {
                try
                {
                    var remainingItems = await _mistakeService.GetMistakeItemsByUploadAsync(item.SourceUploadId);
                    var hasPending = remainingItems.Any(i => i.ReviewStatus == (int)ReviewStatus.PendingReview);

                    if (!hasPending)
                    {
                        // 无待审核 item
                        if (request.ReviewStatus == ReviewStatus.Confirmed)
                        {
                            // 通过时：RemoveImagesFromRecord 已在上方调用
                            // 这里检查记录是否仍存在，若存在则标记退回
                            try
                            {
                                await _studentClient.MarkUploadRecordAsReturnedAsync(
                                    new StudentProto.MarkUploadRecordAsReturnedRequest
                                    {
                                        RecordId = item.SourceUploadId,
                                        StudentId = item.StudentId ?? string.Empty,
                                        Comments = "所有错题已审核处理完毕"
                                    });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "ReviewMistakeItem: Failed to mark upload record as returned after all items reviewed, SourceUploadId: {Id}", item.SourceUploadId);
                            }
                        }
                        else if (request.ReviewStatus == ReviewStatus.Rejected)
                        {
                            // 驳回时：图片仍在 uploads/ 下，不需要移除
                            // 标记上传记录为已退回
                            try
                            {
                                await _studentClient.MarkUploadRecordAsReturnedAsync(
                                    new StudentProto.MarkUploadRecordAsReturnedRequest
                                    {
                                        RecordId = item.SourceUploadId,
                                        StudentId = item.StudentId ?? string.Empty,
                                        Comments = "所有错题已驳回"
                                    });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "ReviewMistakeItem: Failed to mark upload record as returned after rejection, SourceUploadId: {Id}", item.SourceUploadId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ReviewMistakeItem: Failed to check remaining items for SourceUploadId: {Id}", item.SourceUploadId);
                }
            }

            return response;
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

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

            // 通过 Student gRPC MigrateImagesToMistake 接口批量迁移
            if (pathsToMigrate.Count > 0)
            {
                try
                {
                    var migrateRequest = new StudentProto.MigrateImagesToMistakeRequest();
                    migrateRequest.SourcePaths.AddRange(pathsToMigrate);
                    var migrateResponse = await _studentClient.MigrateImagesToMistakeAsync(migrateRequest);

                    foreach (var migrateResult in migrateResponse.Results)
                    {
                        if (migrateResult.Success)
                        {
                            pathMapping[migrateResult.SourcePath] = migrateResult.NewPath;
                            migratedImagePaths.Add(migrateResult.SourcePath);
                            _logger.LogDebug("CompleteUploadReview: Migrated {OldPath} -> {NewPath}", migrateResult.SourcePath, migrateResult.NewPath);
                        }
                        else
                        {
                            _logger.LogError("CompleteUploadReview: Failed to migrate {Path}: {Error}", migrateResult.SourcePath, migrateResult.ErrorMessage);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CompleteUploadReview: Error calling MigrateImagesToMistake");
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

    public override async Task<GetAllReferencedImagePathsResponse> GetAllReferencedImagePaths(Empty request, ServerCallContext context)
    {
        var paths = await _mistakeService.GetAllReferencedImagePathsAsync();
        var response = new GetAllReferencedImagePathsResponse();
        response.ImagePaths.AddRange(paths);
        return response;
    }

    public override async Task<PresignedUrlResponse> GetPresignedUrl(GetPresignedUrlRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "object_path is required"));

        var expirySeconds = request.ExpirySeconds > 0 ? request.ExpirySeconds : 3600;

        try
        {
            var exists = await _ossService.ObjectExistsAsync(request.ObjectPath).ConfigureAwait(false);
            if (!exists)
                throw new RpcException(new Status(StatusCode.NotFound, $"Object not found: {request.ObjectPath}"));

            var url = await _ossService.GetPresignedUrlAsync(request.ObjectPath, expirySeconds).ConfigureAwait(false);

            return new PresignedUrlResponse
            {
                Url = url,
                ExpirySeconds = expirySeconds
            };
        }
        catch (RpcException)
        {
            throw;
        }
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
            ReviewComment = item.ReviewComment ?? string.Empty,
            Type = item.Type,
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
