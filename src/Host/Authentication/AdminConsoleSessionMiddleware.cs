using Microsoft.AspNetCore.Authentication;
using SignaCore.Client.AspNetCore;

namespace Emenda.Host.Authentication;

/// <summary>
/// Request boundary for cookie-session access to the protected admin console
/// API (hosted login): only requests that actually carry the opaque session
/// cookie enter the boundary, only under <c>/admin</c> outside the anonymous
/// <c>/admin/auth</c> area. An expired or unknown session answers the fixed
/// re-authentication JSON before any endpoint runs, and a session-authenticated
/// unsafe method without a valid antiforgery credential answers the fixed CSRF
/// JSON (the CSRF validation itself runs inside the package's session handler
/// during authentication and fails closed).
/// </summary>
public sealed class AdminConsoleSessionMiddleware(RequestDelegate next)
{
    internal const string ReauthenticationRequired = "Re-authentication is required.";
    internal const string CsrfRequired = "A valid anti-forgery token is required.";

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<AdminConsoleSettings>();
        if (settings.Available
            && context.Request.Path.StartsWithSegments("/admin")
            && !context.Request.Path.StartsWithSegments("/admin/auth")
            && context.Request.Cookies.TryGetValue(AdminConsoleConstants.SessionCookie, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie))
        {
            var session = await context.AuthenticateAsync(
                SignaCoreHostedLoginDefaults.SessionAuthenticationScheme);
            if (session.Succeeded)
            {
                context.User = session.Principal!;
            }
            else if (session.Failure is not null)
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, CsrfRequired);
                return;
            }
            else
            {
                await RejectAsync(context, StatusCodes.Status401Unauthorized, ReauthenticationRequired);
                return;
            }
        }

        await next(context);
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { success = false, message }, context.RequestAborted);
    }
}
