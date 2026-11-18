using Emenda.Host.Security;
using Emenda.Service;

namespace Emenda.Host.Services.Conversation;

public static class ConversationCollectionEndpoints
{
    public static void MapConversationCollectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mistakes/conversation-collections");
        group.AddEndpointFilter(ServiceOnly);
        app.MapPost("/api/mistakes/conversation-collections", async (CollectionAdmission input, HttpContext http, ConversationCollectionStore store, CancellationToken ct) =>
        {
            try { await store.AdmitAsync(Caller(http), input.ActorId, input.OperationId, input.StudentId, input.OwnerId, input.Grade, ct); return MistakeHttpResponse.Ok(new { operationId = input.OperationId }); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
            catch (ArgumentException) { return MistakeHttpResponse.BadRequest("Invalid collection request."); }
            catch (InvalidDataException) { return Results.Conflict(new { code = "COLLECTION_CONFLICT" }); }
            catch (InvalidOperationException) { return Results.Json(new { code = "COLLECTION_BUSY" }, statusCode: 429); }
        }).AddEndpointFilter(ServiceOnly);
        group.MapGet("/{id:guid}", async (Guid id, string studentId, string actorId, HttpContext http, ConversationCollectionStore store, CancellationToken ct) =>
        {
            try
            {
                ConversationCollectionStore.ValidateIdentity(actorId, studentId, "status");
                var job = await store.StatusAsync(Caller(http), actorId, studentId, id, ct);
                return job == null ? Results.NotFound() : MistakeHttpResponse.Ok(new { operationId = job.Id, studentId = job.StudentId, status = job.Status, errorCode = job.ErrorCode,
                    result = job.Result == null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(job.Result) });
            }
            catch (ArgumentException) { return MistakeHttpResponse.BadRequest("Invalid collection identity."); }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
        });
        group.MapPost("/stop", async (CollectionStop input, HttpContext http, ConversationCollectionStore store, CancellationToken ct) =>
        {
            try { await store.StopAsync(Caller(http), input.ActorId, input.StudentId, ct); return MistakeHttpResponse.Ok(new { stopped = true }); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
            catch (ArgumentException) { return MistakeHttpResponse.BadRequest("Invalid collection identity."); }
        });
        app.MapPost("/api/mistakes/{id:guid}/conversation-analysis", async (Guid id, ConversationAnalysisWrite input, HttpContext http, ConversationAnalysisStore store, CancellationToken ct) =>
        {
            try { return await store.SaveAsync(Caller(http), id, input, ct) ? MistakeHttpResponse.Ok(new { saved = true, evidenceHash = input.EvidenceHash }) : Results.Conflict(new { code = "EVIDENCE_CHANGED" }); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
            catch (ArgumentException) { return MistakeHttpResponse.BadRequest("Invalid analysis."); }
        }).AddEndpointFilter(ServiceOnly);
    }
    private static string Caller(HttpContext http) => (string)http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey]!;
    private static ValueTask<object?> ServiceOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<ConversationCollectionOptions>();
        if (!options.Enabled) return ValueTask.FromResult<object?>(Results.NotFound());
        if (http.Request.Headers.ContainsKey("Origin") || http.Request.Headers.ContainsKey("Sec-Fetch-Site")
            || http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey] is not string caller
            || caller != options.CallerName || !options.Authorizes(http.Request.Headers["X-Question-Collection-Key"]))
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        return next(context);
    }
    public sealed record CollectionAdmission(Guid OperationId, string StudentId, int Grade, string ActorId, string OwnerId);
    public sealed record CollectionStop(string StudentId, string ActorId);
}
