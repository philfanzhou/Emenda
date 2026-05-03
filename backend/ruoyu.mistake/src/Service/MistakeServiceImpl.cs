using System;
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

    public override async Task<BoolResponse> UpdateMistake(UpdateMistakeRequest request, ServerCallContext context)
    {
        var images = request.Images?.Count > 0
            ? request.Images.Select(b => b.ToByteArray()).ToList()
            : null;

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
            },
            images);

        return new BoolResponse { Success = success };
    }

    public override async Task<BoolResponse> MarkMistakeProcessed(MarkMistakeProcessedRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.MarkMistakeProcessedAsync(
            Guid.Parse(request.Id),
            request.QuestionId);
        return new BoolResponse { Success = success };
    }

    public override async Task<BoolResponse> SplitMistake(SplitMistakeRequest request, ServerCallContext context)
    {
        var newMistakes = new List<(MistakeModel mistake, List<byte[]>? images, string? comments)>();

        foreach (var req in request.NewMistakes)
        {
            var entity = new MistakeModel
            {
                StudentId = req.StudentId,
                Subject = req.Subject,
                Grade = req.Grade,
                Date = DateTime.UtcNow,
                Type = req.Type
            };
            var images = req.Images?.Count > 0
                ? req.Images.Select(b => b.ToByteArray()).ToList()
                : null;
            var comments = req.Comments;

            newMistakes.Add((entity, images, comments));
        }

        var success = await _mistakeService.SplitMistakeAsync(Guid.Parse(request.OriginalMistakeId), newMistakes);
        return new BoolResponse { Success = success };
    }

    public override async Task<MistakePracticeDto> AddMistakePractice(AddMistakePracticeRequest request, ServerCallContext context)
    {
        var entity = request.Adapt<MistakePracticeModel>();

        var images = request.Images?.Count > 0
            ? request.Images.Select(b => b.ToByteArray()).ToList()
            : null;

        var result = await _mistakeService.AddPracticeAsync(entity, images);
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

    private static MistakeDto MapMistakeDto(MistakeModel mistake)
    {
        return new MistakeDto
        {
            Id = mistake.Id.ToString(),
            StudentId = mistake.StudentId ?? string.Empty,
            Subject = mistake.Subject,
            Grade = mistake.Grade,
            Date = mistake.Date == DateTime.MinValue ? string.Empty : mistake.Date.ToString("o"),
            Type = mistake.Type,
            QuestionId = mistake.QuestionId ?? string.Empty,
            Status = (MistakeStatus)mistake.Status,
            CreatedAt = mistake.CreatedAt == DateTime.MinValue ? string.Empty : mistake.CreatedAt.ToString("o"),
            UpdatedAt = mistake.UpdatedAt.HasValue ? mistake.UpdatedAt.Value.ToString("o") : string.Empty
        };
    }
}
