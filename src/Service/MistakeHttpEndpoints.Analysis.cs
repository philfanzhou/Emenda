using System.Security.Claims;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;
using Emenda.Domain.Services;
using Emenda.Service.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Service;

public sealed class ConfirmMistakeAnalysisRequest
{
    public long ExpectedRevision { get; set; }
    public string? ReviewerId { get; set; }
}

public static partial class MistakeHttpEndpoints
{
    private static IResult? AnalysisAccess(ClaimsPrincipal user, IMistakeApiResourceAuthorizationService authorization,
        string? requestedReviewer, bool write, out string reviewer, out string caller)
    {
        reviewer = "";
        caller = "";
        if (user.Identity?.IsAuthenticated != true) return IntakeError("authentication_required", 401);
        if (user.Identity.AuthenticationType == "ProductCaller")
        {
            caller = user.FindFirstValue("emenda:caller") ?? "";
            reviewer = requestedReviewer ?? "";
            if (string.IsNullOrEmpty(caller)) return IntakeError("authentication_required", 401);
        }
        else
        {
            var decision = authorization.DecideRoleGate("mistake analysis", user, MistakeResourceAccessGate.StaffOrAdmin);
            if (!decision.Allowed) return decision.ToHttpResponse();
            reviewer = authorization.GetSubjectId(user) ?? "";
            caller = "user:" + reviewer;
            if (!string.IsNullOrEmpty(requestedReviewer) && requestedReviewer != reviewer)
                return IntakeError("reviewer_mismatch", 403);
        }
        if (write && (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 36))
            return IntakeError("reviewer_required", 400);
        return null;
    }

    private static async Task<IResult> StartStructuredAnalysis(Guid id, ReanalyzeMistakeItemRequest request,
        ClaimsPrincipal user, IMistakeApiResourceAuthorizationService authorization,
        IMistakeDomainService mistakes, HttpContext http, CancellationToken ct)
    {
        var denied = AnalysisAccess(http.User, authorization, request.ReviewerId, true, out var reviewer, out var caller);
        if (denied is not null) return denied;
        var item = await mistakes.GetMistakeItemAsync(id);
        if (item is null) return IntakeError("mistake_not_found", 404);
        if (!string.IsNullOrEmpty(request.StudentId) && request.StudentId != item.StudentId)
            return IntakeError("student_mismatch", 400);
        if (request.RequestId is null || request.ExpectedRevision is null)
            return IntakeError("request_id_and_revision_required", 400);
        if (http.RequestServices.GetService<IMistakeSourceGuard>() is { } guard)
            await guard.EnsureOwnedWriteAsync(id, item.StudentId, ct);
        try
        {
            var repository = http.RequestServices.GetRequiredService<IMistakeAnalysisRepository>();
            var job = await repository.StartAsync(id, reviewer, caller, request.RequestId.Value,
                request.ExpectedRevision.Value, request.TeacherDescription, ct);
            return Results.Ok(new { success = true, data = new ReanalyzeMistakeItemResponse { Success = true, JobId = job.ToString() } });
        }
        catch (ArgumentException ex) { return IntakeError(ex.Message, 400); }
    }

    private static async Task<IResult> GetMistakeAnalysis(Guid id, ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService authorization, IMistakeDomainService mistakes,
        [FromServices] IMistakeAnalysisRepository repository, HttpContext http, CancellationToken ct)
    {
        var denied = AnalysisAccess(http.User, authorization, null, false, out _, out _);
        if (denied is not null) return denied;
        if (await mistakes.GetMistakeItemAsync(id) is null) return IntakeError("mistake_not_found", 404);
        var result = await repository.GetAsync(id, ct) ?? new MistakeAnalysisModel { MistakeItemId = id };
        return Results.Ok(new { success = true, data = result });
    }

    private static async Task<IResult> ConfirmMistakeAnalysis(Guid id, ConfirmMistakeAnalysisRequest request,
        ClaimsPrincipal user, IMistakeApiResourceAuthorizationService authorization, IMistakeDomainService mistakes,
        [FromServices] IMistakeAnalysisRepository repository, HttpContext http, CancellationToken ct)
    {
        var denied = AnalysisAccess(http.User, authorization, request.ReviewerId, true, out var reviewer, out _);
        if (denied is not null) return denied;
        var item = await mistakes.GetMistakeItemAsync(id);
        if (item is null) return IntakeError("mistake_not_found", 404);
        if (http.RequestServices.GetService<IMistakeSourceGuard>() is { } guard)
            await guard.EnsureOwnedWriteAsync(id, item.StudentId, ct);
        var result = await repository.ConfirmAsync(id, request.ExpectedRevision, reviewer, ct);
        return Results.Ok(new { success = true, data = result });
    }
}
