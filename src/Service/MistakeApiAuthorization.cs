using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Emenda.Common.Authentication;

namespace Emenda.Service;

/// <summary>
/// Authorization policy used by the Mistake /api endpoint group when
/// <c>IdentityService:MistakeApiStrictAuth</c> is enabled: any caller
/// authenticated through the shared-audience scheme or the Admin AppId
/// audience scheme is accepted. The policy intentionally has no role or
/// resource-ownership requirement; per-resource authorization is a separate
/// slice of the parent tracking issue (IKIZY0).
/// </summary>
public static class MistakeApiAuthorization
{
    public const string PolicyName = "MistakeApiAuthenticatedUser";

    /// <summary>
    /// Adds the Mistake /api policy: authentication only, through either the
    /// default shared-audience scheme or the optional Admin AppId audience
    /// scheme (which fails closed when <c>IdentityService:AdminAudience</c>
    /// is not configured).
    /// </summary>
    public static void AddMistakeApiPolicy(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                EmendaAdminApiAuthorization.SchemeName)
            .RequireAuthenticatedUser());
    }
}
