using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
using Ruoyu.Study.Mistake.Service.Dtos;
namespace Ruoyu.Study.Mistake.Service;

public static partial class MistakeHttpEndpoints
{
    private static IResult IntakeError(string kind,int status) => Results.Json(new {success=false,errorKind=kind},statusCode:status);
    private static IResult? IntakeRole(ClaimsPrincipal user,IMistakeApiResourceAuthorizationService authorization,MistakeResourceAccessGate gate)
    {
        if (user.Identity?.IsAuthenticated!=true || string.IsNullOrWhiteSpace(authorization.GetSubjectId(user)))
            return IntakeError("authentication_required",401);
        var decision=authorization.DecideRoleGate("source intake",user,gate);
        return decision.Allowed ? null : decision.ToHttpResponse();
    }
    private static async Task<IResult> GetSourceIntakeAsync(Guid sourceId,Guid revision,ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService authorization,ISourceIntakeService intake,CancellationToken ct)
    {
        var error=IntakeRole(user,authorization,MistakeResourceAccessGate.AdminOnly);
        if (error!=null) return error;
        if (sourceId==Guid.Empty || revision==Guid.Empty) return IntakeError("invalid_condition",400);
        var result=await intake.GetAsync(sourceId,revision,ct);
        return result==null ? IntakeError("intake_not_found",404) : Results.Ok(result);
    }
    private static async ValueTask<object?> IntakeEndpointFilterAsync(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {
        try
        {
            var intake=context.HttpContext.RequestServices.GetService<ISourceIntakeService>();
            var authorization=context.HttpContext.RequestServices.GetRequiredService<IMistakeApiResourceAuthorizationService>();
            if (intake!=null && !context.HttpContext.Request.Path.StartsWithSegments("/api/mistakes/intakes"))
            {
                var id=context.Arguments.OfType<Guid>().FirstOrDefault();
                var student=id==Guid.Empty ? null : await intake.GetManagedItemStudentAsync(id,context.HttpContext.RequestAborted);
                if (student!=null)
                {
                    var user=context.HttpContext.User;
                    if(user.Identity?.IsAuthenticated!=true || string.IsNullOrWhiteSpace(authorization.GetSubjectId(user)))
                        return IntakeError("authentication_required",401);
                    var path=context.HttpContext.Request.Path.Value ?? "";
                    var gate=context.Arguments.Any(a=>a is ReviewMistakeItemRequest or ReanalyzeMistakeItemRequest)
                        ? MistakeResourceAccessGate.StaffOrAdmin :
                        context.Arguments.Any(a=>a is UpdateMistakeItemRequest) ? MistakeResourceAccessGate.AdminOnly :
                        context.Arguments.Any(a=>a is AddMistakePracticeRequest or LogMistakeProcessRequest) ? MistakeResourceAccessGate.LearnerOnly : (MistakeResourceAccessGate?)null;
                    if(gate.HasValue)
                    {
                        var role=authorization.DecideRoleGate(path,user,gate.Value);
                        if(!role.Allowed) return role.ToHttpResponse();
                    }
                    var access=await authorization.DecideItemOwnershipAsync(path,user,id.ToString(),student,context.HttpContext.RequestAborted);
                    if(!access.Allowed) return access.ToHttpResponse();
                    foreach(var argument in context.Arguments)
                    {
                        var reviewer=argument switch {ReviewMistakeItemRequest request=>request.ReviewerId,ReanalyzeMistakeItemRequest request=>request.ReviewerId,_=>null};
                        if(argument is ReviewMistakeItemRequest or ReanalyzeMistakeItemRequest)
                        { var binding=authorization.DecideReviewerBinding(path,user,reviewer); if(!binding.Allowed) return binding.ToHttpResponse(); }
                        var owner=argument switch {AddMistakePracticeRequest request=>request.StudentId,LogMistakeProcessRequest request=>request.StudentId,_=>null};
                        if(argument is AddMistakePracticeRequest or LogMistakeProcessRequest)
                        { var binding=authorization.DecideOwnedWriteBinding(path,user,id.ToString(),owner,student); if(!binding.Allowed) return binding.ToHttpResponse(); }
                    }
                }
            }
            return await next(context);
        }
        catch (SourceIntakeException ex) { return Results.Json(new {success=false,errorKind=ex.ErrorKind,conflictingItemIds=ex.ConflictingItemIds},statusCode:ex.StatusCode); }
        catch (Exception ex) when (FindDatabaseError(ex)!=null)
        {
            var database=FindDatabaseError(ex)!;
            var kind=new[] {"source_managed","handoff_pending","legacy_source_conflict","lease_lost","grouping_conflict"}
                .FirstOrDefault(k=>database.Message.Contains(k,StringComparison.Ordinal));
            return IntakeError(kind ?? "database_unavailable",kind==null ? 503 : 409);
        }
    }
    private static DbException? FindDatabaseError(Exception? ex)
    {
        while (ex!=null) { if (ex is DbException db) return db; ex=ex.InnerException; }
        return null;
    }
}
