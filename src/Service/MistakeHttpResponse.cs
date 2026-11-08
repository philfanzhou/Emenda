using System.Net;
using Microsoft.AspNetCore.Http;

namespace Emenda.Service;

/// <summary>
/// Unified HTTP response envelope helper for all Mistake HTTP endpoints.
/// Response format:
///   Success: { "success": true, "data": ... } / { "success": true }
///   Failure: { "success": false, "message": "..." }
/// </summary>
public static class MistakeHttpResponse
{
    /// <summary>
    /// Success response: { "success": true, "data": ... }
    /// </summary>
    public static IResult Ok<T>(T data) => Results.Ok(new { success = true, data });

    /// <summary>
    /// Success response (no data, just success=true).
    /// </summary>
    public static IResult Ok() => Results.Ok(new { success = true });

    /// <summary>
    /// 400 Bad Request: { "success": false, "message": "..." }
    /// </summary>
    public static IResult BadRequest(string message)
        => Results.BadRequest(new { success = false, message });

    /// <summary>
    /// 404 Not Found: { "success": false, "message": "..." }
    /// </summary>
    public static IResult NotFound(string message)
        => Results.NotFound(new { success = false, message });

    /// <summary>
    /// 403 Forbidden: { "success": false, "message": "..." }.
    /// Per-resource authorization denials (IKJEPZ) always use this status;
    /// they are never expressed as HTTP 200 + success=false.
    /// </summary>
    public static IResult Forbidden(string message)
        => Results.Json(new { success = false, message }, statusCode: (int)HttpStatusCode.Forbidden);

    /// <summary>
    /// 500 Internal Server Error: { "success": false, "message": "..." }
    /// </summary>
    public static IResult Internal(string message)
        => Results.Json(new { success = false, message }, statusCode: (int)HttpStatusCode.InternalServerError);

    /// <summary>
    /// 503 Service Unavailable: { "success": false, "message": "..." }.
    /// Stable shape for capability-unavailable failures; no partial side
    /// effects have been applied when this status is returned.
    /// </summary>
    public static IResult ServiceUnavailable(string message)
        => Results.Json(new { success = false, message }, statusCode: (int)HttpStatusCode.ServiceUnavailable);
}
