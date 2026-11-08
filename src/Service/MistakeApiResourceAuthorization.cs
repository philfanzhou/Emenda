using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Emenda.Common.Authentication;
using Emenda.Common.Constants;
using Emenda.Domain.Services;
using Emenda.Service.StudentIntegration;

namespace Emenda.Service;

/// <summary>
/// Fixed-at-startup snapshot of the per-resource authorization switch
/// (<c>IdentityService:MistakeApiResourceAuth</c>, IKJEPZ). The Host resolves
/// and validates the switch before any endpoint can serve; the value never
/// changes for the process lifetime and disabling requires a restart.
/// </summary>
public sealed class MistakeApiResourceAuthOptions
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Principal classification of an authenticated /api caller per the IKJEPZ
/// matrix. Both accepted schemes (shared audience and Admin AppId audience)
/// classify with the same rules; roles are read from role /
/// ClaimTypes.Role claims, case-insensitive. A token without any staff role
/// is learner-side even when it has no role claim at all.
/// </summary>
public enum MistakePrincipalKind
{
    Admin,
    Staff,
    Learner,
}

/// <summary>
/// Coarse role gate used by the endpoints whose matrix cells are pure role
/// decisions.
/// </summary>
public enum MistakeResourceAccessGate
{
    /// <summary>POST /api/mistakes/upload, PUT /api/mistakes/{id}.</summary>
    AdminOnly,

    /// <summary>GET pending-reviews, POST /api/mistakes, reviewer workflow endpoints.</summary>
    StaffOrAdmin,

    /// <summary>POST practices, POST process-logs (learner-owned writes).</summary>
    LearnerOnly,
}

public enum MistakeResourceDecisionStatus
{
    Allow,
    Forbidden,
    Unavailable,
}

/// <summary>
/// One authorization decision for a single endpoint invocation. Denials are
/// plain HTTP 403; <see cref="MistakeResourceDecisionStatus.Unavailable"/>
/// carries the fail-closed 503 semantics for learner-scope requests whose
/// ownership resolution cannot be completed. Authorization results never use
/// HTTP 200 + success=false.
/// </summary>
public sealed class MistakeResourceDecision
{
    public MistakeResourceDecisionStatus Status { get; private init; }
    public string Reason { get; private init; } = string.Empty;

    /// <summary>
    /// Learner list-scope injection: the allowed student ids the list query
    /// must be filtered by. Null for every non-scoped decision.
    /// </summary>
    public IReadOnlyList<string>? ScopeStudentIds { get; private init; }

    public bool Allowed => Status == MistakeResourceDecisionStatus.Allow;

    public static MistakeResourceDecision Allow { get; } = new() { Status = MistakeResourceDecisionStatus.Allow };

    public static MistakeResourceDecision AllowScoped(IReadOnlyList<string> studentIds) => new()
    {
        Status = MistakeResourceDecisionStatus.Allow,
        ScopeStudentIds = studentIds
    };

    public static MistakeResourceDecision Forbidden(string reason) => new()
    {
        Status = MistakeResourceDecisionStatus.Forbidden,
        Reason = reason
    };

    public static MistakeResourceDecision Unavailable(string reason) => new()
    {
        Status = MistakeResourceDecisionStatus.Unavailable,
        Reason = reason
    };

    /// <summary>Maps the decision to its HTTP shape (403 / 503).</summary>
    public IResult ToHttpResponse() => Status switch
    {
        MistakeResourceDecisionStatus.Forbidden => MistakeHttpResponse.Forbidden(Reason),
        MistakeResourceDecisionStatus.Unavailable => MistakeHttpResponse.ServiceUnavailable(Reason),
        _ => throw new InvalidOperationException("An allowed decision has no denial response."),
    };
}

public interface IMistakeApiResourceAuthorizationService
{
    bool Enabled { get; }

    /// <summary>Classifies an authenticated caller per the IKJEPZ matrix.</summary>
    MistakePrincipalKind Classify(ClaimsPrincipal user);

    /// <summary>The caller's subject id (sub / NameIdentifier), or null.</summary>
    string? GetSubjectId(ClaimsPrincipal user);

    /// <summary>Role-only gate for endpoints whose cells are pure role decisions.</summary>
    MistakeResourceDecision DecideRoleGate(string endpoint, ClaimsPrincipal user, MistakeResourceAccessGate gate);

    /// <summary>
    /// Staff/Admin reviewer binding: the request reviewerId must equal the
    /// caller's subject id, otherwise 403. Request parameters never grant
    /// identity.
    /// </summary>
    MistakeResourceDecision DecideReviewerBinding(string endpoint, ClaimsPrincipal user, string? reviewerId);

    /// <summary>
    /// Owned-write student binding: the student id supplied by the write
    /// request must equal the stored resource owner (Ordinal), otherwise
    /// 403. Used after the role/ownership decision for writes that carry a
    /// studentId the caller must not choose freely (reanalyze, practices,
    /// process-logs).
    /// </summary>
    MistakeResourceDecision DecideOwnedWriteBinding(
        string endpoint, ClaimsPrincipal user, string resourceId, string? requestedStudentId, string? itemStudentId);

    /// <summary>
    /// Item ownership gate: Staff/Admin pass; a learner must own the item
    /// (itemStudentId ∈ allowed students). Null/empty item student ids deny
    /// learners.
    /// </summary>
    Task<MistakeResourceDecision> DecideItemOwnershipAsync(
        string endpoint, ClaimsPrincipal user, string resourceId, string? itemStudentId, CancellationToken ct);

    /// <summary>
    /// Ownership gate over a by-upload result set: a learner passes only when
    /// every returned item is owned by an allowed student.
    /// </summary>
    Task<MistakeResourceDecision> DecideItemsOwnershipAsync(
        string endpoint, ClaimsPrincipal user, string resourceId, IEnumerable<string?> itemStudentIds, CancellationToken ct);

    /// <summary>
    /// List scope: Staff/Admin keep the baseline behavior; a learner may only
    /// query allowed students. Without a studentId parameter the decision
    /// carries the allowed-student scope the query must be filtered by. An
    /// empty allowed set denies the request (an account mapping to no student
    /// has no student scope at all).
    /// </summary>
    Task<MistakeResourceDecision> DecideListScopeAsync(
        string endpoint, ClaimsPrincipal user, string? requestedStudentId, CancellationToken ct);

    /// <summary>
    /// Presigned-url ownership: Staff/Admin keep the mistakes/ prefix check;
    /// a learner must request a path referenced (Ordinal exact) by an owned
    /// item source region or practice image path.
    /// </summary>
    Task<MistakeResourceDecision> DecidePresignedAccessAsync(
        string endpoint, ClaimsPrincipal user, string objectPath, CancellationToken ct);
}

/// <summary>
/// IKJEPZ per-resource authorization: every /api endpoint checks this service
/// only when <see cref="MistakeApiResourceAuthOptions.Enabled"/> is true. With
/// the switch off (default) no decision method runs and the behavior stays
/// byte-identical to the pre-IKJEPZ baseline. Learner ownership resolution is
/// per request through <see cref="IUploadSourceGateway"/> and is never cached;
/// an unavailable resolution fails closed for learner-scope requests and never
/// degrades to an empty-set or anonymous allowance.
/// </summary>
public sealed class MistakeApiResourceAuthorizationService : IMistakeApiResourceAuthorizationService
{
    private const string ForbiddenEndpointReason = "当前主体无权访问该端点";
    private const string ForbiddenOwnershipReason = "无权访问该学生的错题资源";
    private const string UnavailableReason = "学生归属解析不可用（Student 能力禁用或解析失败），学生范围访问已拒绝";

    private readonly MistakeApiResourceAuthOptions _options;
    private readonly IUploadSourceGateway _uploadSourceGateway;
    private readonly IMistakeDomainService _mistakeDomainService;
    private readonly ILogger<MistakeApiResourceAuthorizationService> _logger;

    public MistakeApiResourceAuthorizationService(
        MistakeApiResourceAuthOptions options,
        IUploadSourceGateway uploadSourceGateway,
        IMistakeDomainService mistakeDomainService,
        ILogger<MistakeApiResourceAuthorizationService> logger)
    {
        _options = options;
        _uploadSourceGateway = uploadSourceGateway;
        _mistakeDomainService = mistakeDomainService;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public MistakePrincipalKind Classify(ClaimsPrincipal user)
    {
        if (user.IsInAnyRole(RoleConstants.Admin))
            return MistakePrincipalKind.Admin;
        if (user.IsInAnyRole(RoleConstants.Teacher, RoleConstants.Assistant))
            return MistakePrincipalKind.Staff;
        return MistakePrincipalKind.Learner;
    }

    public string? GetSubjectId(ClaimsPrincipal user)
    {
        return user.GetUserId();
    }

    public MistakeResourceDecision DecideRoleGate(
        string endpoint, ClaimsPrincipal user, MistakeResourceAccessGate gate)
    {
        var kind = Classify(user);
        var allowed = (kind, gate) switch
        {
            (MistakePrincipalKind.Admin, MistakeResourceAccessGate.AdminOnly) => true,
            (MistakePrincipalKind.Admin, MistakeResourceAccessGate.StaffOrAdmin) => true,
            (MistakePrincipalKind.Staff, MistakeResourceAccessGate.StaffOrAdmin) => true,
            (MistakePrincipalKind.Learner, MistakeResourceAccessGate.LearnerOnly) => true,
            _ => false,
        };
        var decision = allowed
            ? MistakeResourceDecision.Allow
            : MistakeResourceDecision.Forbidden(ForbiddenEndpointReason);
        LogDecision(endpoint, user, kind, resource: null, decision);
        return decision;
    }

    public MistakeResourceDecision DecideReviewerBinding(
        string endpoint, ClaimsPrincipal user, string? reviewerId)
    {
        var kind = Classify(user);
        var subjectId = GetSubjectId(user);
        var bound = kind is MistakePrincipalKind.Admin or MistakePrincipalKind.Staff
            && !string.IsNullOrWhiteSpace(reviewerId)
            && string.Equals(reviewerId, subjectId, StringComparison.Ordinal);
        var decision = bound
            ? MistakeResourceDecision.Allow
            : MistakeResourceDecision.Forbidden(ForbiddenEndpointReason);
        LogDecision(endpoint, user, kind, resource: reviewerId, decision);
        return decision;
    }

    public MistakeResourceDecision DecideOwnedWriteBinding(
        string endpoint, ClaimsPrincipal user, string resourceId, string? requestedStudentId, string? itemStudentId)
    {
        var kind = Classify(user);
        var bound = !string.IsNullOrWhiteSpace(itemStudentId)
            && string.Equals(requestedStudentId, itemStudentId, StringComparison.Ordinal);
        var decision = bound
            ? MistakeResourceDecision.Allow
            : MistakeResourceDecision.Forbidden(ForbiddenOwnershipReason);
        LogDecision(endpoint, user, kind, resourceId, decision);
        return decision;
    }

    public async Task<MistakeResourceDecision> DecideItemOwnershipAsync(
        string endpoint, ClaimsPrincipal user, string resourceId, string? itemStudentId, CancellationToken ct)
    {
        var kind = Classify(user);
        if (kind is MistakePrincipalKind.Admin or MistakePrincipalKind.Staff)
        {
            var allow = MistakeResourceDecision.Allow;
            LogDecision(endpoint, user, kind, resourceId, allow);
            return allow;
        }

        var resolution = await ResolveAllowedAsync(user, ct).ConfigureAwait(false);
        if (!resolution.Available)
        {
            var unavailable = MistakeResourceDecision.Unavailable(UnavailableReason);
            LogDecision(endpoint, user, kind, resourceId, unavailable);
            return unavailable;
        }

        var owned = itemStudentId is not null
            && resolution.StudentIds.Contains(itemStudentId, StringComparer.Ordinal);
        var decision = owned
            ? MistakeResourceDecision.Allow
            : MistakeResourceDecision.Forbidden(ForbiddenOwnershipReason);
        LogDecision(endpoint, user, kind, resourceId, decision);
        return decision;
    }

    public async Task<MistakeResourceDecision> DecideItemsOwnershipAsync(
        string endpoint, ClaimsPrincipal user, string resourceId, IEnumerable<string?> itemStudentIds, CancellationToken ct)
    {
        var kind = Classify(user);
        if (kind is MistakePrincipalKind.Admin or MistakePrincipalKind.Staff)
        {
            var allow = MistakeResourceDecision.Allow;
            LogDecision(endpoint, user, kind, resourceId, allow);
            return allow;
        }

        var resolution = await ResolveAllowedAsync(user, ct).ConfigureAwait(false);
        if (!resolution.Available)
        {
            var unavailable = MistakeResourceDecision.Unavailable(UnavailableReason);
            LogDecision(endpoint, user, kind, resourceId, unavailable);
            return unavailable;
        }

        var owned = itemStudentIds.All(studentId =>
            studentId is not null && resolution.StudentIds.Contains(studentId, StringComparer.Ordinal));
        var decision = owned
            ? MistakeResourceDecision.Allow
            : MistakeResourceDecision.Forbidden(ForbiddenOwnershipReason);
        LogDecision(endpoint, user, kind, resourceId, decision);
        return decision;
    }

    public async Task<MistakeResourceDecision> DecideListScopeAsync(
        string endpoint, ClaimsPrincipal user, string? requestedStudentId, CancellationToken ct)
    {
        var kind = Classify(user);
        if (kind is MistakePrincipalKind.Admin or MistakePrincipalKind.Staff)
        {
            var allow = MistakeResourceDecision.Allow;
            LogDecision(endpoint, user, kind, resource: requestedStudentId, allow);
            return allow;
        }

        var resolution = await ResolveAllowedAsync(user, ct).ConfigureAwait(false);
        if (!resolution.Available)
        {
            var unavailable = MistakeResourceDecision.Unavailable(UnavailableReason);
            LogDecision(endpoint, user, kind, resource: requestedStudentId, unavailable);
            return unavailable;
        }

        if (!string.IsNullOrWhiteSpace(requestedStudentId))
        {
            var inScope = resolution.StudentIds.Contains(requestedStudentId, StringComparer.Ordinal);
            var decision = inScope
                ? MistakeResourceDecision.Allow
                : MistakeResourceDecision.Forbidden(ForbiddenOwnershipReason);
            LogDecision(endpoint, user, kind, requestedStudentId, decision);
            return decision;
        }

        // No studentId parameter: inject the allowed-student scope. An empty
        // allowed set means the account maps to no student — there is no
        // learner scope to serve, deny instead of returning an empty page.
        if (resolution.StudentIds.Count == 0)
        {
            var empty = MistakeResourceDecision.Forbidden(ForbiddenOwnershipReason);
            LogDecision(endpoint, user, kind, resource: null, empty);
            return empty;
        }

        var scoped = MistakeResourceDecision.AllowScoped(resolution.StudentIds);
        LogDecision(endpoint, user, kind, resource: null, scoped);
        return scoped;
    }

    public async Task<MistakeResourceDecision> DecidePresignedAccessAsync(
        string endpoint, ClaimsPrincipal user, string objectPath, CancellationToken ct)
    {
        var kind = Classify(user);
        if (kind is MistakePrincipalKind.Admin or MistakePrincipalKind.Staff)
        {
            // Review visibility: the mistakes/ prefix check stays in the
            // endpoint; staff/admin are not restricted per student here.
            var allow = MistakeResourceDecision.Allow;
            LogDecision(endpoint, user, kind, objectPath, allow);
            return allow;
        }

        var resolution = await ResolveAllowedAsync(user, ct).ConfigureAwait(false);
        if (!resolution.Available)
        {
            var unavailable = MistakeResourceDecision.Unavailable(UnavailableReason);
            LogDecision(endpoint, user, kind, objectPath, unavailable);
            return unavailable;
        }

        // An account mapping to no student owns no object path at all.
        var referenced = resolution.StudentIds.Count > 0
            && await _mistakeDomainService
                .IsObjectPathOwnedByStudentsAsync(objectPath, resolution.StudentIds, ct)
                .ConfigureAwait(false);
        var decision = referenced
            ? MistakeResourceDecision.Allow
            : MistakeResourceDecision.Forbidden(ForbiddenOwnershipReason);
        LogDecision(endpoint, user, kind, objectPath, decision);
        return decision;
    }

    private async Task<AllowedStudentsResolution> ResolveAllowedAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var subjectId = GetSubjectId(user);
        if (string.IsNullOrWhiteSpace(subjectId))
            return AllowedStudentsResolution.Unavailable;
        return await _uploadSourceGateway.GetAllowedStudentIdsAsync(subjectId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Structured authorization audit: subject, endpoint, resource and result
    /// only. Tokens and presigned URLs are never logged; the resource field
    /// carries the item/upload id, student id or object path being decided.
    /// </summary>
    private void LogDecision(
        string endpoint, ClaimsPrincipal user, MistakePrincipalKind kind,
        string? resource, MistakeResourceDecision decision)
    {
        _logger.LogInformation(
            "Mistake resource authorization: Endpoint={Endpoint}, Subject={SubjectId}, Kind={Kind}, Resource={Resource}, Decision={Decision}",
            endpoint, GetSubjectId(user) ?? "(missing)", kind,
            endpoint.Contains("image", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("presigned", StringComparison.OrdinalIgnoreCase)
                ? "(protected-image)" : resource ?? "(none)", decision.Status);
    }
}
