using System.Security.Claims;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;
using Emenda.Domain.Services;
using Emenda.Service.Dtos;
using Emenda.Service.Intake;
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
        var authenticated = new ClaimsPrincipal(user.Identities.Where(identity => identity.IsAuthenticated));
        if (!authenticated.Identities.Any()) return IntakeError("authentication_required", 401);
        var product = authenticated.Identities.FirstOrDefault(identity => identity.AuthenticationType == "ProductCaller");
        if (product is not null)
        {
            caller = product.FindFirst("emenda:caller")?.Value ?? "";
            reviewer = requestedReviewer ?? "";
            if (string.IsNullOrEmpty(caller)) return IntakeError("authentication_required", 401);
        }
        else
        {
            var decision = authorization.DecideRoleGate("mistake analysis", authenticated, MistakeResourceAccessGate.StaffOrAdmin);
            if (!decision.Allowed) return decision.ToHttpResponse();
            reviewer = authorization.GetSubjectId(authenticated) ?? "";
            caller = "user:" + reviewer;
            if (!string.IsNullOrEmpty(requestedReviewer) && requestedReviewer != reviewer)
                return IntakeError("reviewer_mismatch", 403);
        }
        if (write && (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 36))
            return IntakeError("reviewer_required", 400);
        return null;
    }

    private static async Task<IResult?> AnalysisItemAccessAsync(MistakeItemModel item, ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService authorization, string caller, HttpContext http, CancellationToken ct)
    {
        // Ordinary items retain the trusted-BFF contract; extraction ownership belongs to the task.
        if (!item.SourceUploadId.StartsWith("extract:", StringComparison.Ordinal)) return null;
        if (!Guid.TryParseExact(item.SourceUploadId[8..], "N", out var jobId)
            || item.SourceUploadId != $"extract:{jobId:N}")
            return IntakeError("item_binding_conflict", 409);
        var authenticated = new ClaimsPrincipal(user.Identities.Where(identity => identity.IsAuthenticated));
        ExtractionAccess access;
        if (authenticated.Identities.Any(identity => identity.AuthenticationType == "ProductCaller"))
            access = new(caller, caller);
        else
        {
            var decision = authorization.DecideRoleGate("extraction mistake analysis", authenticated, MistakeResourceAccessGate.AdminOnly);
            if (!decision.Allowed) return decision.ToHttpResponse();
            var actor = authorization.GetSubjectId(authenticated);
            if (string.IsNullOrWhiteSpace(actor)) return IntakeError("authentication_required", 401);
            access = new("admin", actor, true);
        }
        try
        {
            var job = await http.RequestServices.GetRequiredService<IExtractionService>().GetAsync(access, jobId, ct);
            if (job.StudentId != item.StudentId || !job.Questions.Any(question =>
                    question.ItemId == item.Id && !question.ItemDeleted
                    && question.Question.Id.ToString("D") == item.QuestionId
                    && question.Question.Subject == item.Subject))
                return IntakeError("item_binding_conflict", 409);
        }
        catch (ExtractionException ex) { return IntakeError(ex.Code, ex.Status); }
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
        denied = await AnalysisItemAccessAsync(item, http.User, authorization, caller, http, ct);
        if (denied is not null) return denied;
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
        var denied = AnalysisAccess(http.User, authorization, null, false, out _, out var caller);
        if (denied is not null) return denied;
        var item = await mistakes.GetMistakeItemAsync(id);
        if (item is null) return IntakeError("mistake_not_found", 404);
        denied = await AnalysisItemAccessAsync(item, http.User, authorization, caller, http, ct);
        if (denied is not null) return denied;
        var result = await repository.GetAsync(id, ct) ?? new MistakeAnalysisModel { MistakeItemId = id };
        return Results.Ok(new { success = true, data = result });
    }

    private static async Task<IResult> ConfirmMistakeAnalysis(Guid id, ConfirmMistakeAnalysisRequest request,
        ClaimsPrincipal user, IMistakeApiResourceAuthorizationService authorization, IMistakeDomainService mistakes,
        [FromServices] IMistakeAnalysisRepository repository, HttpContext http, CancellationToken ct)
    {
        var denied = AnalysisAccess(http.User, authorization, request.ReviewerId, true, out var reviewer, out var caller);
        if (denied is not null) return denied;
        var item = await mistakes.GetMistakeItemAsync(id);
        if (item is null) return IntakeError("mistake_not_found", 404);
        denied = await AnalysisItemAccessAsync(item, http.User, authorization, caller, http, ct);
        if (denied is not null) return denied;
        if (http.RequestServices.GetService<IMistakeSourceGuard>() is { } guard)
            await guard.EnsureOwnedWriteAsync(id, item.StudentId, ct);
        var result = await repository.ConfirmAsync(id, request.ExpectedRevision, reviewer, ct);
        return Results.Ok(new { success = true, data = result });
    }
}
