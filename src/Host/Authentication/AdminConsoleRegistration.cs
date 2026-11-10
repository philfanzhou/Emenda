using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using SignaCore.Client.AspNetCore;

namespace Emenda.Host.Authentication;

/// <summary>
/// Pre-sign-in authorization gate of the admin console: a session may only be
/// established for a subject whose strictly validated SignaCore access token
/// carries the configured role (default <c>admin</c>). Ordinary users are
/// denied before any ticket or cookie is written; every non-Allowed outcome
/// fails closed.
/// </summary>
public sealed class AdminPreSignInRoleGate(string requiredRole)
    : ISignaCorePreSignInAuthorizationDecision
{
    public static AdminPreSignInRoleGate Default { get; } = new("admin");

    public ValueTask<SignaCoreAuthorizationDecisionResult> DecideAsync(
        SignaCorePreSignInAuthorizationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = context.AccessTokenPrincipal.Claims.Any(claim =>
            claim.Type is "role" or ClaimTypes.Role
            && string.Equals(claim.Value, requiredRole, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(
            allowed
                ? SignaCoreAuthorizationDecisionResult.Allowed
                : SignaCoreAuthorizationDecisionResult.Denied);
    }
}

/// <summary>
/// Registers the SignaCore hosted-login slice for the Emenda admin console
/// through the official client package, following the same host adaptation
/// pattern as the Lexarbor/Doctheca consoles: the package owns the OIDC code
/// flow, the server-side session tickets and the CSRF boundary; this host adds
/// only the availability precheck, the role gate, the fixed response shapes
/// and the console authorization policy. Emenda has no Bearer/JwtBearer
/// surface in standalone mode, so the package's forwarding scheme always
/// resolves to its session scheme.
/// </summary>
public static class AdminConsoleRegistration
{
    internal const int TicketCapacity = 256;

    public static IServiceCollection AddEmendaAdminConsole(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = AdminConsoleSettings.Read(configuration);
        services.AddSingleton(settings);
        if (!settings.Available) return services;

        services.AddSignaCoreHostedLogin(options =>
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.RedirectUri = settings.RedirectUri;
            options.Scope = "openid profile";
            options.Validation.ClockSkew = TimeSpan.FromSeconds(30);
            options.SessionCookieName = AdminConsoleConstants.SessionCookie;
            options.AntiforgeryHeaderName = AdminConsoleConstants.CsrfHeader;
            options.TicketCapacity = TicketCapacity;
            options.PreSignInAuthorizationDecision = new AdminPreSignInRoleGate(settings.RequiredRole);
            options.ResponseWriter = AdminConsoleResponseWriter.Instance;
            options.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
            options.PostLogoutReturnPath = AdminConsoleConstants.LogoutReturnPath;
        });
        // Keep the public cookie name; the package owns the Secure policy derived
        // from the configured callback scheme, independently of the environment.
        services.AddOptions<AntiforgeryOptions>()
            .PostConfigure(options => options.Cookie.Name = AdminConsoleConstants.CsrfCookie);
        services.AddAuthorization(options => options.AddPolicy(AdminConsoleConstants.Policy, policy =>
        {
            policy.AddAuthenticationSchemes(SignaCoreHostedLoginDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context =>
                context.User.Identities.Any(identity => identity.IsAuthenticated));
        }));
        return services;
    }
}
