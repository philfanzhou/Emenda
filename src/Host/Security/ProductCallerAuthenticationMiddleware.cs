using System.Security.Claims;
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

    private static readonly object ExtractionOperationsItemKey = new();
    private static readonly object OperationsGranted = true;
    private static readonly object OperationsDenied = false;

    private readonly RequestDelegate _next;
    private readonly ILogger<ProductCallerAuthenticationMiddleware> _logger;

    public ProductCallerAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<ProductCallerAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Requires a verified machine credential with extraction operations access.</summary>
    public static IResult? RequireExtractionOperations(HttpContext http)
    {
        if (!http.Items.TryGetValue(ExtractionOperationsItemKey, out var grant) || grant is not bool canOperateExtractions)
        {
            return Results.Json(new { success = false, message = "caller authentication required" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return canOperateExtractions ? null : Results.Json(
            new { success = false, message = "extraction operations capability required" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    public async Task InvokeAsync(
        HttpContext context,
        CallerRegistry registry)
    {
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<ProductCallerAuthenticationOptions>>()
            .CurrentValue;
        if (options.AllowAnonymous || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var provided = context.Request.Headers[ProductCallerAuthenticationOptions.ApiKeyHeaderName].ToString();
        CallerAuthenticationMatch? match;
        try
        {
            match = await registry.MatchAsync(provided, context.RequestAborted).ConfigureAwait(false);
        }
        catch (DynamicCallerStoreUnavailableException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "caller credential storage unavailable"
            }, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (match is not { } caller)
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

        context.Items[ExtractionOperationsItemKey] = caller.CanOperateExtractions ? OperationsGranted : OperationsDenied;
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
}