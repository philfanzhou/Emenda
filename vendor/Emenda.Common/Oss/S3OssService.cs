using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Minio;
using Minio.DataModel.Args;

namespace Emenda.Common.Oss;

public enum OssBucket
{
    Uploads,      // uploads/
    Mistakes,     // mistakes/
    Questions,    // questions/
    Documents,    // documents/
}

public interface IOssService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null);
    Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null);
    Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null);
    Task<Stream> DownloadAsync(string objectPath);
    Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths);
    Task CopyObjectAsync(string sourcePath, string destPath);
    Task<bool> ObjectExistsAsync(string objectPath);
    // Used for lifecycle decisions. Storage errors must reach the caller for retry.
    Task<bool> ObjectExistsOrThrowAsync(string objectPath, CancellationToken ct) => ObjectExistsAsync(objectPath).WaitAsync(ct);
    Task<bool> DeleteAsync(string objectPath);
    // Idempotent cleanup: missing objects are successful; storage failures remain failures.
    Task<bool> DeleteIfExistsAsync(string objectPath) => DeleteAsync(objectPath);
    Task<bool> DeleteDerivedAsync(string objectPath) => DeleteIfExistsAsync(objectPath);
    Task<bool> DeleteExtractionOriginalAsync(string objectPath) => Task.FromResult(false);
    Task<int> DeleteManyAsync(IEnumerable<string> objectPaths);
    Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600);
    Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600);
    Task<bool> CheckConnectivityAsync();
    Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null);
    // Extraction cleanup needs every historical version, including keys hidden by delete markers; failures propagate.
    Task<List<OssObjectInfo>> ListExtractionObjectsAsync(Guid jobId);
    Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null);
}

public class OssObjectInfo
{
    public string ObjectPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool IsZombie { get; set; }
}

public class S3OssService : IOssService, IDisposable
{
    private readonly IMinioClient _internalClient;
    private readonly IMinioClient _presignClient;
    private AmazonS3Client? _metadataClient;
    private readonly SemaphoreSlim _metadataClientLock = new(1, 1);
    private readonly string _bucketName;
    private readonly PathPrefixValidator? _prefixValidator;
    private readonly Uri? _publicBaseUri;

    public S3OssService(OssOptions options, string[]? allowedPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bucketName = options.BucketName;
        _prefixValidator = allowedPrefixes != null ? new PathPrefixValidator(allowedPrefixes) : null;
        _internalClient = BuildClient(
            options.InternalEndpoint,
            options.AccessKey,
            options.SecretKey,
            options.InternalSecure);

        _publicBaseUri = ParsePublicBaseUri(options.PublicBaseUrl);
        _presignClient = _publicBaseUri == null
            ? _internalClient
            : BuildClient(
                _publicBaseUri.Authority,
                options.AccessKey,
                options.SecretKey,
                string.Equals(_publicBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _metadataClient?.Dispose();
        _metadataClientLock.Dispose();
        if (!ReferenceEquals(_presignClient, _internalClient)) _presignClient.Dispose();
        _internalClient.Dispose();
    }

    private static IMinioClient BuildClient(string endpoint, string accessKey, string secretKey, bool secure)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("The S3 endpoint is required.", nameof(endpoint));
        }

        return new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(secure)
            .Build();
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        var objectPath = GenerateObjectPath(fileName, bucket, folder);
        _prefixValidator?.ValidateWritePath(objectPath);

        await EnsureBucketExistsAsync();

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType(contentType);

        await _internalClient.PutObjectAsync(putObjectArgs);

        return objectPath;
    }

    public async Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        using var stream = new MemoryStream(data);
        return await UploadAsync(stream, fileName, contentType, bucket, folder);
    }

    public async Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        await EnsureBucketExistsAsync();

        var paths = new List<string>();
        var index = 0;

        foreach (var (stream, fileName, contentType) in files)
        {
            var objectPath = GenerateObjectPath($"{index}_{fileName}", bucket, folder);
            _prefixValidator?.ValidateWritePath(objectPath);

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _internalClient.PutObjectAsync(putObjectArgs);
            paths.Add(objectPath);
            index++;
        }

        return paths;
    }

    public async Task<Stream> DownloadAsync(string objectPath)
    {
        var memoryStream = new MemoryStream();

        var getObjectArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithCallbackStream(stream =>
            {
                stream.CopyTo(memoryStream);
                memoryStream.Position = 0;
            });

        await _internalClient.GetObjectAsync(getObjectArgs);

        return memoryStream;
    }

    public async Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths)
    {
        var streams = new List<Stream>();
        foreach (var path in objectPaths)
        {
            streams.Add(await DownloadAsync(path));
        }
        return streams;
    }

    public async Task CopyObjectAsync(string sourcePath, string destPath)
    {
        _prefixValidator?.ValidateWritePath(destPath);
        using var stream = await DownloadAsync(sourcePath);
        await EnsureBucketExistsAsync();

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(destPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType("image/jpeg");

        await _internalClient.PutObjectAsync(putObjectArgs);
    }

    public async Task<bool> ObjectExistsAsync(string objectPath)
    {
        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            var stat = await _internalClient.StatObjectAsync(statArgs);
            return stat != null && stat.Size > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ObjectExistsOrThrowAsync(string objectPath, CancellationToken ct)
    {
        var client = await MetadataClientAsync(ct);
        try
        {
            var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = _bucketName, Key = objectPath }, ct);
            return metadata.ContentLength > 0;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A bodyless HEAD 404 can also mean the bucket disappeared. Only retire work for a missing key.
            await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = _bucketName }, ct);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string objectPath)
    {
        try
        {
            _prefixValidator?.ValidateWritePath(objectPath);

            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            await _internalClient.RemoveObjectAsync(removeObjectArgs);

            // 原图删除后再删关联缩略图：并发生成者上传后会复查原图，二者不会留下孤儿缩略图。
            if (!ThumbnailHelper.IsThumbnailPath(objectPath))
            {
                foreach (var thumbPath in ThumbnailHelper.GetAllThumbnailPaths(objectPath))
                {
                    try
                    {
                        var removeThumbArgs = new RemoveObjectArgs()
                            .WithBucket(_bucketName)
                            .WithObject(thumbPath);
                        await _internalClient.RemoveObjectAsync(removeThumbArgs);
                    }
                    catch
                    {
                        // 缩略图不存在时忽略
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteDerivedAsync(string objectPath)
    {
        if (!objectPath.StartsWith("mistakes/extractions/", StringComparison.Ordinal)
            || !(objectPath.Contains("/versions/", StringComparison.Ordinal) || objectPath.Contains("/supplements/", StringComparison.Ordinal)))
            return false;
        return await PurgeExtractionObjectAsync(objectPath);
    }

    public Task<bool> DeleteExtractionOriginalAsync(string objectPath)
    {
        var segments = objectPath.Split('/');
        if (segments.Length != 5 || segments[0] != "mistakes" || segments[1] != "extractions" ||
            segments[2].Length != 32 || !Guid.TryParseExact(segments[2], "N", out _) || segments[3] != "originals" ||
            segments[4].Length != 36 ||
            !(segments[4].EndsWith(".png", StringComparison.Ordinal) || segments[4].EndsWith(".jpg", StringComparison.Ordinal)) ||
            !Guid.TryParseExact(segments[4][..^4], "N", out _))
            return Task.FromResult(false);
        return PurgeExtractionObjectAsync(objectPath);
    }

    private async Task<bool> PurgeExtractionObjectAsync(string objectPath)
    {
        try
        {
            _prefixValidator?.ValidateWritePath(objectPath);
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var path in ThumbnailHelper.GetAllThumbnailPaths(objectPath).Prepend(objectPath))
            {
                var versions = new List<string?>();
                await foreach (var item in ListObjectVersionsAsync(path, timeout.Token))
                {
                    if (item.Key != path) continue;
                    if (versions.Count >= 10000) return false;
                    versions.Add(item.VersionId);
                }
                foreach (var version in versions)
                {
                    var remove = new RemoveObjectArgs().WithBucket(_bucketName).WithObject(path);
                    if (version is not null) remove.WithVersionId(version);
                    await _internalClient.RemoveObjectAsync(remove, timeout.Token);
                }
                // Hiding bytes behind a delete marker is insufficient: every data version must be absent.
                await foreach (var item in ListObjectVersionsAsync(path, timeout.Token))
                    if (item.Key == path) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public async Task<int> DeleteManyAsync(IEnumerable<string> objectPaths)
    {
        var count = 0;
        foreach (var path in objectPaths)
        {
            if (await DeleteAsync(path))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600)
    {
        var presignedGetObjectArgs = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithExpiry(expirySeconds);

        var url = await _presignClient.PresignedGetObjectAsync(presignedGetObjectArgs);

        if (_publicBaseUri != null && !string.IsNullOrEmpty(url))
        {
            url = PresignedUrlPathPrefixer.AddPrefix(url, _publicBaseUri);
        }

        return url;
    }

    public async Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600)
    {
        var urls = new List<string>();
        foreach (var p in objectPaths)
        {
            urls.Add(await GetPresignedUrlAsync(p, expirySeconds));
        }
        return urls;
    }

    public async Task<bool> CheckConnectivityAsync()
    {
        try
        {
            var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
            return await _internalClient.BucketExistsAsync(beArgs);
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null)
    {
        var objects = new List<OssObjectInfo>();

        try
        {
            var listArgs = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix(prefix)
                .WithRecursive(true);

            await foreach (var item in _internalClient.ListObjectsEnumAsync(listArgs))
            {
                objects.Add(new OssObjectInfo
                {
                    ObjectPath = item.Key,
                    Size = (long)item.Size,
                    LastModified = string.IsNullOrEmpty(item.LastModified) ? null : DateTimeOffset.TryParse(item.LastModified, out var dt) ? (DateTimeOffset?)dt : null,
                    IsZombie = false
                });
            }
        }
        catch
        {
            // 忽略错误，返回空列表
        }

        return objects;
    }

    public async Task<List<OssObjectInfo>> ListExtractionObjectsAsync(Guid jobId)
    {
        var prefix = $"mistakes/extractions/{jobId:N}/";
        var objects = new List<OssObjectInfo>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var item in ListObjectVersionsAsync(prefix, timeout.Token))
        {
            if (!item.Key.StartsWith(prefix, StringComparison.Ordinal) || !keys.Add(item.Key)) continue;
            objects.Add(new OssObjectInfo
            {
                ObjectPath = item.Key,
                Size = item.Size ?? 0,
                LastModified = item.LastModified is { } modified ? new DateTimeOffset(modified) : null,
                IsZombie = false
            });
        }
        return objects;
    }

    private AmazonS3Client CreateMetadataClient(string region) =>
        new(new BasicAWSCredentials(_internalClient.Config.AccessKey, _internalClient.Config.SecretKey), new AmazonS3Config
        {
            ServiceURL = _internalClient.Config.Endpoint,
            AuthenticationRegion = region,
            ForcePathStyle = true
        });

    private async Task<AmazonS3Client> MetadataClientAsync(CancellationToken ct)
    {
        var client = Volatile.Read(ref _metadataClient);
        if (client is not null) return client;
        await _metadataClientLock.WaitAsync(ct);
        try
        {
            if (_metadataClient is not null) return _metadataClient;
            // Preserve Minio's bucket-location discovery, including its empty/EU location defaults.
            using var lookup = CreateMetadataClient("us-east-1");
            var location = await lookup.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = _bucketName }, ct);
            var region = location.Location?.Value;
            region = string.IsNullOrEmpty(region) ? "us-east-1" : region == "EU" ? "eu-west-1" : region;
            client = CreateMetadataClient(region);
            Volatile.Write(ref _metadataClient, client);
            return client;
        }
        finally { _metadataClientLock.Release(); }
    }

    private async IAsyncEnumerable<S3ObjectVersion> ListObjectVersionsAsync(string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Minio 7's iterator skips S3 version markers and delete markers, so use the maintained page API here.
        var client = await MetadataClientAsync(ct);
        var request = new ListVersionsRequest { BucketName = _bucketName, Prefix = prefix, MaxKeys = 1000 };
        HashSet<(string Key, string? Version)>? markers = null;
        while (true)
        {
            var page = await client.ListVersionsAsync(request, ct);
            if (page.Versions is not null)
                foreach (var version in page.Versions) yield return version;
            if (page.IsTruncated == false) yield break;
            if (page.IsTruncated != true || string.IsNullOrEmpty(page.NextKeyMarker))
                throw new IOException("Incomplete S3 version listing.");
            var versionMarker = string.IsNullOrEmpty(page.NextVersionIdMarker) ? null : page.NextVersionIdMarker;
            markers ??= [];
            if (!markers.Add((page.NextKeyMarker, versionMarker)))
                throw new IOException("S3 version listing did not advance.");
            request.KeyMarker = page.NextKeyMarker;
            request.VersionIdMarker = versionMarker;
        }
    }

    public async Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null)
    {
        var bucketPrefix = bucket switch
        {
            OssBucket.Uploads => "uploads",
            OssBucket.Mistakes => "mistakes",
            OssBucket.Questions => "questions",
            OssBucket.Documents => "documents",
            _ => "others"
        };

        var fullPrefix = string.IsNullOrWhiteSpace(prefix)
            ? bucketPrefix
            : $"{bucketPrefix}/{prefix}";

        return await ListObjectsAsync(fullPrefix);
    }

    private async Task EnsureBucketExistsAsync()
    {
        var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
        if (!await _internalClient.BucketExistsAsync(beArgs))
        {
            var mbArgs = new MakeBucketArgs().WithBucket(_bucketName);
            await _internalClient.MakeBucketAsync(mbArgs);
        }
    }

    private static Uri? ParsePublicBaseUri(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(publicBaseUrl.TrimEnd('/'), UriKind.Absolute, out var publicBaseUri) ||
            (publicBaseUri.Scheme != Uri.UriSchemeHttp && publicBaseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(publicBaseUri.Query) ||
            !string.IsNullOrEmpty(publicBaseUri.Fragment) ||
            !string.IsNullOrEmpty(publicBaseUri.UserInfo))
        {
            throw new ArgumentException(
                "Oss:PublicBaseUrl must be an absolute HTTP or HTTPS URL without query, fragment, or user information.",
                nameof(publicBaseUrl));
        }

        return publicBaseUri;
    }

    private static string GenerateObjectPath(string fileName, OssBucket bucket, string? folder = null)
    {
        var prefix = bucket switch
        {
            OssBucket.Uploads => "uploads",
            OssBucket.Mistakes => "mistakes",
            OssBucket.Questions => "questions",
            OssBucket.Documents => "documents",
            _ => "others"
        };

        if (!string.IsNullOrWhiteSpace(folder))
        {
            return $"{prefix}/{folder}/{fileName}";
        }

        var now = DateTime.UtcNow;
        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}

public class OssOptions
{
    public string InternalEndpoint { get; set; } = "localhost:8333";
    public bool InternalSecure { get; set; }
    public string AccessKey { get; set; } = "seaweedfs_admin";
    public string SecretKey { get; set; } = "seaweedfs_admin";
    public string BucketName { get; set; } = "ruoyu-study";
    public string? PublicBaseUrl { get; set; }
}
