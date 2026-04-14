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

    public override async Task<MistakeDto> UploadMistake(UploadMistakeRequest request, ServerCallContext context)
    {
        var entity = request.Adapt<MistakeModel>();

        // Convert image bytes to list
        var images = request.Images?.Count > 0
            ? request.Images.Select(b => b.ToByteArray()).ToList()
            : null;

        var result = await _mistakeService.UploadMistakeAsync(entity, images);
        return result.Adapt<MistakeDto>();
    }

    public override async Task<BoolResponse> DeleteMistake(IdRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.DeleteMistakeAsync(Guid.Parse(request.Id));
        return new BoolResponse { Success = success };
    }

    public override async Task<MistakeDto> GetMistake(GetMistakeRequest request, ServerCallContext context)
    {
        var mistake = await _mistakeService.GetMistakeAsync(Guid.Parse(request.MistakeId));
        if (mistake == null || mistake.StudentId != request.StudentId)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Mistake not found"));
        }
        return mistake.Adapt<MistakeDto>();
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
        result.Items.AddRange(items.Adapt<MistakeDto[]>());
        return result;
    }

    public override async Task<BoolResponse> UpdateMistake(UpdateMistakeRequest request, ServerCallContext context)
    {
        // Convert image bytes to list
        var images = request.Images?.Count > 0
            ? request.Images.Select(b => b.ToByteArray()).ToList()
            : null;

        var success = await _mistakeService.UpdateMistakeAsync(Guid.Parse(request.MistakeId), entity =>
        {
            entity.Content = request.Content;
            entity.CorrectAnswer = request.CorrectAnswer;
            entity.StudentAnswer = request.StudentAnswer;
            entity.Comments = request.Comments;
            entity.QuestionId = request.QuestionId;
        }, images);

        return new BoolResponse { Success = success };
    }

    public override async Task<BoolResponse> MarkMistakeProcessed(IdRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.MarkMistakeProcessedAsync(Guid.Parse(request.Id));
        return new BoolResponse { Success = success };
    }

    public override async Task<BoolResponse> SplitMistake(SplitMistakeRequest request, ServerCallContext context)
    {
        var newMistakes = new List<(MistakeModel mistake, List<byte[]>? images)>();

        foreach (var req in request.NewMistakes)
        {
            var entity = req.Adapt<MistakeModel>();
            var images = req.Images?.Count > 0
                ? req.Images.Select(b => b.ToByteArray()).ToList()
                : null;
            
            newMistakes.Add((entity, images));
        }

        var success = await _mistakeService.SplitMistakeAsync(Guid.Parse(request.OriginalMistakeId), newMistakes);
        return new BoolResponse { Success = success };
    }

    public override async Task<MistakePracticeDto> AddMistakePractice(AddMistakePracticeRequest request, ServerCallContext context)
    {
        var entity = request.Adapt<MistakePracticeModel>();

        // Convert image bytes to list
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
            Guid.Parse(request.MistakeId),
            request.Content,
            request.CorrectAnswer,
            request.StudentAnswer);

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
}

