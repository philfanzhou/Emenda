using Emenda.Host.Security;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;

namespace Emenda.Host;

public static partial class ExtractionEndpoints
{
    private static void MapOperations(RouteGroupBuilder group, bool administrator)
    {
        var routes = group.MapGroup("");
        if (!administrator)
            routes.AddEndpointFilter(async (context, next) =>
                ProductCallerAuthenticationMiddleware.RequireExtractionOperations(context.HttpContext) ?? await next(context));

        var listPath = administrator ? "/summary" : "";
        routes.MapGet(listPath, async (int? page, string? studentId, ExtractionService service, CancellationToken ct) =>
            Ok(await service.ListSummariesAsync(page ?? 1, ct, studentId)));
        routes.MapDelete("/{id:guid}", async (HttpContext http, Guid id, IExtractionDeletionService service, CancellationToken ct) =>
            Ok(await service.DeleteAsync(OperatorAccess(http, administrator), id, ct)));
        routes.MapGet("/{id:guid}/deletion", async (HttpContext http, Guid id, IExtractionDeletionService service, CancellationToken ct) =>
            Ok(await service.GetDeletionAsync(OperatorAccess(http, administrator), id, ct)));
    }

    private static ExtractionAccess OperatorAccess(HttpContext http, bool administrator)
    {
        var access = Access(http, administrator);
        return administrator ? access : access with { Administrator = true };
    }
}
