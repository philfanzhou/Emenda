using System.Net;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceReturnS3Tests
{
    [Theory]
    [InlineData("normal")][InlineData("partial")][InlineData("late")]
    public async Task ActualSeaweedCopyGetAndStudentHttpsMutablePublicationAsync(string fault)
    {
        var endpoint=Environment.GetEnvironmentVariable("MISTAKE_TARGET_TEST_S3_ENDPOINT");
        if(string.IsNullOrWhiteSpace(endpoint))throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_TARGET_TEST_S3_ENDPOINT to owned Seaweed S3.");
        var bucket="return-"+Guid.NewGuid().ToString("N");
        using var minio=new MinioClient().WithEndpoint(endpoint).WithCredentials("targetfixture","targetfixture2026").Build();
        var oss=new S3OssService(new OssOptions {InternalEndpoint=endpoint,AccessKey="targetfixture",SecretKey="targetfixture2026",BucketName=bucket,PublicBaseUrl="http://"+endpoint},["uploads/","mistakes/"]);
        await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
        try
        {
            await using var f=await SourceIntakeFixture.CreateAsync(sourceStrict:true);f.ReturnsEnabled=true;f.TargetsEnabled=true;
            var record=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg"]);
            foreach(var (entry,index) in record.ImagePaths.Select((entry,index)=>(entry,index)))
            {
                using var image=new Image<Rgba32>(4+index,6+index,new Rgba32((byte)(index*80),90,210));using var bytes=new MemoryStream();await image.SaveAsJpegAsync(bytes);bytes.Position=0;
                await minio.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(entry.Path).WithStreamData(bytes).WithObjectSize(bytes.Length).WithContentType("image/jpeg"));
            }
            f.Oss.Setup(o=>o.DownloadAsync(It.IsAny<string>())).Returns((string p)=>oss.DownloadAsync(p));f.Copy=(a,b)=>oss.CopyObjectAsync(a,b);
            f.Source.Oss.Setup(o=>o.DownloadAsync(It.IsAny<string>())).Returns((string p)=>oss.DownloadAsync(p));
            f.Source.Oss.Setup(o=>o.ObjectExistsAsync(It.IsAny<string>())).Returns((string p)=>oss.ObjectExistsAsync(p));
            f.Source.Oss.Setup(o=>o.UploadAsync(It.IsAny<byte[]>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<OssBucket>(),It.IsAny<string?>()))
                .Returns((byte[] bytes,string file,string content,OssBucket target,string? folder)=>oss.UploadAsync(bytes,file,content,target,folder));
            f.Source.Oss.Setup(o=>o.DeleteAsync(It.IsAny<string>())).Returns((string p)=>oss.DeleteAsync(p));
            var item=(await f.ManualAsync(record)).Single();await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);
            var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? abandoned=null;var count=0;
            f.Copy=async(a,b)=>
            {
                var current=Interlocked.Increment(ref count);
                if(fault=="partial" && current==2){abandoned=b;await oss.CopyObjectAsync(a,b);throw new IOException("Injected Copy response loss.");}
                if(fault=="late" && current==1){abandoned=b;blocked.SetResult();await release.Task;await oss.CopyObjectAsync(a,b);return;}
                await oss.CopyObjectAsync(a,b);
            };
            if(fault=="late")
            {
                using var cancel=new CancellationTokenSource();var first=f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0,cancel.Token);
                await blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
            }
            else if(fault=="partial")await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0));
            Assert.True((await f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0)).Success);
            await using var db=f.Context();var op=await db.SourceReturns.SingleAsync();var candidates=await db.SourceReturnAttempts.ToListAsync();
            Assert.All(await db.SourceReturnObjects.ToListAsync(),o=>Assert.NotEqual(abandoned,candidates.Single(a=>a.AttemptId==o.SelectedAttemptId).ObjectKey));
            await using var student=f.Source.Context();var returned=await f.Source.Domain(student).GetUploadRecordAsync(op.ConfirmedRecordId!.Value,record.StudentId);Assert.NotNull(returned);
            var published=returned!.ImagePaths[0].Path;using var original=await oss.DownloadAsync(published);using var sourceBytes=new MemoryStream();await original.CopyToAsync(sourceBytes);
            Assert.True(await f.Source.Domain(student).RotateUploadImageAsync(returned.Id,record.StudentId,0,90));
            var edited=await f.Source.Domain(student).GetUploadRecordAsync(returned.Id,record.StudentId);Assert.NotEqual(published,edited!.ImagePaths[0].Path);
            var newKey=edited.ImagePaths[0].Path;using var newStream=await oss.DownloadAsync(newKey);using var newBytes=new MemoryStream();await newStream.CopyToAsync(newBytes);
            release.TrySetResult();await Task.Delay(200);
            using var stable=await oss.DownloadAsync(newKey);using var stableBytes=new MemoryStream();await stable.CopyToAsync(stableBytes);Assert.Equal(newBytes.ToArray(),stableBytes.ToArray());
            using var originalAfter=await oss.DownloadAsync(published);using var stableOriginal=new MemoryStream();await originalAfter.CopyToAsync(stableOriginal);Assert.Equal(sourceBytes.ToArray(),stableOriginal.ToArray());
            var posts=f.Source.Requests.Count(r=>r.Method=="POST");Assert.True((await f.Service().ResumeReturnAsync(item.Id)).Success);Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));
            Assert.True(await oss.ObjectExistsAsync(record.ImagePaths[0].Path));Assert.Equal(1,await db.ProcessLogs.CountAsync(l=>l.Action==4));Assert.False((await f.Service().GetAsync(record.Id,record.ContentRevision))!.HandoffCompleted);
        }
        finally
        {
            foreach(var item in await oss.ListObjectsAsync())await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(item.ObjectPath));
            await minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
        }
    }
}
