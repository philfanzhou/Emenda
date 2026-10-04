using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Service.Intake;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.Database;
using Record=Ruoyu.Study.Student.Domain.Models.ImageUploadRecordModel;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceHandoffPostgresTests
{
    internal static async Task<SourceIntakeFixture> FixtureAsync()
    {
        var f=await SourceIntakeFixture.CreateAsync(sourceStrict:true);f.TargetsEnabled=true;f.HandoffsEnabled=true;
        // Client certificates and service signing use the same immutable ring after provider restart.
        await f.Source.StartAsync(strict:true,handoffsEnabled:true);
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);return f;
    }
    [Fact]
    public async Task UploadCropCreationReplaysAfterCompletedHandoffLawfulCurrentRevisionEditAndDeleteAsync()
    {
        await using var f=await FixtureAsync();f.SnapshotHttp.BaseAddress=f.Source.Client!.BaseAddress;
        var record=await f.SeedAsync(["uploads/crop-history.jpg","uploads/current-homework.jpg"],types:["mistake","homework"]);await f.StartAsync(tls:true);
        var body=new {sourceUploadId=record.Id,expectedContentRevision=record.ContentRevision,requestKey=Guid.NewGuid(),studentId=record.StudentId,
            subject=1,grade=7,rootCause="original",sourceRegions=new[]{new{sourceImagePath=record.ImagePaths[0].Path,boundingBox=new{x1=1,y1=2,x2=11,y2=12}}}};
        using var first=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",body,f.Token());Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        var original=await first.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(original);var id=Guid.Parse(json.RootElement.GetProperty("data").GetProperty("createdItemIds")[0].GetString()!);
        using var blockedDelete=await f.SendAsync(HttpMethod.Delete,$"/api/mistakes/{id}",token:f.Token());Assert.Equal(HttpStatusCode.Conflict,blockedDelete.StatusCode);
        using var blockedIdentity=await f.SendAsync(HttpMethod.Put,$"/api/mistakes/{id}",new{studentId=Guid.NewGuid().ToString()},f.Token());Assert.Equal(HttpStatusCode.Conflict,blockedIdentity.StatusCode);
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);
        using var confirm=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{id}/review",new{reviewStatus=2,reviewerId=f.Actor},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        using var handoff=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/handoff",token:f.Token());Assert.Equal(HttpStatusCode.OK,handoff.StatusCode);
        var lateRegion=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!;
        lateRegion["requestKey"]=Guid.NewGuid().ToString();
        lateRegion["sourceRegions"]![0]!["boundingBox"]!["x1"]=20;
        lateRegion["sourceRegions"]![0]!["boundingBox"]!["x2"]=30;
        using var sealedSource=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",lateRegion,f.Token());
        Assert.Equal(HttpStatusCode.Conflict,sealedSource.StatusCode);Assert.Contains("source_sealed",await sealedSource.Content.ReadAsStringAsync());
        await using(var student=f.Source.Context())
        {
            var current=await student.ImageUploadRecords.SingleAsync(r=>r.Id==record.Id);Assert.NotEqual(record.ContentRevision,current.ContentRevision);
            var consumedRevision=current.ContentRevision;current.ImagePaths="[]";await student.SaveChangesAsync();Assert.NotEqual(consumedRevision,current.ContentRevision);
            student.ImageUploadRecords.Remove(current);await student.SaveChangesAsync();Assert.False(await student.ImageUploadRecords.AnyAsync(r=>r.Id==record.Id));
            Assert.Single(await student.SourceHandoffReceipts.ToListAsync());Assert.Single(await student.UploadSourceSnapshots.ToListAsync());
        }
        f.Enabled=false;await f.StartAsync(strict:false,resource:false,tls:true);f.BeforeRead=_=>throw new IOException("Replay must use permanent creation facts.");
        using var replay=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",body,f.Token());Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(original,await replay.Content.ReadAsStringAsync());
        await using var db=f.Context();Assert.Single(await db.MistakeItems.ToListAsync());Assert.Single(await db.SourceIntakeGroups.ToListAsync());Assert.Single(await db.SourceIntakeRequests.ToListAsync());
        Assert.Equal("Completed",(await db.SourceHandoffs.SingleAsync()).Phase);Assert.Equal(1,await db.ProcessLogs.CountAsync(l=>l.Action==1));
        Assert.Empty(f.Source.Deletes);Assert.DoesNotContain("delete",f.SideEffects);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealProviderBothHttpEntriesTerminalSourceConsumedOnceAndRetainedAsync(bool automatic)
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/猫😀.jpg","uploads/B.jpg","uploads/猫😀.jpg"],types:["mistake","mistake","homework"],rotations:[90,180]);
        // Restart changes the provider port; rebuild the trusted client fixture with the updated address.
        if(f.SnapshotHttp.BaseAddress!=f.Source.Client!.BaseAddress)f.SnapshotHttp.BaseAddress=f.Source.Client.BaseAddress;
        var items=automatic ? await f.Service().CreateAutomaticAsync(record.Id,record.ContentRevision) : await f.ManualAsync(record);
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);await f.StartAsync();
        foreach(var item in items)
        {
            using var confirmed=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",new {reviewStatus=2,reviewerId=f.Actor},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        }
        using var response=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/handoff",token:f.Token());Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.True(json.RootElement.GetProperty("data").GetProperty("success").GetBoolean());
        await using var db=f.Context();var op=await db.SourceHandoffs.SingleAsync();Assert.Equal("Completed",op.Phase);Assert.NotNull(op.Receipt);
        await using var student=f.Source.Context();var remaining=await student.ImageUploadRecords.SingleAsync(r=>r.Id==record.Id);Assert.NotEqual(record.ContentRevision,remaining.ContentRevision);
        Assert.Single(await student.SourceHandoffReceipts.ToListAsync());Assert.DoesNotContain("delete",f.SideEffects);Assert.Empty(f.Source.Deletes);
        var view=await f.Service().GetAsync(record.Id,record.ContentRevision);Assert.True(view!.HandoffCompleted);Assert.Equal("SourceRetained",view.SourceState);
        var puts=f.Source.Requests.Count(r=>r.Method=="PUT" && r.Path.EndsWith("/handoff"));f.HandoffsEnabled=false;
        var replay=await f.Service().HandoffAsync(record.Id,record.ContentRevision,Guid.NewGuid().ToString());Assert.Equal("Completed",replay.State);Assert.Equal(puts,f.Source.Requests.Count(r=>r.Method=="PUT" && r.Path.EndsWith("/handoff")));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("UPDATE mistake_item SET review_status=1"));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("DELETE FROM source_handoff"));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("TRUNCATE source_handoff"));
    }
    internal static async Task<Record> ReadyAsync(SourceIntakeFixture f,bool automatic=false,bool mixed=false)
    {
        if(f.SnapshotHttp.BaseAddress!=f.Source.Client!.BaseAddress)f.SnapshotHttp.BaseAddress=f.Source.Client.BaseAddress;
        var record=await f.SeedAsync(mixed ? ["uploads/a.jpg","uploads/a.jpg"] : ["uploads/a.jpg"],types:mixed ? ["mistake","homework"] : null);
        var items=automatic ? await f.Service().CreateAutomaticAsync(record.Id,record.ContentRevision) : await f.ManualAsync(record);
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);await f.StartAsync();
        foreach(var item in items)
        {using var confirmed=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",new {reviewStatus=2,reviewerId=f.Actor},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);}
        f.Fault.CommitCount=0;return record;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingOldWriterRechecksTerminalStatusAfterSourceLockAsync(bool seal)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f);
        var other=await ReadyAsync(f);
        await using var owner=new NpgsqlConnection(f.Connection);await owner.OpenAsync();
        await using var transaction=await owner.BeginTransactionAsync();
        async Task SqlAsync(string sql)
        {
            await using var command=new NpgsqlCommand(sql,owner,transaction);
            command.Parameters.AddWithValue("source",record.Id);command.Parameters.AddWithValue("revision",record.ContentRevision);
            await command.ExecuteNonQueryAsync();
        }
        await SqlAsync("SELECT pg_advisory_xact_lock(hashtextextended(@source::text,791249))");
        var applicationName="handoff-waiting-writer-"+Guid.NewGuid().ToString("N");
        await using var writer=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(f.Connection){ApplicationName=applicationName}.ConnectionString);
        await writer.OpenAsync();
        await using var update=new NpgsqlCommand("UPDATE mistake_item SET review_status=1 WHERE source_upload_id=@source",writer){CommandTimeout=20};
        update.Parameters.AddWithValue("source",record.Id.ToString("D"));
        var waitingWrite=update.ExecuteNonQueryAsync();
        try
        {
            var blocked=false;var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!blocked && DateTime.UtcNow<deadline)
            {
                await SqlAsync("SELECT pg_stat_clear_snapshot()");
                await using var observation=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@name AND wait_event='advisory')",owner,transaction);
                observation.Parameters.AddWithValue("name",applicationName);blocked=(bool)(await observation.ExecuteScalarAsync())!;
                if(!blocked)await Task.Delay(25);
            }
            Assert.True(blocked,"The old writer must wait on the source advisory lock before sealing.");
            // A different source remains writable while this source is locked.
            await f.SqlAsync("UPDATE mistake_item SET review_status=1 WHERE source_upload_id=@source",new NpgsqlParameter("source",other.Id.ToString("D")));
            if(seal)
            {
                await SqlAsync("UPDATE source_intake SET owner_id=gen_random_uuid(),fence=fence+1,lease_until=clock_timestamp()+interval '60 seconds' WHERE source_upload_id=@source AND content_revision=@revision");
                await SqlAsync("""
                    WITH groups AS(SELECT handoff_encode(VARIADIC ARRAY['mistake-handoff-groups-v1']||array_agg(handoff_encode(item_id::text,canonical) ORDER BY item_id::text COLLATE "C")) canonical FROM source_intake_group WHERE source_upload_id=@source AND content_revision=@revision),
                    targets AS(SELECT jsonb_agg(jsonb_build_object('sourcePath',o.source_path,'targetKey',a.target_key,'length',o.expected_length,'sha256',o.expected_sha256) ORDER BY handoff_sort_key(o.source_path) COLLATE "C")::text payload FROM source_target_object o JOIN source_target_attempt a ON a.attempt_id=o.selected_attempt_id WHERE o.source_upload_id=@source AND o.content_revision=@revision AND a.phase='Verified'),
                    fixed AS(SELECT s.*,g.canonical groups_canonical,encode(sha256(convert_to(g.canonical,'UTF8')),'hex') groups_digest,t.payload targets FROM source_intake s CROSS JOIN groups g CROSS JOIN targets t WHERE s.source_upload_id=@source AND s.content_revision=@revision),
                    encoded AS(SELECT f.*,handoff_payload(source_upload_id,content_revision,snapshot_id,student_id,groups_digest,targets) canonical FROM fixed f)
                    INSERT INTO source_handoff(source_upload_id,content_revision,snapshot_id,student_id,operation_id,actor,sealed_at,groups_canonical,groups_digest,targets,payload_canonical,payload_digest,owner_id,fence,phase)
                    SELECT source_upload_id,content_revision,snapshot_id,student_id,'mistake-handoff:'||snapshot_id::text,'ruoyu.mistake',clock_timestamp(),groups_canonical,groups_digest,targets,canonical,encode(sha256(convert_to(canonical,'UTF8')),'hex'),owner_id,fence,'Prepared' FROM encoded
                    """);
            }
            await transaction.CommitAsync();
            if(seal)
            {
                var rejected=await Assert.ThrowsAsync<PostgresException>(async()=>await waitingWrite.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("P0001",rejected.SqlState);Assert.Equal("source_sealed",rejected.MessageText);
            }
            else Assert.Equal(1,await waitingWrite.WaitAsync(TimeSpan.FromSeconds(10)));
            await using var final=f.Context();
            Assert.Equal(seal ? 2 : 1,(await final.MistakeItems.SingleAsync(i=>i.SourceUploadId==record.Id.ToString("D"))).ReviewStatus);
            Assert.Equal(1,(await final.MistakeItems.SingleAsync(i=>i.SourceUploadId==other.Id.ToString("D"))).ReviewStatus);
            if(seal)Assert.Equal("Prepared",(await final.SourceHandoffs.SingleAsync()).Phase);
            else Assert.Empty(await final.SourceHandoffs.ToListAsync());
        }
        finally
        {
            if(!waitingWrite.IsCompleted){update.Cancel();try{await waitingWrite;}catch(PostgresException){ }catch(OperationCanceledException){ }}
        }
    }
    [Fact]
    public async Task IncompleteManualThenMultipleCropGroupsSealAllAndExistingCreateReplaysAsync()
    {
        await using var f=await FixtureAsync();if(f.SnapshotHttp.BaseAddress!=f.Source.Client!.BaseAddress)f.SnapshotHttp.BaseAddress=f.Source.Client.BaseAddress;
        var record=await f.SeedAsync(["uploads/A.jpg","uploads/B.jpg"]);var key=Guid.NewGuid();
        var region=new Ruoyu.Study.Mistake.Domain.Models.SourceRegionModel {SourceImagePath="uploads/A.jpg",BoundingBox=new(){X1=0,Y1=0,X2=10,Y2=10}};
        var first=(await f.ManualAsync(record,key,[region])).Single();await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);
        await f.SqlAsync("UPDATE mistake_item SET review_status=2");Assert.True((await f.Service().GetAsync(record.Id,record.ContentRevision))!.TargetReady);
        Assert.Equal("handoff_not_ready",(await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString()))).ErrorKind);
        await using(var db=f.Context())Assert.Empty(await db.SourceHandoffs.ToListAsync());
        region=new(){SourceImagePath="uploads/A.jpg",BoundingBox=new(){X1=11,Y1=0,X2=20,Y2=10}};
        await f.ManualAsync(record,regions:[region]);await f.ManualAsync(record,regions:[new(){SourceImagePath="uploads/B.jpg"}]);
        Assert.False((await f.Service().GetAsync(record.Id,record.ContentRevision))!.TargetReady);
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);await f.SqlAsync("UPDATE mistake_item SET review_status=2");
        Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
        var replay=await f.ManualAsync(record,key,[new(){SourceImagePath="uploads/A.jpg",BoundingBox=new(){X1=0,Y1=0,X2=10,Y2=10}}]);Assert.Equal(first.Id,replay.Single().Id);
        Assert.Equal("source_sealed",(await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(record,regions:[new(){SourceImagePath="uploads/A.jpg",BoundingBox=new(){X1=0,Y1=11,X2=10,Y2=20}}]))).ErrorKind);
        await using var final=f.Context();Assert.Equal(3,await final.SourceIntakeGroups.CountAsync());Assert.Equal(3,await final.ProcessLogs.CountAsync(l=>l.Action==1));
    }
    [Theory]
    [InlineData(1,false)][InlineData(2,false)][InlineData(3,false)]
    [InlineData(1,true)][InlineData(2,true)][InlineData(3,true)]
    public async Task EachMistakeActualCommitBoundaryRecoversSameIdentityAsync(int boundary,bool lost)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f);
        if(lost)f.Fault.LoseCommitNumber=boundary;else f.Fault.FailCommitNumber=boundary;
        try{await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString());}catch(SourceIntakeException e){Assert.Equal(503,e.StatusCode);}
        f.Fault.LoseCommitNumber=0;f.Fault.FailCommitNumber=0;await f.SqlAsync("UPDATE source_intake SET lease_until=NULL");
        Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
        await using var db=f.Context();Assert.Single(await db.SourceHandoffs.ToListAsync());await using var student=f.Source.Context();Assert.Single(await student.SourceHandoffReceipts.ToListAsync());
    }
    [Fact]
    public async Task ArchivedMixedSourceAllowsRealNewRevisionNewActiveFreezeAndOldReceiptReplayAsync()
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f,mixed:true);await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString());
        await using var student=f.Source.Context();var current=await student.ImageUploadRecords.SingleAsync(r=>r.Id==record.Id);
        current.ImagePaths="[{\"path\":\"uploads/a.jpg\",\"type\":\"mistake\"}]";await student.SaveChangesAsync();
        var next=(await new Ruoyu.Study.Student.Database.Repositories.ImageUploadRecordRepository(student).GetByIdAsync(current.Id))!;
        Assert.NotEqual(record.ContentRevision,next.ContentRevision);var item=(await f.ManualAsync(next)).Single();Assert.NotEqual(item.Id,(await f.Service().GetAsync(record.Id,record.ContentRevision))!.ItemIds.Single());
        Assert.True(await new Ruoyu.Study.Student.Database.Repositories.ImageUploadRecordRepository(student).IsSourceFrozenAsync(record.Id));
        var old=await new Ruoyu.Study.Student.Database.Repositories.SourceHandoffRepository(student,f.Source.Oss.Object).GetAsync(record.Id,record.ContentRevision,default);Assert.NotNull(old);
        Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
        await Assert.ThrowsAsync<PostgresException>(()=>f.Source.SqlAsync("UPDATE image_upload_record SET image_paths='[]'"));
        await Assert.ThrowsAsync<PostgresException>(()=>f.Source.SqlAsync("UPDATE image_upload_record SET student_id='other'"));
        Assert.Equal(2,await student.UploadSourceSnapshots.CountAsync());
    }
    [Theory]
    [InlineData("lost_put")][InlineData("bad_json")][InlineData("receipt_identity")][InlineData("404")][InlineData("401")][InlineData("403")][InlineData("503")]
    public async Task ProviderFaultsNeverInferCompleteAndFreshClientResumesAsync(string fault)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f,mixed:true);
        using var client=new HttpClient(new ReceiptFault(fault){InnerHandler=new Ruoyu.Study.Mistake.Host.StudentServiceTokenHandler(f.Identity){InnerHandler=f.Source.CreateTrustedHandler()}}){BaseAddress=f.Source.Client!.BaseAddress};
        var service=new SourceIntakeService(f.DatabaseOptions,new(f.Enabled),f.Snapshots,f.Oss.Object,f.Analyzer.Object,Microsoft.Extensions.Options.Options.Create(new Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis.VlAnalyzerOptions()),new(f.TargetsEnabled),new(false),null,new(true),new(client,f.Identity));
        try{var result=await service.HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString());Assert.NotEqual("Completed",result.State);}catch(SourceIntakeException){ }
        await using var db=f.Context();var op=await db.SourceHandoffs.SingleAsync();Assert.NotEqual("Completed",op.Phase);
        if(fault=="receipt_identity"){Assert.Equal("Conflict",op.Phase);Assert.DoesNotContain(await f.Service().PendingHandoffsAsync(),p=>p.Source==record.Id);return;}
        if(fault=="lost_put")
        {
            await f.Source.SqlAsync("DELETE FROM image_upload_record");
            f.BeforeRead=_=>throw new IOException("Historical recovery cannot read targets.");f.Source.BeforeDownload=_=>throw new IOException("Historical provider cannot read objects.");
        }
        Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
        await using var student=f.Source.Context();Assert.Single(await student.SourceHandoffReceipts.ToListAsync());
    }
    [Theory]
    [InlineData("automatic")][InlineData("manual_unsealed")][InlineData("manual_sealed")]
    public async Task ActualWorkerSealsOnlyAutomaticAndResumesSavedManualAsync(string mode)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f,automatic:mode=="automatic");
        if(mode=="manual_sealed")
        {
            using var unavailable=new HttpClient(new ReceiptFault("503"){InnerHandler=new Ruoyu.Study.Mistake.Host.StudentServiceTokenHandler(f.Identity){InnerHandler=f.Source.CreateTrustedHandler()}}){BaseAddress=f.Source.Client!.BaseAddress};
            var blocked=new SourceIntakeService(f.DatabaseOptions,new(f.Enabled),f.Snapshots,f.Oss.Object,f.Analyzer.Object,Microsoft.Extensions.Options.Options.Create(new Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis.VlAnalyzerOptions()),new(f.TargetsEnabled),new(false),null,new(true),new(unavailable,f.Identity));
            await Assert.ThrowsAsync<SourceIntakeException>(()=>blocked.HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString()));
        }
        await f.StartAsync(handoffWorker:true);
        if(mode=="manual_unsealed")
        {await Task.Delay(350);await using var db=f.Context();Assert.Empty(await db.SourceHandoffs.ToListAsync());Assert.Empty(await f.Service().PendingHandoffsAsync());return;}
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(DateTime.UtcNow<deadline && !(await f.Service().GetAsync(record.Id,record.ContentRevision))!.HandoffCompleted)await Task.Delay(100);
        Assert.True((await f.Service().GetAsync(record.Id,record.ContentRevision))!.HandoffCompleted);
        await using var source=f.Source.Context();Assert.Single(await source.SourceHandoffReceipts.ToListAsync());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelledOrLateLeaseOwnerCannotCompleteAndFreshInstanceResumesAsync(bool takeover)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeRead=async path=>{if(path.StartsWith("mistakes/source-targets/",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}};
        using var cancelled=new CancellationTokenSource();var old=f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString(),ct:cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("intake_busy",(await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString()))).ErrorKind);
        Assert.Equal("P0001",(await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("INSERT INTO source_intake_request(actor,action,request_key,source_upload_id,content_revision,canonical,item_ids) SELECT actor,action,gen_random_uuid(),source_upload_id,content_revision,canonical,NULL FROM source_intake_request LIMIT 1"))).SqlState);
        if(takeover)
        {
            await f.SqlAsync("UPDATE source_intake SET lease_until=NULL");f.BeforeRead=null;
            Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
            release.SetResult();Assert.Equal("lease_lost",(await Assert.ThrowsAsync<SourceIntakeException>(()=>old)).ErrorKind);
        }
        else
        {
            cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>old);release.SetResult();f.BeforeRead=null;
            await using(var before=f.Source.Context())Assert.Empty(await before.SourceHandoffReceipts.ToListAsync());
            Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
        }
        await using var db=f.Context();Assert.Single(await db.SourceHandoffs.ToListAsync());await using var student=f.Source.Context();Assert.Single(await student.SourceHandoffReceipts.ToListAsync());
    }

    [Fact]
    public async Task ConfirmedAndRealManagedCompletedReturnTogetherPermitFullHandoffAsync()
    {
        await using var f=await FixtureAsync();f.ReturnsEnabled=true;if(f.SnapshotHttp.BaseAddress!=f.Source.Client!.BaseAddress)f.SnapshotHttp.BaseAddress=f.Source.Client.BaseAddress;
        var record=await f.SeedAsync(["uploads/A.jpg","uploads/B.jpg"]);
        var confirmed=(await f.ManualAsync(record,regions:[new(){SourceImagePath="uploads/A.jpg"}])).Single();
        var rejected=(await f.ManualAsync(record,regions:[new(){SourceImagePath="uploads/B.jpg"}])).Single();
        await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);await f.StartAsync();
        using var reviewed=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{confirmed.Id}/review",new {reviewStatus=2,reviewerId=f.Actor},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,reviewed.StatusCode);
        using var returned=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{rejected.Id}/review",new {reviewStatus=3,reviewerId=f.Actor,returnReason="Reason"},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,returned.StatusCode);
        await using(var db=f.Context())Assert.Equal("Completed",(await db.SourceReturns.SingleAsync()).Phase);
        using var handoff=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/handoff",token:f.Token());Assert.Equal(HttpStatusCode.OK,handoff.StatusCode);
        await using var final=f.Context();Assert.Equal("Completed",(await final.SourceHandoffs.SingleAsync()).Phase);Assert.Equal(2,await final.MistakeItems.CountAsync());
        await using var student=f.Source.Context();Assert.Single(await student.SourceHandoffReceipts.ToListAsync());Assert.Single(await student.ReturnedRecordOperations.ToListAsync());Assert.Empty(f.Source.Deletes);Assert.DoesNotContain("delete",f.SideEffects);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task HandoffAdminRoleRemainsRequiredAcrossCompatibilityAndDisabledSwitchesAsync(bool strict)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f);f.HandoffsEnabled=false;await f.StartAsync(strict:strict,resource:strict);
        var route=$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/handoff";
        using var anonymous=await f.SendAsync(HttpMethod.Post,route);Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        foreach(var role in new[]{"teacher","assistant","user"}){using var denied=await f.SendAsync(HttpMethod.Post,route,token:f.Token(role));Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);}
        using var disabled=await f.SendAsync(HttpMethod.Post,route,token:f.Token());Assert.Equal(HttpStatusCode.ServiceUnavailable,disabled.StatusCode);
        await using var db=f.Context();Assert.Empty(await db.SourceHandoffs.ToListAsync());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task NormalTlsUntrustedCaOrSanCannotProduceHandoffReceiptAsync(bool badSan)
    {
        await using var f=await FixtureAsync();var record=await ReadyAsync(f);using var trusted=f.Source.CreateTrustedHandler();
        HttpMessageHandler transport=new HttpClientHandler{UseProxy=false};var address=f.Source.Client!.BaseAddress!;
        if(badSan)
        {
            var socket=new SocketsHttpHandler{UseProxy=false};socket.ConnectCallback=async (_,ct)=>{var tcp=new System.Net.Sockets.TcpClient();try{await tcp.ConnectAsync(IPAddress.Loopback,address.Port,ct);return tcp.GetStream();}catch{tcp.Dispose();throw;}};
            socket.SslOptions.RemoteCertificateValidationCallback=(_,cert,chain,errors)=>trusted.ServerCertificateCustomValidationCallback!(new HttpRequestMessage(),cert as System.Security.Cryptography.X509Certificates.X509Certificate2,chain,errors);
            transport.Dispose();transport=socket;address=new Uri($"https://bad-san.fixture:{address.Port}");
        }
        using var client=new HttpClient(new Ruoyu.Study.Mistake.Host.StudentServiceTokenHandler(f.Identity){InnerHandler=transport}){BaseAddress=address};
        var service=new SourceIntakeService(f.DatabaseOptions,new(f.Enabled),f.Snapshots,f.Oss.Object,f.Analyzer.Object,Microsoft.Extensions.Options.Options.Create(new Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis.VlAnalyzerOptions()),new(f.TargetsEnabled),new(false),null,new(true),new(client,f.Identity));
        Assert.Equal("handoff_receipt_unavailable",(await Assert.ThrowsAsync<SourceIntakeException>(()=>service.HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString()))).ErrorKind);
        await using(var db=f.Source.Context())Assert.Empty(await db.SourceHandoffReceipts.ToListAsync());
        Assert.Equal("Completed",(await f.Service().HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString())).State);
    }
    private sealed class ReceiptFault(string fault):DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);
            if(request.Method==HttpMethod.Put && fault=="lost_put"){response.Dispose();throw new HttpRequestException("Injected committed PUT response loss.");}
            if(request.Method!=HttpMethod.Get)return response;
            if(int.TryParse(fault,out var code)){response.Dispose();return new((HttpStatusCode)code);}
            if(!response.IsSuccessStatusCode)return response;
            if(fault=="bad_json"){response.Content=new StringContent("{");return response;}
            if(fault=="receipt_identity"){var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;json["snapshotId"]=Guid.NewGuid().ToString();response.Content=new StringContent(json.ToJsonString(),System.Text.Encoding.UTF8,"application/json");}
            return response;
        }
    }

}
