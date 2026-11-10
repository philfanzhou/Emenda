using Microsoft.AspNetCore.Http;
using SignaCore.Client.AspNetCore;

namespace Emenda.Host.Authentication;

/// <summary>
/// Presents the package's protocol outcomes in this host's fixed shapes: sign-in
/// failures redirect to the console page with one bounded <c>authError</c>
/// value, an invalid return URL answers the fixed 400 body, and the session
/// endpoint keeps the <c>success</c>/<c>data</c> envelope. No other failure
/// detail ever reaches the browser.
/// </summary>
/// <remarks>
/// Reason mapping: <see cref="SignaCoreSignInReason.UserCanceled"/> and
/// <see cref="SignaCoreSignInReason.PreSignInDenied"/> surface as
/// <c>authError=notAdmin</c>; discovery and token-endpoint transport failures
/// surface as <c>authError=identityUnavailable</c>; every other protocol
/// failure surfaces as <c>authError=signInFailed</c>.
/// </remarks>
public sealed class AdminConsoleResponseWriter : ISignaCoreHostedLoginResponseWriter
{
    public static readonly AdminConsoleResponseWriter Instance = new();

    internal const string SignInFailedRedirect = AdminConsoleConstants.ConsolePath + "?authError=signInFailed";
    internal const string DeniedRedirect = AdminConsoleConstants.ConsolePath + "?authError=notAdmin";
    internal const string IdentityUnavailableRedirect = AdminConsoleConstants.ConsolePath + "?authError=identityUnavailable";
    internal const string InvalidReturnUrlMessage = "The return URL must be a path inside this site.";
    internal const string ReauthenticationRequired = "Re-authentication is required.";

    public Task WriteSignInFailureAsync(HttpContext context, SignaCoreSignInReason reason,
        CancellationToken cancellationToken)
    {
        if (reason == SignaCoreSignInReason.InvalidReturnUrl)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsJsonAsync(
                new { success = false, message = InvalidReturnUrlMessage }, cancellationToken);
        }
        context.Response.Redirect(RedirectTarget(reason));
        return Task.CompletedTask;
    }

    public Task WriteFailurePageAsync(HttpContext context, SignaCoreSignInReason? reason,
        CancellationToken cancellationToken)
    {
        context.Response.Redirect(reason is { } value ? RedirectTarget(value) : SignInFailedRedirect);
        return Task.CompletedTask;
    }

    public async Task WriteSessionStatusAsync(HttpContext context, SignaCoreSessionStatus status,
        CancellationToken cancellationToken)
    {
        if (!status.Authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = ReauthenticationRequired,
                data = new { authenticated = false, reason = "reauthenticationRequired" }
            }, cancellationToken);
            return;
        }

        DateTimeOffset? expiresAt = null;
        if (context.Request.Cookies.TryGetValue(AdminConsoleConstants.SessionCookie, out var key)
            && !string.IsNullOrEmpty(key))
        {
            var ticket = await context.RequestServices.GetRequiredService<ITicketStore>()
                .RetrieveAsync(key, cancellationToken);
            expiresAt = ticket?.ExpiresUtc;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new
        {
            success = true,
            data = new
            {
                authenticated = true,
                reason = "authenticated",
                username = status.DisplayName,
                expiresAt
            }
        }, cancellationToken);
    }

    internal static string RedirectTarget(SignaCoreSignInReason reason) => reason switch
    {
        SignaCoreSignInReason.AccessDenied or SignaCoreSignInReason.UserCanceled
            or SignaCoreSignInReason.PreSignInDenied => DeniedRedirect,
        SignaCoreSignInReason.AuthorityUnreachable => IdentityUnavailableRedirect,
        _ => SignInFailedRedirect
    };
}
