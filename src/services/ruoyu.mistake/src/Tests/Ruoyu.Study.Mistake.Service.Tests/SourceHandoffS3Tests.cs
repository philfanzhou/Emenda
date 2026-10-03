using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using Minio;
using Minio.DataModel.Args;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Services;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceHandoffS3Tests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BothProvidersUseSameRealS3AndRecoveryOnlyHistoricalReceiptAsync(bool lostStudentCommit)
    {
        var endpoint=Environment.GetEnvironmentVariable("MISTAKE_TARGET_TEST_S3_ENDPOINT");
        if(string.IsNullOrWhiteSpace(endpoint))throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_TARGET_TEST_S3_ENDPOINT to owned Seaweed S3.");
        var bucket="handoff-"+Guid.NewGuid().ToString("N");
        using var minio=new MinioClient().WithEndpoint(endpoint).WithCredentials("targetfixture","targetfixture2026").Build();
        var oss=new S3OssService(new OssOptions {InternalEndpoint=endpoint,AccessKey="targetfixture",SecretKey="targetfixture2026",BucketName=bucket,PublicBaseUrl="http://"+endpoint},["uploads/","mistakes/"]);
        await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
        try
        {
            await using var f=await SourceHandoffPostgresTests.FixtureAsync();f.SnapshotHttp.BaseAddress=f.Source.Client!.BaseAddress;
            var record=await f.SeedAsync(["uploads/A.jpg","uploads/B.jpg","uploads/A.jpg"],types:["mistake","mistake","homework"]);
            foreach(var entry in record.ImagePaths.DistinctBy(i=>i.Path))
            {
                var bytes=await File.ReadAllBytesAsync(Path.Combine(f.Source.Root,"objects",entry.Path));using var stream=new MemoryStream(bytes);
                await minio.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(entry.Path).WithStreamData(stream).WithObjectSize(stream.Length).WithContentType("image/jpeg"));
            }
            f.Oss.Setup(o=>o.DownloadAsync(It.IsAny<string>())).Returns((string p)=>oss.DownloadAsync(p));f.Copy=(a,b)=>oss.CopyObjectAsync(a,b);
            f.Source.Oss.Setup(o=>o.DownloadAsync(It.IsAny<string>())).Returns((string p)=>oss.DownloadAsync(p));
            f.Source.Oss.Setup(o=>o.ObjectExistsAsync(It.IsAny<string>())).Returns((string p)=>oss.ObjectExistsAsync(p));
            var items=await f.ManualAsync(record);await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);await f.StartAsync();
            foreach(var item in items){using var confirmed=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",new {reviewStatus=2,reviewerId=f.Actor},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);}
            if(lostStudentCommit)f.Source.Commit.LoseCommitResponse=true;
            using var handoff=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/handoff",token:f.Token());
            Assert.True(handoff.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
            // Independent service/context and provider listener restore reads the original receipt.
            var result=await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString());Assert.Equal("Completed",result.State);
            await using var db=f.Context();var targets=await db.SourceTargetAttempts.Where(t=>t.Phase=="Verified").ToListAsync();
            foreach(var target in targets)
            {
                f.Source.Oss.Verify(o=>o.DownloadAsync(target.TargetKey),Times.AtLeastOnce);
                Assert.True(await oss.ObjectExistsAsync(target.TargetKey));
            }
            foreach(var entry in record.ImagePaths)Assert.True(await oss.ObjectExistsAsync(entry.Path));
            await using var student=f.Source.Context();var receipt=await student.SourceHandoffReceipts.SingleAsync();Assert.False(receipt.RecordDeleted);
            var current=await student.ImageUploadRecords.SingleAsync(r=>r.Id==record.Id);current.ImagePaths="[]";await student.SaveChangesAsync();student.ImageUploadRecords.Remove(current);await student.SaveChangesAsync();
            f.Source.Oss.Invocations.Clear();f.HandoffsEnabled=false;
            Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
            var historical=await new Ruoyu.Study.Student.Database.Repositories.SourceHandoffRepository(student,f.Source.Oss.Object).GetAsync(record.Id,record.ContentRevision,default);Assert.Equal(receipt.ReceiptId,historical!.ReceiptId);
            Assert.DoesNotContain(f.Source.Oss.Invocations,i=>i.Method.Name is "DownloadAsync" or "DeleteAsync" or "CopyObjectAsync");
            foreach(var target in targets)Assert.False(await f.Source.Protection(student).DeleteIfUnreferencedAsync(target.TargetKey,oss));
        }
        finally
        {
            foreach(var item in await oss.ListObjectsAsync())await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(item.ObjectPath));
            await minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
        }
    }
}
