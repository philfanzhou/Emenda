using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Emenda.Host.Security;

/// <summary>
/// Authenticates product callers with <c>X-Emenda-Api-Key</c> and rejects
/// unauthenticated access to the business <c>/api</c> surface. Health,
/// contract and root endpoints stay anonymous. The caller name is recorded on
/// the request for audit and logging; student ownership is not resolved here
/// (the caller owns that responsibility).
/// </summary>
public sealed class ProductCallerAuthenticationMiddleware
{
    public const string CallerNameItemKey = "Emenda.CallerName";

    private readonly RequestDelegate _next;
    private readonly ILogger<ProductCallerAuthenticationMiddleware> _logger;

    public ProductCallerAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<ProductCallerAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOptionsMonitor<ProductCallerAuthenticationOptions> optionsMonitor)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled || options.AllowAnonymous || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var provided = context.Request.Headers[ProductCallerAuthenticationOptions.ApiKeyHeaderName].ToString();
        var caller = MatchCaller(options, provided);
        if (caller is null)
        {
            _logger.LogWarning(
                "Product caller authentication failed for {Method} {Path}",
                context.Request.Method,
                context.Request.Path.Value);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "caller authentication required"
            }).ConfigureAwait(false);
            return;
        }

        context.Items[CallerNameItemKey] = caller.Name;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, caller.Name),
                new Claim("emenda:caller", caller.Name)
            },
            authenticationType: "ProductCaller"));

        await _next(context).ConfigureAwait(false);
    }

    private static ProductCallerCredential? MatchCaller(
        ProductCallerAuthenticationOptions options,
        string provided)
    {
        if (string.IsNullOrWhiteSpace(provided))
            return null;

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        ProductCallerCredential? matched = null;
        foreach (var candidate in options.ValidCallers)
        {
            var expected = Encoding.UTF8.GetBytes(candidate.ApiKey);
            if (expected.Length == providedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expected, providedBytes))
            {
                matched = candidate;
            }
        }

        return matched;
    }
}