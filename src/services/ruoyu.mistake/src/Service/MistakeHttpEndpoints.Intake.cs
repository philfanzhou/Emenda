using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Ruoyu.Study.Common.Authentication;
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
    private static async Task<IResult> ResumeSourceTargetsAsync(Guid sourceId,Guid revision,ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService authorization,ISourceTargetService targets,CancellationToken ct)
    {
        var error=IntakeRole(user,authorization,MistakeResourceAccessGate.AdminOnly);
        if(error!=null)return error;
        if(sourceId==Guid.Empty || revision==Guid.Empty)return IntakeError("invalid_condition",400);
        return Results.Ok(await targets.ResumeTargetsAsync(sourceId,revision,ct));
    }
    private static async Task<IResult> HandoffSourceAsync(Guid sourceId,Guid revision,ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService authorization,ISourceHandoffService handoffs,CancellationToken ct)
    {
        var error=IntakeRole(user,authorization,MistakeResourceAccessGate.AdminOnly);if(error!=null)return error;
        var result=await handoffs.HandoffAsync(sourceId,revision,authorization.GetSubjectId(user)!,ct:ct);
        var success=result.State=="Completed";
        return Results.Json(new {success=true,data=new {success,errorKind=success ? null : "handoff_pending",
            errorMessage=success ? null : "来源交接处理中，请稍后重试",handoff=result}},statusCode:success ? 200 : 202);
    }
    private static async ValueTask<object?> IntakeEndpointFilterAsync(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {
        try
        {
            // Authenticate the same configured schemes as the strict API policy.
            // Only managed/new-intake operations enrich the principal. Unmanaged
            // routes retain their original authentication and compatibility behavior.
            var intake=context.HttpContext.RequestServices.GetService<ISourceIntakeService>();
            var schemes=context.HttpContext.RequestServices.GetService<IAuthenticationSchemeProvider>();
            if (schemes!=null && intake!=null && await NeedsManagedAuthenticationAsync(context,intake))
            {
                var identities=new List<ClaimsIdentity>();
                foreach(var name in new[] {JwtBearerDefaults.AuthenticationScheme,RuoyuAdminApiAuthorization.SchemeName})
                {
                    if(await schemes.GetSchemeAsync(name)==null) continue;
                    var result=await context.HttpContext.AuthenticateAsync(name);
                    if(result.Succeeded && result.Principal!=null) identities.AddRange(result.Principal.Identities);
                }
                if(identities.Count==0 && context.Arguments.OfType<GetPresignedUrlRequest>().Any(r=>Ruoyu.Study.Common.Oss.ReservedSourceTargetPath.IsReserved(r.ObjectPath)))
                    return IntakeError("authentication_required",401);
                if(identities.Count>0)
                {
                    context.HttpContext.User=new ClaimsPrincipal(identities);
                    for(var i=0;i<context.Arguments.Count;i++)
                        if(context.Arguments[i] is ClaimsPrincipal) context.Arguments[i]=context.HttpContext.User;
                }
            }
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
                        var owner=argument switch {AddMistakePracticeRequest request=>request.StudentId,LogMistakeProcessRequest request=>request.StudentId,ReanalyzeMistakeItemRequest request=>request.StudentId,_=>null};
                        if(argument is AddMistakePracticeRequest or LogMistakeProcessRequest or ReanalyzeMistakeItemRequest)
                        { var binding=authorization.DecideOwnedWriteBinding(path,user,id.ToString(),owner,student); if(!binding.Allowed) return binding.ToHttpResponse();
                          if(argument is ReanalyzeMistakeItemRequest reanalyze) reanalyze.StudentId=student; }
                    }
                }
            }
            return await next(context);
        }
        catch (SourceIntakeException ex) { return Results.Json(new {success=false,errorKind=ex.ErrorKind,conflictingItemIds=ex.ConflictingItemIds},statusCode:ex.StatusCode); }
        catch (Exception ex) when (FindDatabaseError(ex)!=null)
        {
            var database=FindDatabaseError(ex)!;
            var kind=new[] {"source_sealed","handoff_binding","handoff_receipt_binding","handoff_not_ready","handoff_targets_pending","source_managed","handoff_pending","legacy_source_conflict","lease_lost","grouping_conflict","target_managed","target_unverified","target_binding","reserved_target","return_managed","return_binding","return_unverified","return_payload_binding","return_receipt_binding","return_in_progress","return_final_binding","reanalysis_active"}
                .FirstOrDefault(k=>database.Message.Contains(k,StringComparison.Ordinal));
            return IntakeError(kind ?? "database_unavailable",kind==null ? 503 : 409);
        }
    }
    private static async Task<bool> NeedsManagedAuthenticationAsync(EndpointFilterInvocationContext context,ISourceIntakeService intake)
    {
        var http=context.HttpContext;var ct=http.RequestAborted;var path=http.Request.Path.Value ?? "";
        if(http.Request.Path.StartsWithSegments("/api/mistakes/intakes")) return true;
        var id=context.Arguments.OfType<Guid>().FirstOrDefault();
        if(id!=Guid.Empty && await intake.GetManagedItemStudentAsync(id,ct)!=null) return true;
        foreach(var argument in context.Arguments)
        {
            if(argument is SubmitMistakeUploadRequest upload)
                return intake.Enabled || upload.ExpectedContentRevision.HasValue || upload.RequestKey.HasValue
                    || await intake.HasManagedDataAsync(sourceId:upload.SourceUploadId,ct:ct);
            if(argument is AddMistakeItemRequest add)
                return intake.Enabled || add.ExpectedContentRevision.HasValue || add.RequestKey.HasValue
                    || await intake.HasManagedDataAsync(sourceId:add.SourceUploadId,ct:ct);
            if(argument is GetPresignedUrlRequest image && !string.IsNullOrWhiteSpace(image.ObjectPath))
                return Ruoyu.Study.Common.Oss.ReservedSourceTargetPath.IsReserved(image.ObjectPath) || await intake.GetManagedObjectStudentAsync(image.ObjectPath,ct)!=null
                    || await intake.IsManagedPracticeObjectAsync(image.ObjectPath,ct);
            if(argument is GetReanalyzeJobsRequest jobs && jobs.JobIds is {Count: >0 and <=100})
            {
                var ids=jobs.JobIds.Select(raw=>Guid.TryParse(raw,out var key) ? key : Guid.Empty).Where(key=>key!=Guid.Empty).ToList();
                return (await intake.GetManagedJobReviewersAsync(ids,ct)).Count>0;
            }
        }
        if(path=="/api/mistakes/reanalyze/active")
        {
            var reviewer=http.Request.Query["reviewerId"].ToString();
            if(string.IsNullOrWhiteSpace(reviewer)) return false;
            var jobs=await http.RequestServices.GetRequiredService<IReanalyzeJobService>().GetActiveJobsAsync(reviewer,ct);
            foreach(var job in jobs) if(await intake.GetManagedItemStudentAsync(job.ItemId,ct)!=null) return true;
            return false;
        }
        if(path=="/api/mistakes" && HttpMethods.IsGet(http.Request.Method))
            return await intake.HasManagedDataAsync(studentId:http.Request.Query["studentId"].ToString(),ct:ct);
        if(path=="/api/mistakes/pending-reviews") return await intake.HasManagedDataAsync(ct:ct);
        if(http.Request.RouteValues.TryGetValue("sourceUploadId",out var source))
            return await intake.HasManagedDataAsync(sourceId:source?.ToString(),ct:ct);
        return false;
    }
    private static DbException? FindDatabaseError(Exception? ex)
    {
        while (ex!=null) { if (ex is DbException db) return db; ex=ex.InnerException; }
        return null;
    }
}
