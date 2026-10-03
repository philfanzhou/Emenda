using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Domain.Services;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceReturnRecoveryPostgresTests
{
    private static async Task<SourceIntakeFixture> FixtureAsync()
    {var f=await SourceIntakeFixture.CreateAsync(sourceStrict:true);f.TargetsEnabled=true;f.ReturnsEnabled=true;f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);return f;}
    private static async Task<Guid> ReadyAsync(SourceIntakeFixture f)
    {var r=await f.SeedAsync(["uploads/"+Guid.NewGuid()+".jpg"]);var item=(await f.ManualAsync(r)).Single();await f.Service().ResumeTargetsAsync(r.Id,r.ContentRevision);f.Fault.CommitCount=0;return item.Id;}
    private sealed class ProviderFault(HttpClient real,string fault) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var forwarded=new HttpRequestMessage(request.Method,request.RequestUri);
            foreach(var h in request.Headers)forwarded.Headers.TryAddWithoutValidation(h.Key,h.Value);
            if(request.Content!=null){forwarded.Content=new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct));foreach(var h in request.Content.Headers)forwarded.Content.Headers.TryAddWithoutValidation(h.Key,h.Value);}
            var response=await real.SendAsync(forwarded,ct);
            if(request.Method==HttpMethod.Post && fault=="lost-post") {response.Dispose();throw new IOException("Injected POST response loss after real commit.");}
            if(request.Method!=HttpMethod.Get || response.StatusCode!=HttpStatusCode.OK) return response;
            if(fault=="404" || fault=="503" || fault=="401" || fault=="403" || fault=="bad-json") {response.Dispose();return new HttpResponseMessage(fault=="404"?HttpStatusCode.NotFound:fault=="503"?HttpStatusCode.ServiceUnavailable:fault=="401"?HttpStatusCode.Unauthorized:fault=="403"?HttpStatusCode.Forbidden:HttpStatusCode.OK){Content=new StringContent("invalid-json")};}
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;var data=json["data"]!;
            switch(fault) {case "operation":data["operationId"]="other";break;case "student":data["studentId"]="other";break;case "record":data["uploadRecordId"]=Guid.Empty;break;case "version":data["digestVersion"]="unknown";break;case "digest":data["payloadDigest"]=new string('0',64);break;case "time":data["createdAt"]="not-a-time";break;case "offset":data["createdAt"]="2026-10-03T00:00:00+08:00";break;}
            response.Dispose();return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.ToJsonString(),System.Text.Encoding.UTF8,"application/json")};
        }
    }
    [Theory]
    [InlineData("operation")][InlineData("student")][InlineData("record")][InlineData("version")][InlineData("digest")][InlineData("time")][InlineData("offset")]
    [InlineData("401")][InlineData("403")][InlineData("404")][InlineData("503")][InlineData("bad-json")][InlineData("lost-post")]
    public async Task RealReceiptFaultNeverCompletesAndSamePayloadHistoricalGetRecoversAsync(string fault)
    {
        await using var f=await FixtureAsync();var id=await ReadyAsync(f);
        using var http=new HttpClient(new ProviderFault(f.SnapshotHttp,fault)){BaseAddress=f.SnapshotHttp.BaseAddress};var service=f.Service(new SourceReturnClient(http,f.Identity));
        try {var view=await service.ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0);Assert.False(view.Success);}catch(SourceIntakeException error){Assert.True(error.StatusCode is 409 or 503);}
        await using var db=f.Context();var op=await db.SourceReturns.SingleAsync();Assert.NotEqual("Completed",op.Phase);Assert.Equal(1,(await db.MistakeItems.SingleAsync()).ReviewStatus);Assert.Equal(0,await db.ProcessLogs.CountAsync(l=>l.Action==4));
        var conflict=fault is "operation" or "student" or "record" or "version" or "digest" or "time" or "offset";
        Assert.Equal(conflict?"Conflict":"Sending",op.Phase);var posts=f.Source.Requests.Count(r=>r.Method=="POST");
        if(conflict){await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ResumeReturnAsync(id));Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));return;}
        await using(var student=f.Source.Context())await student.ImageUploadRecords.Where(r=>r.Id==student.ReturnedRecordOperations.Select(o=>o.UploadRecordId).First()).ExecuteDeleteAsync();
        Assert.True((await f.Service().ResumeReturnAsync(id)).Success);Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));
        db.ChangeTracker.Clear();Assert.Equal(1,await db.ProcessLogs.CountAsync(l=>l.Action==4));Assert.Equal("Completed",(await db.SourceReturns.SingleAsync()).Phase);
    }
    [Theory]
    [InlineData(1)][InlineData(2)]
    public async Task ActualPendingOrExpiredRunningJobBlocksReturnBeforeAnyIoAsync(int state)
    {
        await using var f=await FixtureAsync();var id=await ReadyAsync(f);var job=Guid.NewGuid();
        await f.SqlAsync($"INSERT INTO reanalyze_job(id,item_id,reviewer_id,student_id,status,owner_id,lease_until,created_at) VALUES('{job}','{id}','{f.Actor}','{f.Student}',1,'{Guid.NewGuid()}',clock_timestamp()-interval '1 second',now())");
        if(state==2){await using var running=f.Context();Assert.Single(await new ReanalyzeJobRepository(running).ClaimAsync(1));await f.SqlAsync("UPDATE reanalyze_job SET lease_until=clock_timestamp()-interval '1 second'");}
        var copies=f.SideEffects.Count;var posts=f.Source.Requests.Count(r=>r.Method=="POST");
        var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0));Assert.Equal("reanalysis_active",error.ErrorKind);
        Assert.Equal(0,await f.ScalarAsync<int>("SELECT count(*)::int FROM source_return"));Assert.Equal(copies,f.SideEffects.Count);Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));
        await using var db=f.Context();var claimed=await new ReanalyzeJobRepository(db).ClaimAsync(1);Assert.Single(claimed);
        Assert.True(await new ReanalyzeJobRepository(db).CompleteAsync(job,claimed[0].OwnerId!.Value,claimed[0].FencingVersion,"new",10));
        Assert.True((await f.Service().ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0)).Success);
    }
    [Fact]
    public async Task IndependentInstancesAndConcurrentSqlWritersCannotBypassDurableIntentAsync()
    {
        await using var f=await FixtureAsync();var id=await ReadyAsync(f);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Copy=async(a,b)=>{entered.SetResult();await release.Task;await f.Source.Files.CopyObjectAsync(a,b);};
        var running=f.Service().ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await f.Service().ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0)).Success);
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(id,f.Actor.ToString(),"Different",null,0,0));
        var sql=new[]{"UPDATE mistake_item SET review_status=2","UPDATE mistake_item SET root_cause='evil'",$"INSERT INTO mistake_practice(id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES('{Guid.NewGuid()}','{id}','{f.Student}',now(),'[]'::jsonb,true)",$"INSERT INTO process_log(id,mistake_item_id,student_id,action,created_at) VALUES('{Guid.NewGuid()}','{id}','{f.Student}',4,now())",$"INSERT INTO reanalyze_job(id,item_id,reviewer_id,student_id,created_at) VALUES('{Guid.NewGuid()}','{id}','{f.Actor}','{f.Student}',now())"};
        await Task.WhenAll(sql.Select(s=>Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync(s))));
        await f.StartAsync();
        var writes=new[]{
            (HttpMethod.Post,$"/api/mistakes/{id}/review",(object)new{reviewStatus=2,reviewerId=f.Actor},f.Token("teacher")),
            (HttpMethod.Put,$"/api/mistakes/{id}",(object)new{grade=8},f.Token()),
            (HttpMethod.Post,$"/api/mistakes/{id}/reanalyze",(object)new{studentId=f.Student,reviewerId=f.Actor,teacherDescription="retry"},f.Token("teacher")),
            (HttpMethod.Post,$"/api/mistakes/{id}/practices",(object)new{studentId=f.Student,isCorrect=true},f.Token("user")),
            (HttpMethod.Post,$"/api/mistakes/{id}/process-logs",(object)new{studentId=f.Student,action=4,details="evil"},f.Token("user"))};
        foreach(var (method,path,body,token) in writes){using var response=await f.SendAsync(method,path,body,token);Assert.True(response.StatusCode==(path.EndsWith("/practices") ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict),$"{method} {path}: {response.StatusCode}");}
        release.SetResult();Assert.True((await running).Success);
        Assert.Equal(1,await f.ScalarAsync<int>("SELECT count(*)::int FROM source_return"));Assert.Equal(1,await f.ScalarAsync<int>("SELECT count(*)::int FROM process_log WHERE action=4"));
    }

    [Fact]
    public async Task ConfirmFirstAndOtherItemsAndLaterGroupsKeepOriginalCreateReplayAsync()
    {
        await using var f=await FixtureAsync();var source=await f.SeedAsync(["uploads/shared.jpg"]);var key=Guid.NewGuid();
        var first=(await f.ManualAsync(source,key)).Single();
        var second=(await f.ManualAsync(source,regions:[new(){SourceImagePath="uploads/shared.jpg",BoundingBox=new(){X1=100,Y1=0,X2=200,Y2=100}}])).Single();
        await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);
        Assert.True((await f.Service().ReturnAsync(first.Id,f.Actor.ToString(),"Reason",null,0,0)).Success);await f.StartAsync();
        using var confirm=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{second.Id}/review",new{reviewStatus=2,reviewerId=f.Actor},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        var copies=f.SideEffects.Count;var posts=f.Source.Requests.Count(r=>r.Method=="POST");
        var denied=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ReturnAsync(second.Id,f.Actor.ToString(),"Reason",null,0,0));Assert.Equal("return_not_pending",denied.ErrorKind);Assert.Equal(copies,f.SideEffects.Count);Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));
        var late=(await f.ManualAsync(source,regions:[new(){SourceImagePath="uploads/shared.jpg",BoundingBox=new(){X1=200,Y1=0,X2=300,Y2=100}}])).Single();
        Assert.Equal(1,late.ReviewStatus);var replay=(await f.ManualAsync(source,key)).Single();Assert.Equal(first.Id,replay.Id);Assert.Equal(1,replay.ReviewStatus);
        await using var db=f.Context();Assert.Equal(3,await db.SourceIntakeGroups.CountAsync());Assert.Equal(1,await db.SourceReturns.CountAsync());Assert.Equal(1,await db.SourceTargetObjects.CountAsync());Assert.Equal(3,await db.ProcessLogs.CountAsync(l=>l.Action==1));Assert.Equal(1,await db.ProcessLogs.CountAsync(l=>l.Action==4));
        var view=(await f.Service().GetAsync(source.Id,source.ContentRevision))!;Assert.False(view.HandoffCompleted);Assert.Equal("SourceRetained",view.SourceState);
    }

    [Fact]
    public async Task RealDumpRestoreAllReturnStatesAndCandidatesRetainsFactsAndResumesAsync()
    {
        await using var f=await FixtureAsync();var ids=new List<Guid>();
        foreach(var state in new[]{"Prepared","Unknown","CopySettled","Sending","Conflict","Completed"})
        {
            f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);var id=await ReadyAsync(f);ids.Add(id);
            using var http=new HttpClient(new ProviderFault(f.SnapshotHttp,state=="Sending"?"503":"digest")){BaseAddress=f.SnapshotHttp.BaseAddress};
            var service=state is "Sending" or "Conflict"?f.Service(new SourceReturnClient(http,f.Identity)):f.Service();
            if(state=="Prepared")f.Copy=(_,_)=>throw new IOException("before copy");
            if(state=="Unknown")f.Copy=async(a,b)=>{await f.Source.Files.CopyObjectAsync(a,b);throw new IOException("lost copy response");};
            if(state=="CopySettled")f.Fault.FailCommitNumber=5;
            try{await service.ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0);}catch(SourceIntakeException){}f.Fault.FailCommitNumber=0;
        }
        Assert.Equal(1,await f.ScalarAsync<int>("SELECT count(*)::int FROM source_return_attempt WHERE phase='CopySettled' AND copy_settled_at IS NOT NULL"));
        Assert.Equal(2,await f.ScalarAsync<int>("SELECT count(*)::int FROM source_return_attempt WHERE phase IN ('Reserved','Unknown') AND copy_settled_at IS NULL"));
        var before=await f.ScalarAsync<string>("SELECT string_agg(row_to_json(s)::text,'|' ORDER BY item_id) FROM source_return s");
        var attempts=await f.ScalarAsync<string>("SELECT string_agg(row_to_json(s)::text,'|' ORDER BY attempt_id) FROM source_return_attempt s");
        var dump=await f.BackupAsync();await f.SqlAsync("DROP SCHEMA public CASCADE;CREATE SCHEMA public");await f.RestoreAsync(dump);
        Assert.Equal(before,await f.ScalarAsync<string>("SELECT string_agg(row_to_json(s)::text,'|' ORDER BY item_id) FROM source_return s"));Assert.Equal(attempts,await f.ScalarAsync<string>("SELECT string_agg(row_to_json(s)::text,'|' ORDER BY attempt_id) FROM source_return_attempt s"));
        await using(var db=f.Context())await DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance);
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);
        foreach(var id in ids.Where((_,i)=>i!=4))Assert.True((await f.Service().ResumeReturnAsync(id)).Success);
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ResumeReturnAsync(ids[4]));Assert.Equal(5,await f.ScalarAsync<int>("SELECT count(*)::int FROM process_log WHERE action=4"));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("TRUNCATE source_return CASCADE"));
    }

    [Theory]
    [InlineData(false,"407a1f76d8e7719465e2149ae854c71fabbbea3da62ffacede9cde2356cdf80c")]
    [InlineData(true,"141ae46614cbe29dcd08b781eb417f749ab49822e12d7faa3f387ae14f919153")]
    public async Task ConsumerAndActualPostgresDigestMatchProviderFixedV1Utf16VectorsAsync(bool unicode,string expected)
    {
        await using var f=await FixtureAsync();var student=unicode?"学😀":"s";
        var payload=new Ruoyu.Study.Mistake.StudentClient.CreateReturnedRecordRequest{Comments=unicode?"备注:|😀":"",ReturnReason=unicode?"退回中文|:":null,ImageEntries=unicode?[new(){Path="uploads/猫😀:|.jpg",Type="MiStAkE"},new(){Path="uploads/a.jpg",Type="mistake"},new(){Path="uploads/猫😀:|.jpg",Type="MiStAkE"}]:[new(){Path="uploads/A",Type="mistake"}]};
        Assert.Equal(expected,SourceReturnClient.Digest(student,payload));await using var db=f.Context();var json=System.Text.Json.JsonSerializer.Serialize(payload,IntakeCanonical.Json);
        Assert.Equal(expected,await db.Database.SqlQueryRaw<string>("SELECT return_payload_digest(@student,@payload) AS \"Value\"",new NpgsqlParameter("student",student),new NpgsqlParameter("payload",json)).SingleAsync());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UntrustedRealTlsReceiptFailsClosedWithoutAnonymousOrPostFallbackAsync(bool badSan)
    {
        await using var f=await FixtureAsync();var id=await ReadyAsync(f);var posts=f.Source.Requests.Count(r=>r.Method=="POST");
        using var trusted=f.Source.CreateTrustedHandler();
        HttpMessageHandler transport=new HttpClientHandler{UseProxy=false};var address=f.SnapshotHttp.BaseAddress!;
        if(badSan)
        {
            var socket=new SocketsHttpHandler{UseProxy=false};
            socket.ConnectCallback=async (_,ct)=>{var tcp=new System.Net.Sockets.TcpClient();try{await tcp.ConnectAsync(IPAddress.Loopback,address.Port,ct);return tcp.GetStream();}catch{tcp.Dispose();throw;}};
            socket.SslOptions.RemoteCertificateValidationCallback=(_,cert,chain,errors)=>trusted.ServerCertificateCustomValidationCallback!(new HttpRequestMessage(),cert as System.Security.Cryptography.X509Certificates.X509Certificate2,chain,errors);
            transport.Dispose();transport=socket;address=new Uri($"https://bad-san.fixture:{address.Port}");
        }
        using var token=new Ruoyu.Study.Mistake.Host.StudentServiceTokenHandler(f.Identity){InnerHandler=transport};using var client=new HttpClient(token){BaseAddress=address};
        var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service(new SourceReturnClient(client,f.Identity)).ReturnAsync(id,f.Actor.ToString(),"Reason",null,0,0));Assert.Equal("return_receipt_unavailable",error.ErrorKind);Assert.Equal(posts,f.Source.Requests.Count(r=>r.Method=="POST"));Assert.Equal("Sending",await f.ScalarAsync<string>("SELECT phase FROM source_return"));Assert.True((await f.Service().ResumeReturnAsync(id)).Success);
    }

    [Fact]
    public async Task NonemptySevenHistoryUpgradesAndCatalogDriftFailsReadOnlyAsync()
    {
        await using var f=await FixtureAsync();var id=await ReadyAsync(f);
        await using(var db=f.Context())await db.GetService<IMigrator>().MigrateAsync(SourceTargetSchema.Migration);
        await using(var db=f.Context()){await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));await DatabaseInitializer.UpgradeKnownAsync(db,f.Name,true,true);await DatabaseInitializer.UpgradeKnownAsync(db,f.Name,true,true);}
        Assert.Equal(id,await f.ScalarAsync<Guid>("SELECT id FROM mistake_item"));Assert.Equal(9,await f.ScalarAsync<int>("SELECT count(*)::int FROM \"__EFMigrationsHistory\""));
        await f.SqlAsync("ALTER TABLE source_return DISABLE TRIGGER trg_source_return");
        await using(var db=f.Context())await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));
        Assert.Equal("D",await f.ScalarAsync<string>("SELECT tgenabled::text FROM pg_trigger WHERE tgname='trg_source_return'"));
    }
}
