using System.Net;
using System.Text;
using Emenda.Common.Oss;

namespace Emenda.Service.Tests;

public sealed class S3DerivedObjectCleanupTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(404, true)]
    public async Task VariantParentProbeDistinguishesMissingFromStorageFailure(int objectStatus, bool bucketDisappeared)
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            var locationQueries = 0;
            while (locationQueries < 2)
            {
                var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                if (context.Request.HttpMethod == "HEAD")
                {
                    context.Response.StatusCode = objectStatus;
                    if (objectStatus == 200) context.Response.ContentLength64 = 4;
                    context.Response.Close();
                    if (objectStatus != 404) return;
                    continue;
                }
                Assert.Contains("location", context.Request.Url!.Query);
                locationQueries++;
                if (locationQueries == 2 && bucketDisappeared)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                var location = Encoding.UTF8.GetBytes("<LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">us-east-1</LocationConstraint>");
                context.Response.ContentLength64 = location.Length;
                await context.Response.OutputStream.WriteAsync(location, stop.Token);
                context.Response.Close();
            }
        });
        using var service = new S3OssService(new() { InternalEndpoint = $"127.0.0.1:{port}", BucketName = "test-bucket",
            AccessKey = "synthetic-access", SecretKey = "synthetic-secret" });
        var probe = service.ObjectExistsOrThrowAsync("mistakes/legacy/test.png", stop.Token);
        if (bucketDisappeared || objectStatus == 403)
        {
            var error = await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => probe);
            Assert.Equal((HttpStatusCode)objectStatus, error.StatusCode);
        }
        else Assert.Equal(objectStatus == 200, await probe);
        await server;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PurgesEveryContentVersionRatherThanOnlyHidingThem_AndKeepsFailureRetryable(bool failDeletion, bool original)
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var key = original
            ? $"mistakes/extractions/{Guid.NewGuid():N}/originals/{Guid.NewGuid():N}.png"
            : "mistakes/extractions/synthetic-job/versions/synthetic-revision/crop.png";
        var versions = new HashSet<string> { "old", "current", "marker" };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    var request = context.Request;
                    string body;
                    if (request.QueryString.AllKeys.Contains("location")) body = "<LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">us-east-1</LocationConstraint>";
                    else if (request.HttpMethod == "DELETE")
                    {
                        if (failDeletion) { context.Response.StatusCode = 403; body = "<Error><Code>AccessDenied</Code><Message>denied</Message></Error>"; }
                        else { versions.Remove(request.QueryString["versionId"]!); context.Response.StatusCode = 204; body = ""; }
                    }
                    else
                    {
                        var prefix = request.QueryString["prefix"];
                        body = "<ListVersionsResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-bucket</Name><Prefix>" + prefix + "</Prefix><IsTruncated>false</IsTruncated>"
                            + (prefix == key ? string.Concat(versions.Select(v => v == "marker"
                                ? $"<DeleteMarker><Key>{key}</Key><VersionId>{v}</VersionId><IsLatest>true</IsLatest><LastModified>2026-11-22T00:00:00Z</LastModified></DeleteMarker>"
                                : $"<Version><Key>{key}</Key><VersionId>{v}</VersionId><IsLatest>false</IsLatest><LastModified>2026-11-22T00:00:00Z</LastModified><ETag>fixture</ETag><Size>4</Size><StorageClass>STANDARD</StorageClass></Version>")) : "") + "</ListVersionsResult>";
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
            var service = new S3OssService(new() { InternalEndpoint = $"127.0.0.1:{port}", BucketName = "test-bucket",
                AccessKey = "synthetic-access", SecretKey = "synthetic-secret" });
            Assert.Equal(!failDeletion, await (original ? service.DeleteExtractionOriginalAsync(key) : service.DeleteDerivedAsync(key)).WaitAsync(stop.Token));
            Assert.Equal(failDeletion ? 2 : 0, versions.Count(v => v != "marker"));
            Assert.False(await service.DeleteDerivedAsync("mistakes/extractions/job/originals/keep.png"));
            Assert.False(await service.DeleteExtractionOriginalAsync("mistakes/extractions/job/originals/keep.png"));
            if (original) Assert.False(await service.DeleteDerivedAsync(key));
        }
        finally { stop.Cancel(); await server; listener.Stop(); }
    }
}
