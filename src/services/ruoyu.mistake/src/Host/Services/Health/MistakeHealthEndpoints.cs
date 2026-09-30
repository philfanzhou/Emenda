using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ruoyu.Study.Mistake.Host.Services.Health;

/// <summary>
/// Anonymous health endpoints (IKJFIX). Both live outside the /api group, so
/// they stay reachable without a token in the anonymous compatibility window
/// and under strict authentication alike.
/// </summary>
public static class MistakeHealthEndpoints
{
    /// <summary>
    /// Maps GET /health/live (pure process liveness, no external IO, always 200
    /// while the process can serve) and GET /health/ready (per-component
    /// dependency probe of the startup capability snapshot; 200 when every
    /// applicable component passes, 503 with a per-component breakdown
    /// otherwise).
    /// </summary>
    public static IEndpointRouteBuilder MapMistakeHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (MistakeReadinessProbe probe, CancellationToken cancellationToken) =>
        {
            var components = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var allOk = components.All(component => component.Ok);
            var payload = new
            {
                status = allOk ? "ok" : "unavailable",
                checks = components.Select(component => new
                {
                    name = component.Name,
                    ok = component.Ok,
                    reason = component.Reason,
                }),
            };
            return allOk
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }
}
