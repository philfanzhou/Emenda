using System.Net;
using System.Text;
using Emenda.Common.Oss;
using Xunit.Abstractions;

namespace Emenda.Service.Tests;

public sealed class S3ObjectListingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListingFailureNeverReturnsAnEmptyOrPartialInventory(bool failAfterFirstPage)
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var jobId = Guid.NewGuid();
        var prefix = $"mistakes/extractions/{jobId:N}/";
        var key = prefix + $"originals/{Guid.NewGuid():N}.png";
        var listingRequests = 0;
        var versionedRequests = 0;
        var prefixes = new List<string?>();
        var markers = new List<(string? Key, string? Version)>();
        var wire = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    if (wire.Count < 20) wire.Add(CaptureRequest(context.Request));
                    string body;
                    if (HasSubresource(context.Request, "location"))
                        body = "<LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">us-east-1</LocationConstraint>";
                    else
                    {
                        Interlocked.Increment(ref listingRequests);
                        if (HasSubresource(context.Request, "versions")) Interlocked.Increment(ref versionedRequests);
                        prefixes.Add(context.Request.QueryString["prefix"]);
                        markers.Add((context.Request.QueryString["key-marker"], context.Request.QueryString["version-id-marker"]));
                        if (listingRequests == 1 && failAfterFirstPage)
                            body = $"<ListVersionsResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-bucket</Name><Prefix>{prefix}</Prefix><MaxKeys>1000</MaxKeys><IsTruncated>true</IsTruncated><NextKeyMarker>{key}</NextKeyMarker><NextVersionIdMarker>old</NextVersionIdMarker><Version><Key>{key}</Key><VersionId>old</VersionId><IsLatest>false</IsLatest><LastModified>2026-11-22T00:00:00Z</LastModified><ETag>fixture</ETag><Size>4</Size><StorageClass>STANDARD</StorageClass></Version></ListVersionsResult>";
                        else
                        {
                            context.Response.StatusCode = 403;
                            body = "<Error><Code>AccessDenied</Code><Message>injected listing failure</Message><Resource>/test-bucket</Resource><RequestId>synthetic</RequestId></Error>";
                        }
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/xml";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var service = new S3OssService(new() { InternalEndpoint = $"127.0.0.1:{port}", BucketName = "test-bucket",
                AccessKey = "synthetic-access", SecretKey = "synthetic-secret" });

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => service.ListExtractionObjectsAsync(jobId).WaitAsync(stop.Token));
            output.WriteLine($"{failure.GetType().Name}: {failure.Message}");

            Assert.False(failure is OperationCanceledException, failure.ToString());
            Assert.Equal(failAfterFirstPage ? 2 : 1, listingRequests);
            Assert.Equal(listingRequests, versionedRequests);
            Assert.All(prefixes, value => Assert.Equal(prefix, value));
            if (failAfterFirstPage) Assert.Equal((key, "old"), markers[1]);
        }
        finally { stop.Cancel(); await server; listener.Stop(); output.WriteLine(string.Join(Environment.NewLine, wire)); }
    }

    [Fact]
    public async Task ExtractionInventoryFindsAndPurgesLegacyContentHiddenByDeleteMarkers()
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var jobId = Guid.NewGuid();
        var prefix = $"mistakes/extractions/{jobId:N}/";
        var key = prefix + $"versions/{Guid.NewGuid():N}/{Guid.NewGuid():N}/0-{new string('a', 16)}.png";
        var versions = new HashSet<string> { "old", "older", "marker" };
        var listedPrefixes = new List<string?>();
        var wire = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    var request = context.Request;
                    if (wire.Count < 20) wire.Add(CaptureRequest(request));
                    string body;
                    if (HasSubresource(request, "location"))
                        body = "<LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">us-east-1</LocationConstraint>";
                    else if (request.HttpMethod == "DELETE")
                    {
                        versions.Remove(request.QueryString["versionId"]!);
                        context.Response.StatusCode = 204;
                        body = "";
                    }
                    else if (HasSubresource(request, "versions"))
                    {
                        var requestedPrefix = request.QueryString["prefix"];
                        listedPrefixes.Add(requestedPrefix);
                        body = "<ListVersionsResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-bucket</Name><Prefix>" + requestedPrefix + "</Prefix><IsTruncated>false</IsTruncated>"
                            + (requestedPrefix == prefix || requestedPrefix == key ? string.Concat(versions.Select(version => version == "marker"
                                ? $"<DeleteMarker><Key>{key}</Key><VersionId>{version}</VersionId><IsLatest>true</IsLatest><LastModified>2026-11-22T00:00:00Z</LastModified></DeleteMarker>"
                                : $"<Version><Key>{key}</Key><VersionId>{version}</VersionId><IsLatest>false</IsLatest><LastModified>2026-11-22T00:00:00Z</LastModified><ETag>fixture</ETag><Size>4</Size><StorageClass>STANDARD</StorageClass></Version>")) : "")
                            + "</ListVersionsResult>";
                    }
                    else
                        body = $"<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-bucket</Name><Prefix>{prefix}</Prefix><KeyCount>0</KeyCount><IsTruncated>false</IsTruncated></ListBucketResult>";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/xml";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var service = new S3OssService(new() { InternalEndpoint = $"127.0.0.1:{port}", BucketName = "test-bucket",
                AccessKey = "synthetic-access", SecretKey = "synthetic-secret" });
            Assert.Empty(await service.ListObjectsAsync(prefix).WaitAsync(stop.Token));

            var inventory = await service.ListExtractionObjectsAsync(jobId).WaitAsync(stop.Token);

            Assert.Equal(key, Assert.Single(inventory).ObjectPath);
            Assert.True(await service.DeleteDerivedAsync(key).WaitAsync(stop.Token));
            Assert.Empty(versions);
            Assert.Empty(await service.ListExtractionObjectsAsync(jobId).WaitAsync(stop.Token));
            Assert.Contains(prefix, listedPrefixes);
            Assert.All(listedPrefixes, value => Assert.StartsWith(prefix, value));
        }
        finally { stop.Cancel(); await server; listener.Stop(); output.WriteLine(string.Join(Environment.NewLine, wire)); }
    }

    [Theory]
    [InlineData("", "us-east-1")]
    [InlineData("EU", "eu-west-1")]
    [InlineData("ap-southeast-1", "ap-southeast-1")]
    public async Task VersionListingUsesBucketRegionAndRetriesFailedRegionDiscovery(string location, string signingRegion)
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var lookups = 0;
        var listingSignatures = new List<string?>();
        var wire = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    if (wire.Count < 20) wire.Add(CaptureRequest(context.Request));
                    string body;
                    if (HasSubresource(context.Request, "location"))
                    {
                        if (++lookups == 1)
                        {
                            context.Response.StatusCode = 403;
                            body = "<Error><Code>AccessDenied</Code><Message>injected location failure</Message></Error>";
                        }
                        else body = $"<LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">{location}</LocationConstraint>";
                    }
                    else
                    {
                        listingSignatures.Add(context.Request.Headers["Authorization"]);
                        body = "<ListVersionsResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-bucket</Name><IsTruncated>false</IsTruncated></ListVersionsResult>";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/xml";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var service = new S3OssService(new() { InternalEndpoint = $"127.0.0.1:{port}", BucketName = "test-bucket",
                AccessKey = "synthetic-access", SecretKey = "synthetic-secret" });
            await Assert.ThrowsAnyAsync<Exception>(() => service.ListExtractionObjectsAsync(Guid.NewGuid()).WaitAsync(stop.Token));

            Assert.Empty(await service.ListExtractionObjectsAsync(Guid.NewGuid()).WaitAsync(stop.Token));
            Assert.Empty(await service.ListExtractionObjectsAsync(Guid.NewGuid()).WaitAsync(stop.Token));

            Assert.Equal(2, lookups);
            Assert.Equal(2, listingSignatures.Count);
            Assert.All(listingSignatures, signature => Assert.Contains($"/{signingRegion}/s3/aws4_request", signature));
        }
        finally { stop.Cancel(); await server; listener.Stop(); output.WriteLine(string.Join(Environment.NewLine, wire)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionListingContinuesWithOnlyAKeyMarker(bool emptyVersionMarker)
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var jobId = Guid.NewGuid();
        var prefix = $"mistakes/extractions/{jobId:N}/";
        var keys = new[] { prefix + $"originals/{Guid.NewGuid():N}.png", prefix + $"originals/{Guid.NewGuid():N}.png" };
        var cursors = new List<(string? Key, string? Version)>();
        var wire = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    if (wire.Count < 20) wire.Add(CaptureRequest(context.Request));
                    string body;
                    if (HasSubresource(context.Request, "location"))
                        body = "<LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"/>";
                    else
                    {
                        cursors.Add((context.Request.QueryString["key-marker"], context.Request.QueryString["version-id-marker"]));
                        var first = cursors.Count == 1;
                        var key = keys[first ? 0 : 1];
                        var next = first ? $"<NextKeyMarker>{key}</NextKeyMarker>" + (emptyVersionMarker ? "<NextVersionIdMarker/>" : "") : "";
                        body = $"<ListVersionsResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-bucket</Name><Prefix>{prefix}</Prefix><IsTruncated>{(first ? "true" : "false")}</IsTruncated>{next}<Version><Key>{key}</Key><VersionId>null</VersionId><IsLatest>true</IsLatest><LastModified>2026-11-22T00:00:00Z</LastModified><ETag>fixture</ETag><Size>4</Size><StorageClass>STANDARD</StorageClass></Version></ListVersionsResult>";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/xml";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var service = new S3OssService(new() { InternalEndpoint = $"127.0.0.1:{port}", BucketName = "test-bucket",
                AccessKey = "synthetic-access", SecretKey = "synthetic-secret" });

            var inventory = await service.ListExtractionObjectsAsync(jobId).WaitAsync(stop.Token);

            Assert.Equal(keys, inventory.Select(item => item.ObjectPath));
            Assert.Equal(2, cursors.Count);
            Assert.Equal(keys[0], cursors[1].Key);
            Assert.Null(cursors[1].Version);
            Assert.All(wire, request => Assert.DoesNotContain("version-id-marker", request));
        }
        finally { stop.Cancel(); await server; listener.Stop(); output.WriteLine(string.Join(Environment.NewLine, wire)); }
    }

    private static bool HasSubresource(HttpListenerRequest request, string name) =>
        request.QueryString.AllKeys.Contains(name) || request.QueryString.GetValues(null)?.Contains(name, StringComparer.Ordinal) == true;

    private static string CaptureRequest(HttpListenerRequest request)
    {
        var credential = request.Headers["Authorization"]?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("Credential=", StringComparison.Ordinal));
        var region = credential?.Split('/').ElementAtOrDefault(2) ?? "none";
        var query = string.Join(";", request.QueryString.AllKeys.Select(key => $"{key ?? "<null>"}={request.QueryString[key]}"));
        return $"{request.HttpMethod} {request.RawUrl}; query [{query}]; signing-region {region}";
    }
}
