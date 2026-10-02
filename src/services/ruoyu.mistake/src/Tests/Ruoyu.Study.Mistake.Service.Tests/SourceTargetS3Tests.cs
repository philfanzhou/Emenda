using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Minio;
using Minio.DataModel.Args;
using Moq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Host.Services.Intake;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceTargetS3Tests
{
    [Theory]
    [InlineData("automatic")][InlineData("manual-missing")][InlineData("manual-groups")]
    public async Task ActualS3ThreeEntriesWorkerOrAdminResumeAndAuthorizedDownloadAsync(string entry)
    {
        var endpoint=Environment.GetEnvironmentVariable("MISTAKE_TARGET_TEST_S3_ENDPOINT");
        if(string.IsNullOrWhiteSpace(endpoint)) throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_TARGET_TEST_S3_ENDPOINT to an owned S3 instance.");
        var bucket="target-"+Guid.NewGuid().ToString("N");
        using var minio=new MinioClient().WithEndpoint(endpoint).WithCredentials("targetfixture","targetfixture2026").Build();
        var oss=new S3OssService(new OssOptions{InternalEndpoint=endpoint,AccessKey="targetfixture",SecretKey="targetfixture2026",BucketName=bucket,PublicBaseUrl="http://"+endpoint},["uploads/","mistakes/"]);
        await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
        try
        {
            await using var f=await SourceIntakeFixture.CreateAsync();f.TargetsEnabled=true;
            var record=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg"]);
            foreach(var image in record.ImagePaths)
            {
                using var data=await f.Source.Files.DownloadAsync(image.Path);
                await minio.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(image.Path).WithStreamData(data).WithObjectSize(data.Length).WithContentType("image/jpeg"));
            }
            f.Oss.Setup(o=>o.DownloadAsync(It.IsAny<string>())).Returns((string p)=>oss.DownloadAsync(p));
            f.Oss.Setup(o=>o.GetPresignedUrlAsync(It.IsAny<string>(),It.IsAny<int>())).Returns((string p,int e)=>oss.GetPresignedUrlAsync(p,e));
            f.Copy=(a,b)=>oss.CopyObjectAsync(a,b);await f.StartAsync();
            if(entry=="automatic") await f.Service().CreateAutomaticAsync(record.Id,record.ContentRevision);
            else
            {
                var path=entry=="manual-missing" ? "/api/mistakes/upload" : "/api/mistakes";
                using var created=await f.SendAsync(HttpMethod.Post,path,new{studentId=record.StudentId,sourceUploadId=record.Id,
                    expectedContentRevision=record.ContentRevision,requestKey=Guid.NewGuid(),subject=1,grade=7,
                    imagePaths=new[]{"uploads/a.jpg","uploads/b.jpg"},sourceRegions=new[]{new{sourceImagePath="uploads/a.jpg"},new{sourceImagePath="uploads/b.jpg"}}},f.Token());
                Assert.Equal(HttpStatusCode.OK,created.StatusCode);
            }
            var before=await f.Service().GetAsync(record.Id,record.ContentRevision);Assert.False(before!.TargetReady);
            if(entry=="automatic")
            {
                var worker=new SourceTargetWorker(f.App!.Services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),NullLogger<SourceTargetWorker>.Instance);
                await worker.StartAsync(CancellationToken.None);
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(!(await f.Service().GetAsync(record.Id,record.ContentRevision))!.TargetReady && DateTime.UtcNow<deadline)await Task.Delay(100);
                await worker.StopAsync(CancellationToken.None);worker.Dispose();
            }
            else
            {
                using var resumed=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/targets",token:f.Token());Assert.Equal(HttpStatusCode.OK,resumed.StatusCode);
            }
            var ready=await f.Service().GetAsync(record.Id,record.ContentRevision);Assert.True(ready!.TargetReady);Assert.False(ready.HandoffCompleted);
            foreach(var target in ready.Targets!)
            {
                using var signed=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=target.TargetKey,size="full"},f.Token("user"));Assert.Equal(HttpStatusCode.OK,signed.StatusCode);
                using var json=JsonDocument.Parse(await signed.Content.ReadAsStringAsync());using var http=new HttpClient(new HttpClientHandler{UseProxy=false});
                var actual=await http.GetByteArrayAsync(json.RootElement.GetProperty("data").GetProperty("url").GetString()!);
                using var original=await oss.DownloadAsync(target.SourcePath);using var memory=new MemoryStream();await original.CopyToAsync(memory);
                Assert.Equal(memory.ToArray(),actual);
            }
            var replay=await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);Assert.Equal(ready.Targets!.Select(t=>t.TargetKey),replay.Targets!.Select(t=>t.TargetKey));
            Assert.Equal(2,f.SideEffects.Count(x=>x=="copy"));Assert.DoesNotContain("delete",f.SideEffects);
            await using var db=f.Context();Assert.Equal(2,await db.SourceTargetObjects.CountAsync());Assert.Equal(2,await db.SourceTargetAttempts.CountAsync());
        }
        finally
        {
            foreach(var item in await oss.ListObjectsAsync())await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(item.ObjectPath));
            await minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
        }
    }
}
