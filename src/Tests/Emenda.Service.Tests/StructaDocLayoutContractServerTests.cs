using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Emenda.Host.Services.Layout;

namespace Emenda.Service.Tests;

/// <summary>
/// IKQA2N contract tests against a loopback Kestrel test server (ephemeral port 0, real
/// HTTP exchanges, no database or containers): the multipart request the client actually
/// sends, the per-image budget governing real exchanges, the concurrency bound, streamed
/// oversized-response rejection, and the budget headroom behavior.
/// </summary>
public sealed class StructaDocLayoutContractServerTests
{
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[32];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    [Fact]
    public async Task RealExchangeSendsOneFileSectionAndReceivesAValidatedResult()
    {
        var png = Png(640, 480);
        CapturedRequest? captured = null;
        await using var server = await StructaDocTestServer.StartAsync(async context =>
        {
            captured = await CapturedRequest.ReadAsync(context);
            await StructaDocTestServer.WriteJsonAsync(context, HttpStatusCode.OK, """
                {"requestId":"req-1","width":640,"height":480,"coordinateFrame":"top-left-origin-normalized","regions":[{"id":0,"label":"text","x0":0.05,"y0":0.1,"x1":0.6,"y1":0.4},{"id":1,"label":"figure","x0":0.1,"y0":0.5,"x1":0.9,"y1":0.95}],"orientation":null,"engine":"mineru-cloud","model":"mineru2","version":"1.0","diagnostics":[]}
                """);
        });
        var client = server.CreateLayoutClient();

        var result = await client.DetectLayoutAsync(new LayoutDetectionImage(png, "image/png"));

        Assert.NotNull(captured);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/api/v1/layout/detect", captured.Path);
        Assert.Equal("ApiKey server-test-layout-key", captured.Authorization);
        Assert.Equal("file", captured.FileSectionName);
        Assert.Null(captured.OtherSectionNames.FirstOrDefault());
        Assert.Equal("image/png", captured.FileContentType);
        Assert.Equal(png, captured.FileBytes);
        Assert.Equal(2, result.Regions.Count);
        Assert.Equal("text", result.Regions[0].Label);
    }

    [Fact]
    public async Task PerImageBudgetGovernsTheExchangeAndExpiryClassifiesAsUpstreamTimeout()
    {
        await using var server = await StructaDocTestServer.StartAsync(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4), context.RequestAborted);
        });
        // A budget far below the server delay must cut the exchange even though the test
        // HttpClient keeps its 100-second default timeout: the linked token is the governor.
        var client = server.CreateLayoutClient(new StructaDocLayoutOptions
        {
            BaseUrl = server.BaseUrl,
            ApiKey = "server-test-layout-key",
            PerImageBudgetSeconds = 1,
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<LayoutDetectionException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage(Png(2, 2), "image/png")));
        stopwatch.Stop();

        Assert.Equal(LayoutDetectionFailure.UpstreamTimeout, failure.Failure);
        Assert.True(failure.IsTransient);
        // The exchange ended by the budget (1 s plus teardown), not by any 100 s default.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"budget-governed exchange took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task SlowAnswerWithinTheBudgetSucceeds()
    {
        await using var server = await StructaDocTestServer.StartAsync(async context =>
        {
            // Longer than nothing: proves an in-budget slow answer is accepted while the
            // budget clock runs, the shape the 120-second cloud deadline needs.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await StructaDocTestServer.WriteJsonAsync(context, HttpStatusCode.OK,
                """{"requestId":"req-2","width":2,"height":2,"coordinateFrame":"top-left-origin-normalized","regions":[]}""");
        });
        var client = server.CreateLayoutClient(new StructaDocLayoutOptions
        {
            BaseUrl = server.BaseUrl,
            ApiKey = "server-test-layout-key",
            PerImageBudgetSeconds = 30,
        });

        var result = await client.DetectLayoutAsync(new LayoutDetectionImage(Png(2, 2), "image/png"));
        Assert.Empty(result.Regions);
    }

    [Fact]
    public async Task ConcurrencyIsBoundedAcrossParallelExchanges()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        await using var server = await StructaDocTestServer.StartAsync(async context =>
        {
            var current = Interlocked.Increment(ref inFlight);
            // Track the high-water mark under a lock-free compare loop.
            int observed, computed;
            do
            {
                observed = Volatile.Read(ref maxInFlight);
                computed = Math.Max(observed, current);
            } while (Interlocked.CompareExchange(ref maxInFlight, computed, observed) != observed);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                await StructaDocTestServer.WriteJsonAsync(context, HttpStatusCode.OK,
                    """{"requestId":"r","width":2,"height":2,"coordinateFrame":"top-left-origin-normalized","regions":[]}""");
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });
        var client = server.CreateLayoutClient(new StructaDocLayoutOptions
        {
            BaseUrl = server.BaseUrl,
            ApiKey = "server-test-layout-key",
            MaxConcurrentExchanges = 2,
        });

        var exchanges = Enumerable.Range(0, 4).Select(_ =>
            client.DetectLayoutAsync(new LayoutDetectionImage(Png(2, 2), "image/png"))).ToArray();
        var results = await Task.WhenAll(exchanges);

        Assert.All(results, result => Assert.Empty(result.Regions));
        Assert.InRange(maxInFlight, 1, 2);
    }

    [Fact]
    public async Task StreamedOversizedAnswerIsRejectedMidBody()
    {
        await using var server = await StructaDocTestServer.StartAsync(async context =>
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            // No Content-Length: the bound must hold while streaming.
            var chunk = new string('x', 1024);
            await context.Response.WriteAsync("[");
            for (var i = 0; i < 64; i++) await context.Response.WriteAsync(chunk);
            await context.Response.WriteAsync("]");
        });
        var client = server.CreateLayoutClient(new StructaDocLayoutOptions
        {
            BaseUrl = server.BaseUrl,
            ApiKey = "server-test-layout-key",
            MaxResponseBytes = 4096,
        });

        var failure = await Assert.ThrowsAsync<LayoutDetectionException>(
            () => client.DetectLayoutAsync(new LayoutDetectionImage(Png(2, 2), "image/png")));
        Assert.Equal(LayoutDetectionFailure.MalformedResponse, failure.Failure);
        Assert.Contains("response bound", failure.Message);
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        string? Authorization,
        string? FileSectionName,
        string? FileContentType,
        byte[] FileBytes,
        IReadOnlyList<string> OtherSectionNames)
    {
        public static async Task<CapturedRequest> ReadAsync(HttpContext context)
        {
            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            using var raw = new MemoryStream();
            await context.Request.Body.CopyToAsync(raw, context.RequestAborted);
            raw.Position = 0;
            var boundary = BoundaryOf(context.Request.ContentType)
                ?? throw new InvalidOperationException("The request carries no multipart boundary.");
            var reader = new MultipartReader(boundary, raw)
            {
                BodyLengthLimit = LayoutImageInspection.MaximumImageBytes + 1024 * 1024,
            };
            string? fileSectionName = null;
            string? fileContentType = null;
            byte[] fileBytes = [];
            var otherSections = new List<string>();
            while (await reader.ReadNextSectionAsync(context.RequestAborted) is { } section)
            {
                // .NET emits unquoted token names (name=file) and RFC 5987 filenames
                // (filename*=utf-8''image); parse both spellings.
                var name = SectionName(section.ContentDisposition);
                var isFile = section.ContentDisposition?.Contains("filename", StringComparison.OrdinalIgnoreCase) == true;
                if (isFile && fileSectionName is null)
                {
                    fileSectionName = name;
                    fileContentType = section.ContentType;
                    using var buffer = new MemoryStream();
                    await section.Body.CopyToAsync(buffer, context.RequestAborted);
                    fileBytes = buffer.ToArray();
                }
                else
                {
                    otherSections.Add(name ?? "(unnamed)");
                }
            }
            return new CapturedRequest(
                context.Request.Method, context.Request.Path.Value ?? "", authorization,
                fileSectionName, fileContentType, fileBytes, otherSections);

            static string? SectionName(string? contentDisposition)
            {
                if (contentDisposition is null) return null;
                foreach (var part in contentDisposition.Split(';'))
                {
                    var trimmed = part.Trim();
                    if (!trimmed.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = trimmed["name=".Length..].Trim();
                    return value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
                        ? value[1..^1]
                        : value;
                }
                return null;
            }

            static string? BoundaryOf(string? contentType)
            {
                if (contentType is null) return null;
                foreach (var part in contentType.Split(';'))
                {
                    var trimmed = part.Trim();
                    if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = trimmed["boundary=".Length..].Trim();
                    return value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
                        ? value[1..^1]
                        : value;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// A minimal loopback StructaDoc double on an ephemeral port. It is not the real
    /// service: it exists to exchange real multipart HTTP with the client under test.
    /// </summary>
    private sealed class StructaDocTestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StructaDocTestServer(WebApplication app) => _app = app;

        public string BaseUrl { get; private set; } = "";

        public static async Task<StructaDocTestServer> StartAsync(
            Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.MapPost("/api/v1/layout/detect", handler);
            await app.StartAsync();
            var server = new StructaDocTestServer(app)
            {
                BaseUrl = app.Urls.Single(url => url.StartsWith("http://", StringComparison.Ordinal)),
            };
            return server;
        }

        public StructaDocLayoutClient CreateLayoutClient(StructaDocLayoutOptions? options = null)
        {
            return new StructaDocLayoutClient(
                new HttpClient { BaseAddress = new Uri(BaseUrl) },
                new LayoutCapabilityOptions(true),
                options ?? new StructaDocLayoutOptions
                {
                    BaseUrl = BaseUrl,
                    ApiKey = "server-test-layout-key",
                },
                NullLogger<StructaDocLayoutClient>.Instance);
        }

        public static Task WriteJsonAsync(HttpContext context, HttpStatusCode status, string body)
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(body);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
