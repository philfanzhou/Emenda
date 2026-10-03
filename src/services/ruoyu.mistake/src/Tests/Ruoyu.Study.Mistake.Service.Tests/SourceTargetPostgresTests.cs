using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceTargetPostgresTests
{
    private static async Task<SourceIntakeFixture> FixtureAsync()
    {
        var f=await SourceIntakeFixture.CreateAsync();f.TargetsEnabled=true;
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);return f;
    }
    [Fact]
    public async Task ReservationPublishSharedOriginalLateGroupReplayAndFullReadAsync()
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg","uploads/unused.jpg"]);
        var region=new SourceRegionModel {SourceImagePath="uploads/a.jpg",BoundingBox=new(){X1=0,Y1=0,X2=100,Y2=100}};
        var key=Guid.NewGuid();var original=await f.ManualAsync(record,key,[region]);
        var result=await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);Assert.True(result.TargetReady);Assert.False(result.HandoffCompleted);
        Assert.Equal("SourceRetained",result.SourceState);var selected=Assert.Single(result.Targets!);Assert.Equal("Verified",selected.State);
        Assert.True(await f.Source.Files.ObjectExistsAsync("uploads/a.jpg"));Assert.Equal(1,f.SideEffects.Count(x=>x=="copy"));
        await f.ManualAsync(record,regions:[new(){SourceImagePath="uploads/a.jpg",BoundingBox=new(){X1=100,Y1=0,X2=200,Y2=100}}]);
        Assert.True((await f.Service().GetAsync(record.Id,record.ContentRevision))!.TargetReady);
        await f.ManualAsync(record,regions:[new(){SourceImagePath="uploads/b.jpg"}]);Assert.False((await f.Service().GetAsync(record.Id,record.ContentRevision))!.TargetReady);
        var next=await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);Assert.Equal(2,next.Targets!.Count);Assert.Equal(selected.TargetKey,next.Targets.First(t=>t.SourcePath=="uploads/a.jpg").TargetKey);
        Assert.Equal(original.Single().Id,(await f.ManualAsync(record,key,[region])).Single().Id);Assert.Equal(2,f.SideEffects.Count(x=>x=="copy"));Assert.DoesNotContain("delete",f.SideEffects);
        await f.StartAsync();using var signed=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=selected.TargetKey,size="full"},f.Token("user"));Assert.Equal(HttpStatusCode.OK,signed.StatusCode);
        await using(var db=f.Context()) {Assert.Equal(2,await db.SourceTargetObjects.CountAsync());Assert.Equal(2,await db.SourceTargetAttempts.CountAsync());await DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance);}
    }
    [Fact]
    public async Task CopyResponseLossRecoveryNeverRecopiesExistingKeyAsync()
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(record);
        f.Copy=async(a,b)=>{await f.Source.Files.CopyObjectAsync(a,b);throw new IOException("Injected copy response loss.");};
        var failure=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision));Assert.Equal(503,failure.StatusCode);
        await using(var db=f.Context()){var a=await db.SourceTargetAttempts.SingleAsync();Assert.Equal("Unknown",a.Phase);Assert.Null((await db.SourceTargetObjects.SingleAsync()).SelectedAttemptId);}
        f.Copy=(_,_)=>throw new InvalidOperationException("Recovery must only read existing candidate.");
        var recovered=await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);Assert.True(recovered.TargetReady);Assert.Equal(1,f.SideEffects.Count(x=>x=="copy"));
        Assert.True(await f.Source.Files.ObjectExistsAsync("uploads/a.jpg"));
    }
    [Fact]
    public async Task InvalidBytesAreNeverSelectedAndReadTamperingFailsClosedAsync()
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(record);
        f.Copy=(_,_)=>Task.CompletedTask;
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision));
        await using(var db=f.Context())Assert.Null((await db.SourceTargetObjects.SingleAsync()).SelectedAttemptId);
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);var ready=await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);Assert.True(ready.TargetReady);
        f.Oss.Setup(o=>o.DownloadAsync(ready.Targets!.Single().TargetKey!)).ReturnsAsync(new MemoryStream([9]));
        await f.StartAsync();using var read=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=ready.Targets!.Single().TargetKey,size="full"},f.Token());Assert.Equal(HttpStatusCode.ServiceUnavailable,read.StatusCode);
        Assert.True(await f.Source.Files.ObjectExistsAsync("uploads/a.jpg"));
    }
    [Fact]
    public async Task TargetAdminMatrixOwnershipDisabledFlagsAndUnknownNamespaceAsync()
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(record);await f.StartAsync();
        var path=$"/api/mistakes/intakes/{record.Id}/{record.ContentRevision}/targets";
        foreach(var token in new string?[]{null,f.Token(issuer:"https://wrong.invalid"),f.Token(audience:"wrong"),f.Token(sub:""),f.Token(expired:true)})
        {using var denied=await f.SendAsync(HttpMethod.Post,path,token:token);Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);}
        foreach(var role in new[]{"user","teacher","assistant"})
        {using var denied=await f.SendAsync(HttpMethod.Post,path,token:f.Token(role));Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);}
        using var resume=await f.SendAsync(HttpMethod.Post,path,token:f.Token());Assert.Equal(HttpStatusCode.OK,resume.StatusCode);
        var target=(await f.Service().GetAsync(record.Id,record.ContentRevision))!.Targets!.Single().TargetKey;
        foreach(var flags in new[]{(false,false),(true,false),(true,true)})
        {
            f.Enabled=false;f.TargetsEnabled=false;await f.StartAsync(flags.Item1,flags.Item2);
            using var owner=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=target,size="full"},f.Token("user"));Assert.Equal(HttpStatusCode.OK,owner.StatusCode);
            using var wrong=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=target,size="full"},f.Token("user",sub:Guid.NewGuid().ToString()));Assert.Equal(HttpStatusCode.Forbidden,wrong.StatusCode);
            using var unknown=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath="mistakes/source-targets/unknown",size="full"},f.Token());Assert.Equal(HttpStatusCode.Forbidden,unknown.StatusCode);
            using var anon=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=target,size="full"});Assert.Equal(HttpStatusCode.Unauthorized,anon.StatusCode);
            using var staff=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=target,size="full"},f.Token("teacher"));Assert.Equal(HttpStatusCode.OK,staff.StatusCode);
            using var unknownStaff=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath="mistakes/source-targets/unknown",size="full"},f.Token("teacher"));Assert.Equal(HttpStatusCode.Forbidden,unknownStaff.StatusCode);
            using var disabled=await f.SendAsync(HttpMethod.Post,path,token:f.Token());Assert.Equal(HttpStatusCode.ServiceUnavailable,disabled.StatusCode);
        }
        await f.Source.App!.StopAsync();
        using var unavailable=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=target,size="full"},f.Token("user"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,unavailable.StatusCode);
        f.Oss.Verify(o=>o.DeleteAsync(It.IsAny<string>()),Times.Never);
    }
    [Fact]
    public async Task DirectSqlGuardsRejectForgedBindingsDeletesTruncatesAndDowngradeAsync()
    {
        await using var f=await FixtureAsync();var record=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(record);await f.Service().ResumeTargetsAsync(record.Id,record.ContentRevision);
        foreach(var sql in new[]{"DELETE FROM source_target_object","DELETE FROM source_target_attempt","TRUNCATE source_target_object CASCADE","TRUNCATE source_target_attempt CASCADE","UPDATE source_target_object SET student_id='other'","UPDATE source_target_object SET selected_attempt_id=NULL","UPDATE source_target_attempt SET target_key='mistakes/evil'","UPDATE source_target_attempt SET verified_sha256='bad'","BEGIN;"+SourceTargetSchema.Down+";COMMIT"})
            await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync(sql));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("UPDATE mistake_item SET source_regions='[{\"sourceImagePath\":\"uploads/source-targets/forged\"}]'::jsonb"));
        await using var db=f.Context();Assert.Single(await db.SourceTargetObjects.ToListAsync());Assert.True((await f.Service().GetAsync(record.Id,record.ContentRevision))!.TargetReady);
    }

    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public async Task ActualCommitResponseLossAtEveryBoundaryRecoversAsync(int boundary)
    {
        await using var f=await FixtureAsync();var source=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(source);
        f.Fault.LoseCommitNumber=f.Fault.CommitCount+boundary;
        try {await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);}catch(SourceIntakeException ex){Assert.Equal(503,ex.StatusCode);}
        f.Fault.LoseCommitNumber=0;await f.SqlAsync("UPDATE source_intake SET lease_until=NULL");
        var result=await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);Assert.True(result.TargetReady);
        await using var db=f.Context();Assert.Single(await db.SourceTargetObjects.ToListAsync());Assert.DoesNotContain("delete",f.SideEffects);
        Assert.True(await f.Source.Files.ObjectExistsAsync("uploads/a.jpg"));
    }
    [Fact]
    public async Task CancelAfterNoncancelableCopyRetainsCandidateAndRestartReadsItAsync()
    {
        await using var f=await FixtureAsync();var source=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(source);using var cancellation=new CancellationTokenSource();
        f.Copy=async(a,b)=>{await f.Source.Files.CopyObjectAsync(a,b);cancellation.Cancel();};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision,cancellation.Token));
        await using(var db=f.Context()) {Assert.Null((await db.SourceTargetObjects.SingleAsync()).SelectedAttemptId);Assert.Equal("Reserved",(await db.SourceTargetAttempts.SingleAsync()).Phase);}
        f.Enabled=false;f.Copy=(_,_)=>throw new InvalidOperationException("Restart recovery must not copy again.");
        var resumed=await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);Assert.True(resumed.TargetReady);Assert.Single(f.SideEffects);
    }
    [Fact]
    public async Task ExpiredAndLateOwnerCannotPublishOrOverwriteSelectedKeyAsync()
    {
        await using var f=await FixtureAsync();var source=await f.SeedAsync(["uploads/a.jpg"]);await f.ManualAsync(source);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        f.Copy=async(a,b)=>{if(Interlocked.Increment(ref calls)==1){entered.SetResult();await release.Task;}await f.Source.Files.CopyObjectAsync(a,b);};
        var old=f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var busy=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision));Assert.Equal(409,busy.StatusCode);
        await f.SqlAsync("UPDATE source_intake SET lease_until=clock_timestamp()-interval '1 second'");
        var winner=await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);var key=winner.Targets!.Single().TargetKey;release.SetResult();
        await Assert.ThrowsAsync<SourceIntakeException>(()=>old);Assert.Equal(key,(await f.Service().GetAsync(source.Id,source.ContentRevision))!.Targets!.Single().TargetKey);
        await using var db=f.Context();Assert.Equal(2,await db.SourceTargetAttempts.CountAsync());Assert.Equal(1,await db.SourceTargetObjects.CountAsync());
        await f.Service().VerifyManagedObjectAsync(key!);Assert.True(await f.Source.Files.ObjectExistsAsync("uploads/a.jpg"));Assert.DoesNotContain("delete",f.SideEffects);
    }
    [Fact]
    public async Task KnownSixHistoryNonemptyUpgradeAndDumpRestoreRetainIdsAndResumeAsync()
    {
        await using var f=await FixtureAsync();
        var source=await f.SeedAsync(["uploads/a.jpg"]);var item=(await f.ManualAsync(source)).Single();
        await using(var db=f.Context()) await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(SourceIntakeSchema.Migration);
        await using(var db=f.Context())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));
            await DatabaseInitializer.UpgradeKnownAsync(db,f.Name,true,true);
        }
        Assert.Equal(item.Id,await f.ScalarAsync<Guid>("SELECT id FROM mistake_item"));Assert.Equal(10,await f.ScalarAsync<int>("SELECT count(*)::int FROM \"__EFMigrationsHistory\""));
        f.Copy=(_,_)=>throw new IOException("Injected outage preserves reservation.");await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision));
        var dump=await f.BackupAsync();
        // Destroy only this random fixture database, then exercise an actual pg_dump restoration.
        await f.SqlAsync("DROP SCHEMA public CASCADE;CREATE SCHEMA public");await f.RestoreAsync(dump);
        await using(var db=f.Context())await DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance);
        f.Copy=(a,b)=>f.Source.Files.CopyObjectAsync(a,b);Assert.True((await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision)).TargetReady);
        Assert.Equal(item.Id,await f.ScalarAsync<Guid>("SELECT id FROM mistake_item"));
    }

    [Theory]
    [InlineData("mistakes/source-targets/forged/original")]
    [InlineData("uploads/source-targets/forged/original")]
    [InlineData("UPLOADS/source-targets/forged/original")]
    public async Task LegacyMoversRefuseActualSourceAndComputedDestinationBeforeSideEffectsAsync(string path)
    {
        await using var f=await FixtureAsync();f.Enabled=false;
        await using(var db=f.Context())await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(SourceIntakeSchema.Migration);
        var id=Guid.NewGuid();var upload=Guid.NewGuid().ToString();
        await f.SqlAsync("INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES (@id,@student,1,7,@source,@regions::jsonb,2,now())",
            new NpgsqlParameter("id",id),new NpgsqlParameter("student",f.Student.ToString()),new NpgsqlParameter("source",upload),
            new NpgsqlParameter("regions",JsonSerializer.Serialize(new[]{new{sourceImagePath=path}})));
        await using(var db=f.Context())await DatabaseInitializer.UpgradeKnownAsync(db,f.Name,true,true);
        await f.StartAsync();
        using var complete=await f.SendAsync(HttpMethod.Post,"/api/mistakes/complete-review",new{sourceUploadId=upload,reviewerId=f.Actor.ToString()},f.Token());Assert.Equal(HttpStatusCode.Conflict,complete.StatusCode);
        using var reject=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{id}/review",new{reviewStatus=3,reviewerId=f.Actor.ToString()},f.Token());Assert.Equal(HttpStatusCode.Conflict,reject.StatusCode);
        var record=new Ruoyu.Study.Mistake.Service.StudentIntegration.ProcessingUploadRecord{Id=upload,StudentId=f.Student.ToString(),ImageEntries=[new(){Path=path,Type="mistake"}]};
        var worker=new Ruoyu.Study.Mistake.Host.Services.UploadPollingWorker(f.App!.Services,Microsoft.Extensions.Options.Options.Create(new Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis.VlAnalyzerOptions()),NullLogger<Ruoyu.Study.Mistake.Host.Services.UploadPollingWorker>.Instance);
        var method=typeof(Ruoyu.Study.Mistake.Host.Services.UploadPollingWorker).GetMethod("ProcessRecordAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        using var scope=Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(f.App.Services);
        await Assert.ThrowsAsync<SourceIntakeException>(()=>(Task)method.Invoke(worker,[record,scope.ServiceProvider,CancellationToken.None])!);
        Assert.Empty(f.SideEffects);Assert.Empty(f.Source.Deletes);Assert.Empty(f.Source.Requests);
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("UPDATE mistake_item SET source_regions=@regions::jsonb",new NpgsqlParameter("regions",JsonSerializer.Serialize(new[]{new{sourceImagePath=path}}))));
        await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("INSERT INTO mistake_practice(id,mistake_item_id,student_id,practice_date,image_paths,is_correct) VALUES (@id,@item,@student,now(),@paths::jsonb,true)",new NpgsqlParameter("id",Guid.NewGuid()),new NpgsqlParameter("item",id),new NpgsqlParameter("student",f.Student.ToString()),new NpgsqlParameter("paths",JsonSerializer.Serialize(new[]{path}))));
    }
    [Theory]
    [InlineData("false","false","true","Source targets require strict")]
    [InlineData("true","false","true","Source targets require strict")]
    [InlineData("true","true","invalid","SourceTargets:Enabled must be true or false")]
    public async Task ActualHostRejectsTargetUnsafeActivationBeforeAnyDatabaseAsync(string strict,string resource,string targets,string diagnostic)
    {
        var settings=new Dictionary<string,string?> {["Mistake__ConfigMode"]="Local",["ConnectionStrings__MistakeLocal"]="Host=127.0.0.1;Port=1;Database=blocked;Username=fixture;Password=synthetic",
            ["Mistake__SourceTargets__Enabled"]=targets,["IdentityService__MistakeApiStrictAuth"]=strict,["IdentityService__MistakeApiResourceAuth"]=resource};
        var result=await LocalBootstrapPostgresTests.RunHostAsync([],settings);Assert.NotEqual(0,result.ExitCode);Assert.Contains(diagnostic,result.Output);Assert.DoesNotContain("Database: PostgreSQL",result.Output);
    }

    [Fact]
    public async Task UnreferencedManifestImageCannotForgeTargetOwnershipAsync()
    {
        await using var f=await FixtureAsync();var source=await f.SeedAsync(["uploads/a.jpg","uploads/unused.jpg"]);await f.ManualAsync(source,regions:[new(){SourceImagePath="uploads/a.jpg"}]);
        await f.Service().ResumeTargetsAsync(source.Id,source.ContentRevision);var manifest=(await f.Service().GetAsync(source.Id,source.ContentRevision))!.Manifest;var unused=manifest.Images.Single(i=>i.Path=="uploads/unused.jpg");
        await f.SqlAsync("UPDATE source_intake SET lease_until=clock_timestamp()+interval '60 seconds'");
        var error=await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("INSERT INTO source_target_object(source_upload_id,content_revision,source_path,snapshot_id,student_id,expected_length,expected_sha256) VALUES (@source,@revision,@path,@snapshot,@student,@length,@hash)",
            new NpgsqlParameter("source",source.Id),new NpgsqlParameter("revision",source.ContentRevision),new NpgsqlParameter("path",unused.Path),new NpgsqlParameter("snapshot",manifest.SnapshotId),new NpgsqlParameter("student",source.StudentId),new NpgsqlParameter("length",unused.Length),new NpgsqlParameter("hash",unused.Sha256)));
        Assert.Contains("target_binding",error.Message);Assert.Single(f.SideEffects);
    }
}
