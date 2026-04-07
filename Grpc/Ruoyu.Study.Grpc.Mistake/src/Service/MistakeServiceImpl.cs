using System.Threading.Tasks;
using Grpc.Core;
using Mapster;
using Ruoyu.Study.Mistake.Contract.Protos;
using Ruoyu.Study.Mistake.Database.Entity;
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
        var entity = request.Adapt<MistakeEntity>();
        var result = await _mistakeService.UploadMistakeAsync(entity);
        return result.Adapt<MistakeDto>();
    }

    public override async Task<BoolResponse> DeleteMistake(IdRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.DeleteMistakeAsync(request.Id);
        return new BoolResponse { Success = success };
    }

    public override async Task<MistakeDto> GetMistake(GetMistakeRequest request, ServerCallContext context)
    {
        var mistake = await _mistakeService.GetMistakeAsync(request.MistakeId);
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
        var success = await _mistakeService.UpdateMistakeAsync(request.MistakeId, entity =>
        {
            entity.Content = request.Content;
            entity.CorrectAnswer = request.CorrectAnswer;
            entity.StudentAnswer = request.StudentAnswer;
            entity.Comments = request.Comments;
            entity.QuestionId = request.QuestionId;
        });

        return new BoolResponse { Success = success };
    }

    public override async Task<BoolResponse> MarkMistakeProcessed(IdRequest request, ServerCallContext context)
    {
        var success = await _mistakeService.MarkMistakeProcessedAsync(request.Id);
        return new BoolResponse { Success = success };
    }

    public override async Task<MistakePracticeDto> AddMistakePractice(AddMistakePracticeRequest request, ServerCallContext context)
    {
        var practice = request.Adapt<MistakePracticeEntity>();
        var result = await _mistakeService.AddPracticeAsync(practice);
        return result.Adapt<MistakePracticeDto>();
    }

    public override async Task<MistakePracticeListResponse> GetMistakePractices(IdRequest request, ServerCallContext context)
    {
        var practices = await _mistakeService.GetPracticesAsync(request.Id);
        var response = new MistakePracticeListResponse();
        response.Practices.AddRange(practices.Adapt<MistakePracticeDto[]>());
        return response;
    }
}
