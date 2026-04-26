using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel.Args;

namespace Ruoyu.Study.Mistake.Components.Oss;

public enum OssBucket
{
    Mistakes,
}

public interface IOssService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Mistakes);
    Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Mistakes);
    Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Mistakes);
    Task<Stream> DownloadAsync(string objectPath);
    Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths);
    Task<bool> DeleteAsync(string objectPath);
    Task<int> DeleteManyAsync(IEnumerable<string> objectPaths);
    Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600);
    Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600);
    Task<bool> CheckConnectivityAsync();
}

public class MinioOssService : IOssService
{
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName;

    public MinioOssService(string endpoint, string accessKey, string secretKey, string bucketName)
    {
        _bucketName = bucketName;

        _minioClient = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .Build();
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Mistakes)
    {
        var objectPath = GenerateObjectPath(fileName, bucket);

        await EnsureBucketExistsAsync();

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType(contentType);

        await _minioClient.PutObjectAsync(putObjectArgs);

        return objectPath;
    }

    public async Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Mistakes)
    {
        using var stream = new MemoryStream(data);
        return await UploadAsync(stream, fileName, contentType, bucket);
    }

    public async Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Mistakes)
    {
        await EnsureBucketExistsAsync();

        var paths = new List<string>();
        var index = 0;

        foreach (var (stream, fileName, contentType) in files)
        {
            var objectPath = GenerateObjectPath($"{index}_{fileName}", bucket);

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs);
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

        await _minioClient.GetObjectAsync(getObjectArgs);

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

    public async Task<bool> DeleteAsync(string objectPath)
    {
        try
        {
            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            await _minioClient.RemoveObjectAsync(removeObjectArgs);
            return true;
        }
        catch
        {
            return false;
        }
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

        return await _minioClient.PresignedGetObjectAsync(presignedGetObjectArgs);
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
            return await _minioClient.BucketExistsAsync(beArgs);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureBucketExistsAsync()
    {
        var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
        if (!await _minioClient.BucketExistsAsync(beArgs))
        {
            var mbArgs = new MakeBucketArgs().WithBucket(_bucketName);
            await _minioClient.MakeBucketAsync(mbArgs);
        }
    }

    private static string GenerateObjectPath(string fileName, OssBucket bucket)
    {
        var now = DateTime.UtcNow;
        var prefix = bucket switch
        {
            OssBucket.Mistakes => "mistakes",
            _ => "others"
        };

        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}

public class OssOptions
{
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public string BucketName { get; set; } = "ruoyu-study";
    public bool IsSecure { get; set; } = false;
}
