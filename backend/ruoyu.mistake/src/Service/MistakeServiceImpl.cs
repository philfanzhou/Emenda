using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Mapster;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;

namespace Ruoyu.Study.Mistake.Service;

public class MistakeServiceImpl : MistakeGrpcService.MistakeGrpcServiceBase
{
    private readonly MistakeDomainService _mistakeService;

    public MistakeServiceImpl(MistakeDomainService mistakeService)
    {
        _mistakeService = mistakeService;
    }

    public override async Task<BoolResponse> DeleteMistake(IdRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.DeleteMistakeAsync(Guid.Parse(request.Id));
        return new BoolResponse { Success = success };
    }

    public override async Task<BoolResponse> DeleteMistakeImage(DeleteMistakeImageRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.DeleteMistakeImageAsync(Guid.Parse(request.MistakeId), request.ImagePath);
        return new BoolResponse { Success = success };
    }

    public override async Task<GetMistakeResponse> GetMistake(GetMistakeRequest request, ServerCallContext context)
    {
        var (mistake, detail) = await _mistakeService.GetMistakeAsync(Guid.Parse(request.MistakeId));
        if (mistake == null || mistake.StudentId != request.StudentId)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Mistake not found"));
        }

        var response = new GetMistakeResponse
        {
            Mistake = MapMistakeDto(mistake),
            Detail = detail != null ? new MistakeDetailDto
            {
                MistakeId = detail.MistakeId.ToString(),
                OriginalImages = { detail.OriginalImages },
                Comments = detail.Comments ?? string.Empty
            } : null
        };
        return response;
    }

    public override async Task<MistakePageResult> GetMistakeList(GetMistakeListRequest request, ServerCallContext context)
    {
        var (items, totalCount) = await _mistakeService.GetMistakeListAsync(
            request.StudentId,
            request.Subject,
            request.Grade,
            (int)request.Status,
            request.Page,
            request.Size
        );

        var result = new MistakePageResult
        {
            PageMeta = new PageMeta
            {
                Page = request.Page,
                Size = request.Size,
                TotalCount = totalCount,
                TotalPages = (totalCount + request.Size - 1) / request.Size
            }
        };
        result.Items.AddRange(items.Select(MapMistakeDto));
        return result;
    }

    public override async Task<CreateMistakeResponse> CreateMistake(CreateMistakeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.StudentId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "缺少学生ID"));

        if (request.Subject < 0 || request.Subject > 9)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "学科值无效（有效范围：0-9）"));

        if (request.Grade < 0 || request.Grade > 12)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "年级值无效（有效范围：0-12）"));

        var hasImages = request.ImagePaths?.Count > 0;
        var hasComments = !string.IsNullOrWhiteSpace(request.Comments);

        if (!hasImages && !hasComments)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "错题图片和正文不能同时为空，至少需要提供一项"));

        var mistake = new MistakeModel
        {
            StudentId = request.StudentId,
            Subject = request.Subject,
            Grade = request.Grade,
            Date = DateTimeOffset.UtcNow,
            Type = request.Type
        };

        var imagePaths = hasImages ? new List<string>(request.ImagePaths!) : (List<string>?)null;

        try
        {
            var (createdMistake, _) = await _mistakeService.CreateMistakeAsync(mistake, imagePaths, request.Comments);

            return new CreateMistakeResponse
            {
                Success = true,
                MistakeId = createdMistake.Id.ToString()
            };
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<BoolResponse> UpdateMistake(UpdateMistakeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.MistakeId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "缺少错题ID"));

        try
        {
            var success = await _mistakeService.UpdateMistakeAsync(
                Guid.Parse(request.MistakeId),
                entity =>
                {
                    entity.Subject = request.Subject > 0 ? request.Subject : entity.Subject;
                    entity.Grade = request.Grade > 0 ? request.Grade : entity.Grade;
                    entity.Type = request.Type > 0 ? request.Type : entity.Type;
                    if (!string.IsNullOrEmpty(request.QuestionId))
                    {
                        entity.QuestionId = request.QuestionId;
                    }
                },
                detail =>
                {
                    if (!string.IsNullOrEmpty(request.Comments))
                    {
                        detail.Comments = request.Comments;
                    }
                });

            return new BoolResponse { Success = success };
        }
        catch (MistakeValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<BoolResponse> MarkMistakeProcessed(MarkMistakeProcessedRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.MarkMistakeProcessedAsync(
            Guid.Parse(request.Id),
            request.QuestionId);
        return new BoolResponse { Success = success };
    }

    public override async Task<MistakePracticeDto> AddMistakePractice(AddMistakePracticeRequest request, ServerCallContext context)
    {
        var entity = request.Adapt<MistakePracticeModel>();

        var imagePaths = request.ImagePaths?.Count > 0
            ? new List<string>(request.ImagePaths)
            : null;

        var result = await _mistakeService.AddPracticeAsync(entity, imagePaths);
        return result.Adapt<MistakePracticeDto>();
    }

    public override async Task<MistakePracticeListResponse> GetMistakePractices(IdRequest request, ServerCallContext context)
    {
        var practices = await _mistakeService.GetPracticesAsync(Guid.Parse(request.Id));
        var response = new MistakePracticeListResponse();
        response.Practices.AddRange(practices.Adapt<MistakePracticeDto[]>());
        return response;
    }

    public override async Task<MistakeTypeResponse> AnalyzeMistakeType(AnalyzeMistakeRequest request, ServerCallContext context)
    {
        var result = await _mistakeService.AnalyzeMistakeTypeAsync(
            Guid.Parse(request.MistakeId));

        return result;
    }

    public override async Task<BoolResponse> LogMistakeProcess(LogMistakeProcessRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.LogMistakeProcessAsync(
            Guid.Parse(request.MistakeId),
            request.StudentId,
            request.Action,
            request.Details);

        return new BoolResponse { Success = success };
    }

    public override async Task<ProcessLogListResponse> GetMistakeProcessHistory(IdRequest request, ServerCallContext context)
    {
        var logs = await _mistakeService.GetMistakeProcessHistoryAsync(Guid.Parse(request.Id));
        var response = new ProcessLogListResponse();
        response.Logs.AddRange(logs.Adapt<ProcessLogDto[]>());
        return response;
    }

    public override async Task<BoolResponse> ReturnMistake(ReturnMistakeRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.DeleteMistakeWithoutDeletingImagesAsync(
            Guid.Parse(request.MistakeId));
        return new BoolResponse { Success = success };
    }

    private static MistakeDto MapMistakeDto(MistakeModel mistake)
    {
        return new MistakeDto
        {
            Id = mistake.Id.ToString(),
            StudentId = mistake.StudentId ?? string.Empty,
            Subject = mistake.Subject,
            Grade = mistake.Grade,
            Date = mistake.Date == DateTimeOffset.MinValue ? string.Empty : mistake.Date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            Type = mistake.Type,
            QuestionId = mistake.QuestionId ?? string.Empty,
            Status = (MistakeStatus)mistake.Status,
            CreatedAt = mistake.CreatedAt == DateTimeOffset.MinValue ? string.Empty : mistake.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            UpdatedAt = mistake.UpdatedAt.HasValue ? mistake.UpdatedAt.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") : string.Empty
        };
    }
}
