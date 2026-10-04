using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceReturnPostgresTests
{
    private static async Task<SourceIntakeFixture> FixtureAsync()
    {
        var f=await SourceIntakeFixture.CreateAsync(sourceStrict:true);f.TargetsEnabled=true;f.ReturnsEnabled=true;
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);return f;
    }
    private static async Task<(Ruoyu.Study.Student.Domain.Models.ImageUploadRecordModel Record,MistakeItemModel Item)> ReadyAsync(SourceIntakeFixture f,string[]? paths=null)
    {
        var record=await f.SeedAsync(paths ?? ["uploads/a.jpg"]);var item=(await f.ManualAsync(record)).Single();
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);f.Fault.CommitCount=0;return (record,item);
    }
    private static async Task AssertCompletedAsync(SourceIntakeFixture f,Guid item)
    {
        await using var db=f.Context();var operation=await db.SourceReturns.SingleAsync(o=>o.ItemId==item);
        Assert.Equal("Completed",operation.Phase);Assert.NotNull(operation.PayloadDigest);Assert.NotNull(operation.CompletedAt);
        Assert.Equal(3,(await db.MistakeItems.SingleAsync(m=>m.Id==item)).ReviewStatus);
        Assert.Equal(1,await db.ProcessLogs.CountAsync(l=>l.MistakeItemId==item && l.Action==4));
        Assert.Equal(1,await db.ProcessLogs.CountAsync(l=>l.MistakeItemId==item && l.Action==1));
        await using var student=f.Source.Context();var receipt=await student.ReturnedRecordOperations.SingleAsync(o=>o.OperationId==operation.OperationId);
        Assert.Equal(operation.ConfirmedRecordId,receipt.UploadRecordId);Assert.Equal(operation.PayloadDigest,receipt.PayloadDigest);
        Assert.DoesNotContain("delete",f.SideEffects);Assert.Empty(f.Source.Deletes);
    }
    [Theory]
    [InlineData("automatic")][InlineData("upload")][InlineData("add")]
    public async Task ThreeRealEntriesMultipleFullImagesAndHistoricalLifecycleAsync(string entry)
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/A.jpg","uploads/b.jpg","uploads/A.jpg"]);
        Guid id;
        if(entry=="automatic") id=(await f.Service().CreateAutomaticAsync(record.Id,record.ContentRevision)).Single().Id;
        else
        {
            await f.StartAsync();var payload=new Dictionary<string,object?>
            {
                ["studentId"]=record.StudentId,["sourceUploadId"]=record.Id,["expectedContentRevision"]=record.ContentRevision,
                ["requestKey"]=Guid.NewGuid(),["subject"]=1,["grade"]=7
            };
            // Keep the actual legacy upload paths and Add regions contracts separate.
            payload[entry=="upload" ? "imagePaths" : "sourceRegions"]=entry=="upload" ? (object)record.ImagePaths.Select(i=>i.Path) : f.Regions(record);
            using var created=await f.SendAsync(HttpMethod.Post,entry=="upload" ? "/api/mistakes/upload" : "/api/mistakes",payload,f.Token());Assert.Equal(HttpStatusCode.OK,created.StatusCode);
            await using var before=f.Context();id=(await before.SourceIntakeGroups.SingleAsync()).ItemId;
        }
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);await f.StartAsync();
        var body=new {reviewStatus=3,reviewerId=f.Actor,rootCause="Cause",returnReason="Reason",grade=8,subject=2,id=Guid.NewGuid()};
        using var returned=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{id}/review",body,f.Token("teacher"));
        Assert.Equal(HttpStatusCode.OK,returned.StatusCode);using var json=JsonDocument.Parse(await returned.Content.ReadAsStringAsync());Assert.True(json.RootElement.GetProperty("data").GetProperty("success").GetBoolean());Assert.Equal(record.Id.ToString(),json.RootElement.GetProperty("data").GetProperty("sourceUploadId").GetString());Assert.Equal(record.StudentId,json.RootElement.GetProperty("data").GetProperty("studentId").GetString());
        await AssertCompletedAsync(f,id);
        await using var db=f.Context();var objects=await db.SourceReturnObjects.OrderBy(o=>o.Position).ToListAsync();Assert.Equal(new[]{"uploads/A.jpg","uploads/b.jpg"},objects.Select(o=>o.SourcePath));
        Assert.All(await db.SourceReturnAttempts.ToListAsync(),a=>{Assert.Equal("Verified",a.Phase);Assert.NotNull(a.CopySettledAt);Assert.StartsWith("uploads/mistake-returns/",a.ObjectKey);});
        var repo=new MistakeItemRepository(db);Assert.Empty(await repo.GetBySourceUploadIdAsync(record.Id.ToString()));Assert.Empty((await repo.GetListAsync(record.StudentId,0,0,0,1,20)).Items);
        Assert.Single((await repo.GetListAsync(record.StudentId,0,0,3,1,20)).Items);Assert.NotNull(await repo.GetByIdAsync(id));Assert.Equal(0,(await repo.GetPendingReviewUploadsAsync(1,20)).TotalCount);
        var intake=await f.Service().GetAsync(record.Id,record.ContentRevision);Assert.False(intake!.HandoffCompleted);Assert.Equal("SourceRetained",intake.SourceState);Assert.Single(intake.Returns!);
        await using var student=f.Source.Context();var op=await db.SourceReturns.SingleAsync();var returnedRecord=await student.ImageUploadRecords.SingleAsync(r=>r.Id==op.ConfirmedRecordId);
        Assert.Equal("Reason",returnedRecord.ReturnReason);Assert.Equal("Rejected",returnedRecord.Comments);var entries=JsonSerializer.Deserialize<List<Ruoyu.Study.Student.Domain.Models.ImagePathEntry>>(returnedRecord.ImagePaths,IntakeCanonical.Json)!;Assert.Equal(2,entries.Count);
        Assert.Equal(2,await student.ImageFingerprints.CountAsync(p=>p.UploadRecordId==returnedRecord.Id));
        foreach(var image in entries) {using var stream=await f.Source.Files.DownloadAsync(image.Path);Assert.True(stream.Length>0);}
        var replay=(await f.Service().GetAsync(record.Id,record.ContentRevision))!;Assert.Equal(intake.ItemIds,replay.ItemIds);
        await student.ImageUploadRecords.Where(r=>r.Id==returnedRecord.Id).ExecuteDeleteAsync();
        var posts=f.Source.Requests.Count(r=>r.Method=="POST");f.Enabled=false;f.TargetsEnabled=false;f.ReturnsEnabled=false;await f.StartAsync(false,false);
        using var completed=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{id}/review",body,f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,completed.StatusCode);Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));
        using var anonymous=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/{id}");Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using var foreign=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/{id}",token:f.Token("user",sub:Guid.NewGuid().ToString()));Assert.Equal(HttpStatusCode.Forbidden,foreign.StatusCode);
    }
    [Theory]
    [InlineData(1,false)][InlineData(2,false)][InlineData(3,false)][InlineData(4,false)][InlineData(5,false)][InlineData(6,false)][InlineData(7,false)]
    [InlineData(1,true)][InlineData(2,true)][InlineData(3,true)][InlineData(4,true)][InlineData(5,true)][InlineData(6,true)][InlineData(7,true)]
    public async Task ActualEachCommitBoundaryBeforeFailureAndAfterResponseLossRecoversAsync(int boundary,bool lost)
    {
        await using var f=await FixtureAsync();var (_,item)=await ReadyAsync(f);
        if(lost)f.Fault.LoseCommitNumber=boundary;else f.Fault.FailCommitNumber=boundary;
        try {await f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason","Cause",0,0);}catch(SourceIntakeException error) {Assert.Equal(503,error.StatusCode);}
        f.Fault.LoseCommitNumber=0;f.Fault.FailCommitNumber=0;
        await f.SqlAsync("UPDATE source_intake SET lease_until=NULL");
        var result=await f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason","Cause",0,0);Assert.True(result.Success);
        await AssertCompletedAsync(f,item.Id);
    }
    [Fact]
    public async Task InflightUnknownCopyNeverPublishedEvenReadableAndLateTaskCannotRewriteSelectionAsync()
    {
        await using var f=await FixtureAsync();var (_,item)=await ReadyAsync(f);
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? unknown=null;var copies=0;f.Copy=async(a,b)=>{if(Interlocked.Increment(ref copies)==1){unknown=b;await f.Source.Files.CopyObjectAsync(a,b);blocked.SetResult();await release.Task;}else await f.Source.Files.CopyObjectAsync(a,b);};
        using var cancelled=new CancellationTokenSource();var first=f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0,cancelled.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        Assert.True(await f.Source.Files.ObjectExistsAsync(unknown!));var completed=await f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0);Assert.True(completed.Success);
        await using var db=f.Context();var selected=await db.SourceReturnObjects.SingleAsync();var attempt=await db.SourceReturnAttempts.SingleAsync(a=>a.AttemptId==selected.SelectedAttemptId);
        Assert.NotEqual(unknown,attempt.ObjectKey);Assert.Null((await db.SourceReturnAttempts.SingleAsync(a=>a.ObjectKey==unknown)).CopySettledAt);
        using var before=await f.Source.Files.DownloadAsync(attempt.ObjectKey);using var memory=new MemoryStream();await before.CopyToAsync(memory);release.SetResult();await Task.Delay(100);
        using var after=await f.Source.Files.DownloadAsync(attempt.ObjectKey);using var actual=new MemoryStream();await after.CopyToAsync(actual);Assert.Equal(memory.ToArray(),actual.ToArray());
        await AssertCompletedAsync(f,item.Id);
    }
    [Fact]
    public async Task PartialMultiImageAndCopySettledRecoveryPreservesFirstSelectedKeyAsync()
    {
        await using var f=await FixtureAsync();var (_,item)=await ReadyAsync(f,["uploads/a.jpg","uploads/b.jpg"]);var copies=0;
        f.Copy=async(a,b)=>{if(++copies==2){await f.Source.Files.CopyObjectAsync(a,b);throw new IOException("Injected lost Copy response.");}await f.Source.Files.CopyObjectAsync(a,b);};
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(item.Id,f.Actor.ToString(),null,null,0,0));
        Guid selected;await using(var db=f.Context()){selected=(await db.SourceReturnObjects.SingleAsync(o=>o.Position==0)).SelectedAttemptId!.Value;Assert.Equal(1,(await db.MistakeItems.SingleAsync()).ReviewStatus);}
        Assert.DoesNotContain(f.Source.Requests,r=>r.Method=="POST");f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);
        Assert.True((await f.Service().ResumeReturnAsync(item.Id)).Success);await using(var db=f.Context()){Assert.Equal(selected,(await db.SourceReturnObjects.SingleAsync(o=>o.Position==0)).SelectedAttemptId);Assert.Equal(3,await db.SourceReturnAttempts.CountAsync());}
        await AssertCompletedAsync(f,item.Id);
    }
    [Fact]
    public async Task AllWriterAndDeferredSqlGuardsRejectPartialFinalAndRetainHistoryAsync()
    {
        await using var f=await FixtureAsync();var (_,item)=await ReadyAsync(f);f.Copy=(_,_)=>throw new IOException("Injected Copy failure.");
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0));
        foreach(var sql in new[]{"UPDATE mistake_item SET review_status=2","UPDATE mistake_item SET review_status=3","UPDATE mistake_item SET root_cause='evil'","DELETE FROM source_return","DELETE FROM source_return_object","DELETE FROM source_return_attempt","TRUNCATE source_return CASCADE","TRUNCATE mistake_practice","TRUNCATE reanalyze_job","UPDATE source_return SET phase='Completed'","UPDATE source_return SET actor='other'","UPDATE source_return_attempt SET phase='Verified',verified_length=1,verified_sha256='bad'","BEGIN;"+SourceReturnSchema.Down+";COMMIT"})await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync(sql));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync($"INSERT INTO process_log(id,mistake_item_id,student_id,action,created_at) VALUES('{Guid.NewGuid()}','{item.Id}','{f.Student}',4,now())"));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync($"INSERT INTO reanalyze_job(id,item_id,reviewer_id,student_id,created_at) VALUES('{Guid.NewGuid()}','{item.Id}','{f.Actor}','{f.Student}',now())"));
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);Assert.True((await f.Service().ResumeReturnAsync(item.Id)).Success);
        foreach(var sql in new[]{"UPDATE mistake_item SET review_status=1","UPDATE mistake_item SET root_cause='evil'","UPDATE source_return SET phase='Prepared'","UPDATE source_return_object SET selected_attempt_id=NULL","UPDATE source_return_attempt SET object_key='uploads/evil.jpg'","DELETE FROM mistake_item","TRUNCATE process_log CASCADE"})await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync(sql));
        await AssertCompletedAsync(f,item.Id);
    }
    [Fact]
    public async Task PendingReviewAuthAndActualWorkerResumeSameOperationAsync()
    {
        await using var f=await FixtureAsync();var (_,item)=await ReadyAsync(f);await f.StartAsync();
        var body=new{reviewStatus=3,reviewerId=f.Actor,returnReason="Reason"};
        foreach(var token in new string?[]{null,f.Token("user"),f.Token("teacher",issuer:"wrong"),f.Token("teacher",audience:"wrong"),f.Token("teacher",expired:true)})
        {using var refused=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",body,token);Assert.True(refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);}
        using var forged=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",new{reviewStatus=3,reviewerId=Guid.NewGuid()},f.Token("teacher"));Assert.Equal(HttpStatusCode.Forbidden,forged.StatusCode);
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Copy=async(a,b)=>{blocked.SetResult();await release.Task;await f.Source.Files.CopyObjectAsync(a,b);};
        var original=f.Service().ReturnAsync(item.Id,f.Actor.ToString(),"Reason",null,0,0);await blocked.Task;
        using var pending=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",body,f.Token("teacher"));Assert.Equal(HttpStatusCode.Accepted,pending.StatusCode);
        using var json=JsonDocument.Parse(await pending.Content.ReadAsStringAsync());var data=json.RootElement.GetProperty("data");Assert.False(data.GetProperty("success").GetBoolean());Assert.Equal("退回处理中，请稍后重试",data.GetProperty("errorMessage").GetString());
        var different=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(item.Id,Guid.NewGuid().ToString(),"Other",null,0,0));Assert.Equal("return_payload_conflict",different.ErrorKind);
        release.SetResult();Assert.True((await original).Success);await AssertCompletedAsync(f,item.Id);
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);var (secondRecord,second)=await ReadyAsync(f,["uploads/second.jpg"]);f.Copy=(_,_)=>throw new IOException("Injected Copy failure.");await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(second.Id,f.Actor.ToString(),null,null,0,0));f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);
        var worker=new SourceReturnWorker(f.App!.Services.GetRequiredService<IServiceScopeFactory>(),NullLogger<SourceReturnWorker>.Instance);await worker.StartAsync(CancellationToken.None);
        var deadline=DateTime.UtcNow.AddSeconds(15);while((await f.Service().GetAsync(secondRecord.Id,secondRecord.ContentRevision))!.Returns!.All(r=>r.State!="Completed") && DateTime.UtcNow<deadline)await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);worker.Dispose();await AssertCompletedAsync(f,second.Id);
    }
}
