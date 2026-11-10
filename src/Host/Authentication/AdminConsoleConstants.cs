namespace Emenda.Host.Authentication;

/// <summary>
/// Fixed public identifiers of the Emenda admin console's SignaCore
/// hosted-login slice. The OIDC protocol itself lives in the official
/// <c>SignaCore.Client.AspNetCore</c> package; these names stay stable so the
/// browser-facing contract (cookie and header names, route paths) is owned by
/// this product.
/// </summary>
public static class AdminConsoleConstants
{
    /// <summary>The hosted-login route prefix the package endpoints are mounted under.</summary>
    public const string Prefix = "/admin/auth/oidc";

    public const string CallbackPath = "/admin/auth/oidc/callback";

    public const string LogoutReturnPath = "/admin/auth/oidc/logout/return";

    public const string SessionCookie = "emendaAdminSession";

    public const string CsrfCookie = "emendaAdminCsrf";

    public const string CsrfHeader = "X-CSRF-TOKEN";

    /// <summary>The admin console page a completed prepared logout returns to.</summary>
    public const string ConsolePath = "/admin";

    /// <summary>The authorization policy protecting every admin console endpoint.</summary>
    public const string Policy = "EmendaAdminConsole";

    internal const string NotConfiguredMessage = "The admin console is not configured.";
}

/// <summary>
/// Bound and validated configuration of the admin console slice
/// (<c>Emenda:Admin</c>). The availability precheck runs before the official
/// client package is registered: without the required keys the package is
/// never wired, the console page answers a fixed 503 result, and the public
/// API keeps running.
/// </summary>
public sealed record AdminConsoleSettings(
    bool Available,
    string Authority,
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    string PostLogoutRedirectUri,
    string RequiredRole = "admin")
{
    public IReadOnlyList<string> MissingKeys { get; init; } = [];

    internal static AdminConsoleSettings Read(IConfiguration configuration)
    {
        var required = new[]
        {
            "Emenda:Admin:Oidc:Authority",
            "Emenda:Admin:Oidc:ClientId",
            "Emenda:Admin:Oidc:ClientSecret",
            "Emenda:Admin:Oidc:RedirectUri",
            "Emenda:Admin:Oidc:PostLogoutRedirectUri"
        };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
        var requiredRole = string.IsNullOrWhiteSpace(configuration["Emenda:Admin:RequiredRole"])
            ? "admin"
            : configuration["Emenda:Admin:RequiredRole"]!;
        if (missing.Length > 0)
            return new AdminConsoleSettings(false, "", "", "", "", "", requiredRole) { MissingKeys = missing };

        var authority = (configuration["Emenda:Admin:Oidc:Authority"] ?? "").TrimEnd('/');
        var redirect = configuration["Emenda:Admin:Oidc:RedirectUri"] ?? "";
        var postLogout = configuration["Emenda:Admin:Oidc:PostLogoutRedirectUri"] ?? "";
        if (!IsSafeUri(authority, out _)) throw new InvalidOperationException("Emenda:Admin:Oidc:Authority");
        if (!IsSafeUri(redirect, out var redirectUri)
            || redirectUri!.AbsolutePath != AdminConsoleConstants.CallbackPath
            || redirectUri.AbsoluteUri != redirect || redirect.Length > 500 || redirect.Any(c => c > 127))
            throw new InvalidOperationException("Emenda:Admin:Oidc:RedirectUri");
        if (!IsSafeUri(postLogout, out var postLogoutUri)
            || postLogoutUri!.AbsolutePath != AdminConsoleConstants.LogoutReturnPath
            || postLogoutUri.AbsoluteUri != postLogout || postLogout.Length > 500 || postLogout.Any(c => c > 127)
            || postLogoutUri.GetLeftPart(UriPartial.Authority) != redirectUri.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Emenda:Admin:Oidc:PostLogoutRedirectUri");
        return new AdminConsoleSettings(
            true,
            authority,
            configuration["Emenda:Admin:Oidc:ClientId"]!,
            configuration["Emenda:Admin:Oidc:ClientSecret"]!,
            redirect,
            postLogout,
            requiredRole);
    }

    internal static bool IsSafeUri(string value, out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && !value.Any(char.IsControl) && !value.Contains('*')
            && uri.Scheme is "http" or "https";
    }

    public override string ToString() => "AdminConsoleSettings";
}
