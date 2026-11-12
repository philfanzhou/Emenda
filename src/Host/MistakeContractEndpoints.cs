using Emenda.HttpContracts;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace Emenda.Host;

/// <summary>Immutable release metadata, independent of every business dependency.</summary>
public static class MistakeContractEndpoints
{
    public const string Version = "mistake-http-v3";
    public const string ProductVersion = "emenda-intake-v1";
    private static readonly byte[] ProductDocument = ReadDocument(ProductVersion);
    private static readonly byte[] Document = ReadDocument(Version);
    private static readonly IReadOnlyDictionary<string, byte[]> HistoricalDocuments = new Dictionary<string, byte[]>
    {
        ["mistake-http-v1"] = ReadDocument("mistake-http-v1"),
        ["mistake-http-v2"] = ReadDocument("mistake-http-v2")
    };
    public static string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Document)).ToLowerInvariant();
    private static readonly EntityTagHeaderValue Tag = new('"' + Sha256 + '"');

    public static IEndpointRouteBuilder MapMistakeContractEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/contracts/{version}.json", async (string version, HttpContext http) =>
        {
            http.RequestAborted.ThrowIfCancellationRequested();
            if (version != Version && version != ProductVersion && !HistoricalDocuments.ContainsKey(version))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var document = version == ProductVersion ? ProductDocument : version == Version ? Document : HistoricalDocuments[version];
            var tag = version == Version ? Tag : new EntityTagHeaderValue('"' + Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant() + '"');
            if (HistoricalDocuments.ContainsKey(version))
            {
                http.Response.Headers["Link"] = "</contracts/" + Version + ".json>; rel=\"latest-version\"";
                http.Response.Headers["X-Emenda-Contract-Status"] = "historical";
            }
            http.Response.GetTypedHeaders().ETag = tag;
            http.Response.Headers.CacheControl = "public, no-cache";
            // Weak comparison is correct for conditional GET. A malformed condition is ignored.
            if (EntityTagHeaderValue.TryParseList(http.Request.Headers.IfNoneMatch.Select(value => value ?? string.Empty).ToArray(), out var conditions)
                && conditions.Any(value => value == EntityTagHeaderValue.Any || value.Compare(tag, useStrongComparison: false)))
            {
                http.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            http.Response.ContentType = "application/json";
            http.Response.ContentLength = document.Length;
            await http.Response.Body.WriteAsync(document, http.RequestAborted);
        }).AllowAnonymous();
        return endpoints;
    }

    private static byte[] ReadDocument(string version)
    {
        using var stream = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream(version + ".json")
            ?? throw new InvalidOperationException("The release must embed every published Mistake contract.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
