using Ruoyu.Study.Mistake.HttpContracts;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace Ruoyu.Study.Mistake.Host;

/// <summary>Immutable release metadata, independent of every business dependency.</summary>
public static class MistakeContractEndpoints
{
    public const string Version = "mistake-http-v1";
    private static readonly byte[] Document = ReadDocument();
    public static string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Document)).ToLowerInvariant();
    private static readonly EntityTagHeaderValue Tag = new('"' + Sha256 + '"');

    public static IEndpointRouteBuilder MapMistakeContractEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/contracts/{version}.json", async (string version, HttpContext http) =>
        {
            http.RequestAborted.ThrowIfCancellationRequested();
            if (!string.Equals(version, Version, StringComparison.Ordinal))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.GetTypedHeaders().ETag = Tag;
            http.Response.Headers.CacheControl = "public, no-cache";
            // Weak comparison is correct for conditional GET. A malformed condition is ignored.
            if (EntityTagHeaderValue.TryParseList(http.Request.Headers.IfNoneMatch.Select(value => value ?? string.Empty).ToArray(), out var conditions)
                && conditions.Any(value => value == EntityTagHeaderValue.Any || value.Compare(Tag, useStrongComparison: false)))
            {
                http.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            http.Response.ContentType = "application/json";
            http.Response.ContentLength = Document.Length;
            await http.Response.Body.WriteAsync(Document, http.RequestAborted);
        }).AllowAnonymous();
        return endpoints;
    }

    private static byte[] ReadDocument()
    {
        using var stream = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream("mistake-http-v1.json")
            ?? throw new InvalidOperationException("The release must embed mistake-http-v1.json.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
