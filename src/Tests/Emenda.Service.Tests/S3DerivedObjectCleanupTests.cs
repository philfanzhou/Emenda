using System.Net;
using System.Text;
using Emenda.Common.Oss;

namespace Emenda.Service.Tests;

public sealed class S3DerivedObjectCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgesEveryContentVersionRatherThanOnlyHidingThem_AndKeepsFailureRetryable(bool failDeletion)
    {
        var port = HostProcess.GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var key = "mistakes/extractions/11111111111141118111111111111111/versions/revision/crop.png";
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
            Assert.Equal(!failDeletion, await service.DeleteDerivedAsync(key).WaitAsync(stop.Token));
            Assert.Equal(failDeletion ? 2 : 0, versions.Count(v => v != "marker"));
            Assert.False(await service.DeleteDerivedAsync("mistakes/extractions/job/originals/keep.png"));
        }
        finally { stop.Cancel(); await server; listener.Stop(); }
    }
}
