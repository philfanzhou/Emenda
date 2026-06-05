using System;
using System.Collections.Generic;
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
    private readonly MistakeDomainService _mistakeService;
    private readonly IOssService _ossService;
    private readonly StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient _studentClient;
    private readonly ILogger<MistakeServiceImpl> _logger;

    public MistakeServiceImpl(
        MistakeDomainService mistakeService,
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
            return response;
        }
        catch (MistakeValidationException ex)
        {
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

    public override async Task<BoolResponse> ReviewMistakeItem(ReviewMistakeItemRequest request, ServerCallContext context)
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

            return new BoolResponse { Success = item != null };
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
        var item = await _mistakeService.GetMistakeItemAsync(Guid.Parse(request.Id));
        if (item == null)
            throw new RpcException(new Status(StatusCode.NotFound, "错题条目不存在"));

        if (!string.IsNullOrWhiteSpace(request.StudentId))
            item.StudentId = request.StudentId;
        if (request.Subject > 0)
            item.Subject = request.Subject;
        if (request.Grade > 0)
            item.Grade = request.Grade;
        if (request.SourceRegions.Count > 0)
        {
            item.SourceRegions = request.SourceRegions.Select(r => new SourceRegionModel
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
            }).ToList();
        }

        try
        {
            var success = await _mistakeService.UpdateMistakeItemAsync(item);
            if (!success)
                throw new RpcException(new Status(StatusCode.Internal, "更新失败"));

            var updatedItem = await _mistakeService.GetMistakeItemAsync(Guid.Parse(request.Id));
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

            foreach (var item in result.Items)
            {
                bool updated = false;
                foreach (var region in item.SourceRegions)
                {
                    if (string.IsNullOrWhiteSpace(region.SourceImagePath))
                        continue;

                    if (!imagePathsToRemoveFromRecord.Contains(region.SourceImagePath, StringComparer.OrdinalIgnoreCase))
                    {
                        imagePathsToRemoveFromRecord.Add(region.SourceImagePath);
                    }

                    if (!region.SourceImagePath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (item.ReviewStatus != (int)ReviewStatus.Confirmed)
                    {
                        _logger.LogInformation(
                            "Item {ItemId} is not Confirmed (status={Status}), will only remove from upload record, not migrate",
                            item.Id, item.ReviewStatus);
                        continue;
                    }

                    if (pathMapping.TryGetValue(region.SourceImagePath, out var cachedNewPath))
                    {
                        region.SourceImagePath = cachedNewPath;
                        updated = true;
                        continue;
                    }

                    var oldPath = region.SourceImagePath;
                    try
                    {
                        var newPath = "mistakes" + oldPath.Substring("uploads".Length);

                        await _ossService.CopyObjectAsync(oldPath, newPath);

                        await _ossService.DeleteAsync(oldPath);

                        pathMapping[oldPath] = newPath;
                        region.SourceImagePath = newPath;
                        updated = true;
                        migratedImagePaths.Add(oldPath);
                        _logger.LogInformation(
                            "Migrated image from {OldPath} to {NewPath}",
                            oldPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error migrating image {Path}",
                            oldPath);
                    }
                }

                if (updated)
                {
                    await _mistakeService.UpdateMistakeItemAsync(item).ConfigureAwait(false);
                }
            }

            // Remove processed images from upload record (both Confirmed and Rejected)
            if (imagePathsToRemoveFromRecord.Count > 0 && !string.IsNullOrEmpty(result.StudentId))
            {
                try
                {
                    var removeImagesRequest = new StudentProto.RemoveImagesFromRecordRequest
                    {
                        RecordId = request.SourceUploadId,
                        StudentId = result.StudentId
                    };
                    removeImagesRequest.ImagePaths.AddRange(imagePathsToRemoveFromRecord);

                    await _studentClient.RemoveImagesFromRecordAsync(removeImagesRequest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing images from upload record {RecordId}", request.SourceUploadId);
                }
            }

            return new CompleteUploadReviewResponse { Success = true, StudentId = result.StudentId ?? "" };
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

    public override async Task<MistakeTypeResponse> AnalyzeMistakeType(AnalyzeMistakeRequest request, ServerCallContext context)
    {
        return await _mistakeService.AnalyzeMistakeTypeAsync(Guid.Parse(request.MistakeItemId));
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
                : log.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        };
    }
}
