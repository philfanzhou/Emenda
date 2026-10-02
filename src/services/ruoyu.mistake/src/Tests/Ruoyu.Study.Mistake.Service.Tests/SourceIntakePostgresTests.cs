using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.Services;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceIntakePostgresTests
{
    [Fact]
    public async Task BothHttpCreateEntriesCanonicalAliasesOriginalResultAndAuthenticatedReadSurviveRestartDisableAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();
        var source=await f.SeedAsync(["uploads/中文/A.JPG","uploads/B.jpg","uploads/Aux.jpg"],["mistake","mistake","homework"],[90,270,45,180]);
        await f.StartAsync();var key=Guid.NewGuid();
        var body=new {sourceUploadId=source.Id,expectedContentRevision=source.ContentRevision,requestKey=key,studentId=source.StudentId,
            subject=1,grade=7,imagePaths=new[]{"uploads/中文/A.JPG","uploads/B.jpg"},rootCause="cause"};
        using var first=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",body,f.Token());Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var json=JsonDocument.Parse(await first.Content.ReadAsStringAsync());var id=Guid.Parse(json.RootElement.GetProperty("data").GetProperty("createdItemIds")[0].GetString()!);
        using var alias=await f.SendAsync(HttpMethod.Post,"/api/mistakes",new {sourceUploadId=source.Id,expectedContentRevision=source.ContentRevision,
            requestKey=Guid.NewGuid(),studentId=source.StudentId,subject=1,grade=7,rootCause="cause",
            sourceRegions=new[]{new{sourceImagePath="uploads/B.jpg"},new{sourceImagePath="uploads/中文/A.JPG"}}},f.Token());
        Assert.Equal(HttpStatusCode.OK,alias.StatusCode);using var aliasJson=JsonDocument.Parse(await alias.Content.ReadAsStringAsync());Assert.True(aliasJson.RootElement.GetProperty("data").TryGetProperty("id",out var aliasId),await alias.Content.ReadAsStringAsync());Assert.Equal(id.ToString(),aliasId.GetString());
        await using(var db=f.Context()){Assert.Equal(1,await db.MistakeItems.CountAsync());Assert.Equal(1,await db.ProcessLogs.CountAsync());Assert.Equal(2,await db.SourceIntakeRequests.CountAsync());}
        using var read=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/{id}",token:f.Token("user"));Assert.Equal(HttpStatusCode.OK,read.StatusCode);
        using var view=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/intakes/{source.Id}/{source.ContentRevision}",token:f.Token());
        Assert.Equal(HttpStatusCode.OK,view.StatusCode);var viewBody=await view.Content.ReadAsStringAsync();Assert.Contains("SourceRetained",viewBody);Assert.Contains("Aux.jpg",viewBody);Assert.Contains("270",viewBody);
        using var image=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath="uploads/中文/A.JPG",size="full"},f.Token("user"));Assert.Equal(HttpStatusCode.OK,image.StatusCode);
        using(var signed=JsonDocument.Parse(await image.Content.ReadAsStringAsync()))
        {
            var actual=await File.ReadAllBytesAsync(signed.RootElement.GetProperty("data").GetProperty("url").GetString()!);
            await using var expected=await f.Source.Files.DownloadAsync("uploads/中文/A.JPG");using var bytes=new MemoryStream();await expected.CopyToAsync(bytes);
            Assert.Equal(bytes.ToArray(),actual);
        }
        // Metadata may change; creation replay remains the immutable original result.
        await f.SqlAsync("UPDATE mistake_item SET subject=2,root_cause='edited' WHERE id=@id",new NpgsqlParameter("id",id));
        f.Enabled=false;await f.StartAsync(strict:false,resource:false);f.BeforeRead=_=>throw new IOException("Dependency unavailable.");
        using var replay=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",body,f.Token());Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(),await replay.Content.ReadAsStringAsync());
        using var anonymous=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/{id}");Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using var anonList=await f.SendAsync(HttpMethod.Get,$"/api/mistakes?studentId={source.StudentId}&subject=0&grade=0&reviewStatus=0&page=1&size=10");Assert.Equal(HttpStatusCode.Unauthorized,anonList.StatusCode);
        using var disabled=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",new {sourceUploadId=source.Id,expectedContentRevision=source.ContentRevision,requestKey=Guid.NewGuid(),studentId=source.StudentId,subject=1,grade=7,imagePaths=new[]{"uploads/B.jpg"}},f.Token());Assert.Equal(HttpStatusCode.ServiceUnavailable,disabled.StatusCode);
        await f.AssertRetainedAsync(source);
    }

    [Fact]
    public async Task ActualWorkerUsesRevisionSnapshotAndOneWholeFallbackGroupWithoutLegacySideEffectsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg"]);await f.StartAsync();
        var worker=new UploadPollingWorker(f.App!.Services,Options.Create(new VlAnalyzerOptions()),NullLogger<UploadPollingWorker>.Instance);
        var poll=typeof(UploadPollingWorker).GetMethod("PollAndProcessAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)poll.Invoke(worker,[CancellationToken.None])!;
        await (Task)poll.Invoke(worker,[CancellationToken.None])!;
        await using var db=f.Context();var item=await db.MistakeItems.SingleAsync();Assert.Equal(2,item.SourceRegions.Count);Assert.Equal(1,await db.ProcessLogs.CountAsync());
        var intake=await db.SourceIntakes.SingleAsync();Assert.Equal("Automatic",intake.Mode);Assert.True(intake.Materialized);
        f.Analyzer.Verify(a=>a.AnalyzeVerifiedAsync(It.IsAny<string>(),It.IsAny<List<string>>(),It.IsAny<IReadOnlyList<byte[]>>(),It.IsAny<CancellationToken>()),Times.Never);
        await f.AssertRetainedAsync(source);
        f.Enabled=false;await f.StartAsync();worker=new UploadPollingWorker(f.App!.Services,Options.Create(new VlAnalyzerOptions()),NullLogger<UploadPollingWorker>.Instance);await (Task)poll.Invoke(worker,[CancellationToken.None])!;await f.AssertRetainedAsync(source);
    }

    [Fact]
    public async Task AutomaticMultiGroupPlanIsAtomicAndNeverAnalyzedAgainAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();f.ConfiguredVl=true;var source=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg","uploads/c.jpg"]);
        f.Analyzer.Setup(a=>a.AnalyzeVerifiedAsync(It.IsAny<string>(),It.IsAny<List<string>>(),It.IsAny<IReadOnlyList<byte[]>>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlAnalysisResult{Groups=[new(){ImageIndices=[1,0],Subject=1,Grade=7,RootCause="first"},new(){ImageIndices=[2],Subject=3,Grade=8,RootCause="second"}]});
        var result=await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision);Assert.Equal(2,result.Count);
        var replay=await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision);Assert.Equal(result.Select(i=>i.Id).Order(),replay.Select(i=>i.Id).Order());
        var alias=await f.ManualAsync(source,regions:[new(){SourceImagePath="uploads/a.jpg"},new(){SourceImagePath="uploads/b.jpg"}],cause:"first");Assert.Equal(result.First(i=>i.Subject==1).Id,alias.Single().Id);
        var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source,regions:[Crop("uploads/c.jpg",0,0,1,1)]));Assert.Equal("source_mode_conflict",error.ErrorKind);
        f.Analyzer.Verify(a=>a.AnalyzeVerifiedAsync(It.IsAny<string>(),It.IsAny<List<string>>(),It.IsAny<IReadOnlyList<byte[]>>(),It.IsAny<CancellationToken>()),Times.Once);
        await using var db=f.Context();Assert.Equal(2,await db.MistakeItems.CountAsync());Assert.Equal(2,await db.ProcessLogs.CountAsync());await f.AssertRetainedAsync(source);
    }

    [Theory]
    [InlineData("omission")][InlineData("duplicate")][InlineData("out_of_range")][InlineData("empty")][InlineData("bad_payload")][InlineData("throws")]
    public async Task InvalidAutomaticAnalysisHasNoPartialItemsAndRetainsRetryableClaimAsync(string kind)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();f.ConfiguredVl=true;var source=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg"]);
        var groups=kind switch{
            "omission"=>new List<VlImageGroup>{new(){ImageIndices=[0],Subject=1,Grade=7}},
            "duplicate"=>[new(){ImageIndices=[0,0],Subject=1,Grade=7}],
            "out_of_range"=>[new(){ImageIndices=[0,2],Subject=1,Grade=7}],
            "bad_payload"=>[new(){ImageIndices=[0,1],Subject=0,Grade=7}],
            _=>[]};
        var setup=f.Analyzer.Setup(a=>a.AnalyzeVerifiedAsync(It.IsAny<string>(),It.IsAny<List<string>>(),It.IsAny<IReadOnlyList<byte[]>>(),It.IsAny<CancellationToken>()));
        if(kind=="throws")setup.ThrowsAsync(new IOException("Injected analysis error."));else setup.ReturnsAsync(new VlAnalysisResult{Groups=groups});
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));
        await using(var db=f.Context()){Assert.Equal(0,await db.MistakeItems.CountAsync());Assert.Equal(0,await db.ProcessLogs.CountAsync());Assert.False((await db.SourceIntakes.SingleAsync()).Materialized);}
        f.ConfiguredVl=false;Assert.Single(await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));await f.AssertRetainedAsync(source);
    }

    [Fact]
    public async Task ManualCropsHaveStableLegalKeysPayloadAndSharedAtomConflictsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var path=source.ImagePaths[0].Path;
        var a=await f.ManualAsync(source,regions:[Crop(path,0,0,10,10)]);var b=await f.ManualAsync(source,regions:[Crop(path,1,1,11,11)]);Assert.NotEqual(a.Single().Id,b.Single().Id);
        Assert.Equal(a.Single().Id,(await f.ManualAsync(source,regions:[Crop(path,0,0,10,10),Crop(path,0,0,10,10)],cause:"")).Single().Id);
        var payload=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source,regions:[Crop(path,0,0,10,10)],subject:2));Assert.Equal("payload_conflict",payload.ErrorKind);
        var grouping=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source,regions:[Crop(path,0,0,10,10),Crop(path,2,2,12,12)]));Assert.Equal("grouping_conflict",grouping.ErrorKind);
        var auto=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));Assert.Equal("source_mode_conflict",auto.ErrorKind);
        var key=Guid.NewGuid();await f.ManualAsync(source,key,regions:[Crop(path,20,20,int.MaxValue,int.MaxValue)]);
        var conflict=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source,key,regions:[Crop(path,21,20,int.MaxValue,int.MaxValue)]));Assert.Equal("request_payload_conflict",conflict.ErrorKind);
        await using var db=f.Context();Assert.Equal(3,await db.MistakeItems.CountAsync());Assert.Equal(3,await db.ProcessLogs.CountAsync());await f.AssertRetainedAsync(source);
    }
    [Theory]
    [InlineData(-1,0,10,10)][InlineData(0,-1,10,10)][InlineData(0,0,0,10)][InlineData(0,0,10,0)][InlineData(10,0,1,10)][InlineData(0,10,10,1)]
    public async Task InvalidRectanglesNeverAcquireModeOrCreateItemsAsync(int x1,int y1,int x2,int y2)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();
        var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source,regions:[Crop(source.ImagePaths[0].Path,x1,y1,x2,y2)]));Assert.Equal("invalid_region",error.ErrorKind);
        await using var db=f.Context();Assert.Empty(await db.SourceIntakes.ToListAsync());Assert.Empty(await db.MistakeItems.ToListAsync());Assert.Empty(f.Reads);
    }
    [Fact]
    public async Task ExactCaseUnicodeAndRepeatedManifestPathsHaveDistinctSemanticAtomsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync(["uploads/A.jpg","uploads/a.jpg","uploads/é.jpg","uploads/e\u0301.jpg","uploads/A.jpg"]);
        var result=await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision);Assert.Equal(4,result.Single().SourceRegions.Count);
        var replay=await f.ManualAsync(source,regions: f.Regions(source).AsEnumerable().Reverse().ToList(),cause:"Mistake upload (VL skipped)");Assert.Equal(result.Single().Id,replay.Single().Id);
        Assert.Equal(4,f.Reads.Distinct(StringComparer.Ordinal).Count());await f.AssertRetainedAsync(source);
    }

    [Theory]
    [InlineData("worker-worker")][InlineData("worker-manual")][InlineData("manual-manual")]
    public async Task IndependentOwnersSerializeModeAndStableMaterializedResultsAsync(string race)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeRead=async _=>{entered.TrySetResult();await release.Task;};var key=Guid.NewGuid();
        var first=race.StartsWith("worker",StringComparison.Ordinal) ? f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision) : f.ManualAsync(source,key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var exception=await Assert.ThrowsAsync<SourceIntakeException>(()=>race=="worker-worker" ? f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision) : f.ManualAsync(source,key));
        Assert.Equal(race=="worker-manual" ? "source_mode_conflict" : "intake_busy",exception.ErrorKind);
        release.SetResult();var created=await first;Assert.Single(created);
        await using var db=f.Context();Assert.Equal(1,await db.MistakeItems.CountAsync());Assert.Equal(1,await db.ProcessLogs.CountAsync());await f.AssertRetainedAsync(source);
    }
    [Fact]
    public async Task ExpiredOwnerTakeoverFencesLateAutomaticResultsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        f.BeforeRead=async _=>{if(Interlocked.Increment(ref calls)==1){entered.SetResult();await release.Task;}};
        var old=f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.SqlAsync("UPDATE source_intake SET lease_until=clock_timestamp()-interval '1 second'");
        var current=await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision);release.SetResult();
        var exception=await Assert.ThrowsAsync<SourceIntakeException>(()=>old);Assert.Equal("lease_lost",exception.ErrorKind);
        await using var db=f.Context();Assert.Equal(2,(await db.SourceIntakes.SingleAsync()).Fence);Assert.Equal(current.Single().Id,(await db.MistakeItems.SingleAsync()).Id);Assert.Equal(1,await db.ProcessLogs.CountAsync());await f.AssertRetainedAsync(source);
    }
    [Theory]
    [InlineData("hash")][InlineData("missing")][InlineData("partial_read")]
    public async Task SourceByteFailuresKeepClaimAndNeverPublishPartialResultAsync(string failure)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg"]);await f.Snapshots.AcquireAsync(source.Id,source.ContentRevision,CancellationToken.None);
        var path=Path.Combine(f.Source.Root,"objects","uploads/b.jpg");
        if(failure=="hash")
        {
            var bytes=await File.ReadAllBytesAsync(path);bytes[0]^=1;await File.WriteAllBytesAsync(path,bytes);
            // Same length proves the digest check, then a separate read proves length rejection.
            await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));
            await File.WriteAllBytesAsync(path,[..bytes,0]);
        }
        else if(failure=="missing")File.Delete(path);else f.BeforeRead=p=>p.EndsWith("b.jpg",StringComparison.Ordinal) ? throw new IOException("Partial read unavailable") : Task.CompletedTask;
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));
        await using var db=f.Context();Assert.Empty(await db.MistakeItems.ToListAsync());Assert.Empty(await db.SourceIntakeGroups.ToListAsync());Assert.Single(await db.SourceIntakes.ToListAsync());Assert.Empty(f.SideEffects);Assert.Empty(f.Source.Deletes);
    }
    [Fact]
    public async Task LostCommitResponseReplaysActualCommitAndBeforeCommitFailureRollsBackAllGroupsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var key=Guid.NewGuid();f.Fault.LoseCommitNumber=f.Fault.CommitCount+2;
        var result=await f.ManualAsync(source,key);Assert.Equal(result.Single().Id,(await f.ManualAsync(source,key)).Single().Id);
        await using(var db=f.Context()){Assert.Equal(1,await db.ProcessLogs.CountAsync());Assert.Equal(1,await db.MistakeItems.CountAsync());}
        var second=await f.SeedAsync(["uploads/second.jpg"]);f.BeforeRead=_=>{f.Fault.FailBeforeCommit=true;return Task.CompletedTask;};
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().CreateAutomaticAsync(second.Id,second.ContentRevision));
        await using(var db=f.Context()){Assert.Equal(1,await db.MistakeItems.CountAsync());Assert.Equal(1,await db.SourceIntakeGroups.CountAsync());Assert.Equal(1,await db.ProcessLogs.CountAsync());}
        f.BeforeRead=null;Assert.Single(await f.Service().CreateAutomaticAsync(second.Id,second.ContentRevision));await f.AssertRetainedAsync(source);await f.AssertRetainedAsync(second);
    }
    [Fact]
    public async Task CancellationAfterClaimPreservesSourceAndFreshOwnerCanRetryAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();using var cancel=new CancellationTokenSource();
        f.BeforeRead=_=>{cancel.Cancel();return Task.CompletedTask;};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision,cancel.Token));
        await using(var db=f.Context()){Assert.Single(await db.SourceIntakes.ToListAsync());Assert.Empty(await db.MistakeItems.ToListAsync());Assert.Empty(await db.ProcessLogs.ToListAsync());}
        f.BeforeRead=null;Assert.Single(await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));await f.AssertRetainedAsync(source);
        var second=await f.SeedAsync(["uploads/transaction-cancel.jpg"]);using var transactionCancel=new CancellationTokenSource();
        f.BeforeRead=_=>{f.Fault.CancelBeforeCommit=transactionCancel.Cancel;return Task.CompletedTask;};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Service().CreateAutomaticAsync(second.Id,second.ContentRevision,transactionCancel.Token));
        await using(var db=f.Context()){Assert.Single(await db.MistakeItems.ToListAsync());Assert.Single(await db.ProcessLogs.ToListAsync());Assert.Single(await db.SourceIntakeGroups.ToListAsync());Assert.False((await db.SourceIntakes.SingleAsync(s=>s.SourceUploadId==second.Id)).Materialized);}
        f.BeforeRead=null;Assert.Single(await f.Service().CreateAutomaticAsync(second.Id,second.ContentRevision));await f.AssertRetainedAsync(second);
    }
    [Fact]
    public async Task DatabaseGuardsRejectOldSqlWritersDeletesIdentityEditsAndTruncatesButAllowMetadataAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var item=(await f.ManualAsync(source)).Single();
        var mutations=new[]{
            $"DELETE FROM mistake_item WHERE id='{item.Id}'",$"UPDATE mistake_item SET student_id='other' WHERE id='{item.Id}'",
            $"UPDATE mistake_item SET source_upload_id='other' WHERE id='{item.Id}'",$"UPDATE mistake_item SET source_regions='[]' WHERE id='{item.Id}'",
            $"UPDATE mistake_item SET review_status=3 WHERE id='{item.Id}'", "DELETE FROM source_intake_group", "DELETE FROM source_intake_request", "DELETE FROM source_intake",
            "UPDATE source_intake SET manifest='{}'","UPDATE source_intake_group SET canonical='changed'","UPDATE source_intake_request SET canonical='changed'",
            "DELETE FROM process_log", "UPDATE process_log SET details='changed'", "TRUNCATE mistake_item CASCADE", "TRUNCATE source_intake CASCADE",
            $"INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES(gen_random_uuid(),'{source.StudentId}',1,7,'{source.Id}','[]',1,now())"};
        foreach(var mutation in mutations)await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync(mutation));
        await f.SqlAsync("UPDATE mistake_item SET subject=3,grade=8,root_cause='edited',review_status=2");
        await using var db=f.Context();Assert.Equal(item.Id,(await db.MistakeItems.SingleAsync()).Id);Assert.Equal(1,await db.ProcessLogs.CountAsync());await f.AssertRetainedAsync(source);
    }
    [Fact]
    public async Task LegacySourceConflictHasNoIntakeAndKeepsOriginalRowsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var legacy=Guid.NewGuid();
        await f.SqlAsync($"INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES('{legacy}','{source.StudentId}',1,7,'{source.Id}','[]',1,now())");
        var exception=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source));Assert.Equal("legacy_source_conflict",exception.ErrorKind);
        await using var db=f.Context();Assert.Equal(legacy,(await db.MistakeItems.SingleAsync()).Id);Assert.Empty(await db.SourceIntakes.ToListAsync());Assert.Empty(f.SideEffects);Assert.Empty(f.Source.Deletes);
    }
    [Fact]
    public async Task RealBackupRestoreKeepsManifestRequestModeLeaseItemLogAndDowngradeGuardAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var key=Guid.NewGuid();var result=await f.ManualAsync(source,key);
        await using(var db=f.Context())await Assert.ThrowsAnyAsync<Exception>(()=>db.GetService<IMigrator>().MigrateAsync("20260930000001_LeaseReanalyzeJobs"));
        await f.StartAsync();
        using(var confirm=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{result.Single().Id}/review",new{reviewStatus=2,reviewerId=f.Actor.ToString()},f.Token()))Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        using(var practice=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{result.Single().Id}/practices",new{studentId=source.StudentId,isCorrect=true,studentAnswer="retained"},f.Token("user")))Assert.Equal(HttpStatusCode.OK,practice.StatusCode);
        Guid practiceId;await using(var db=f.Context())practiceId=(await db.MistakePractices.SingleAsync()).Id;
        var dump=await f.BackupAsync();await using var restored=await SourceIntakeFixture.CreateAsync(initialize:false);await restored.RestoreAsync(dump);
        await using(var db=restored.Context()){await DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance);Assert.Equal(result.Single().Id,(await db.MistakeItems.SingleAsync()).Id);Assert.Equal(3,await db.ProcessLogs.CountAsync());Assert.Equal(practiceId,(await db.MistakePractices.SingleAsync()).Id);Assert.Equal("retained",(await db.MistakePractices.SingleAsync()).StudentAnswer);Assert.Equal(1,await db.SourceIntakeRequests.CountAsync());Assert.Equal(1,(await db.SourceIntakes.SingleAsync()).Fence);}
        restored.Enabled=false;var replay=await restored.Service().CreateManualAsync(source.Id,source.ContentRevision,key,f.Actor.ToString(),source.StudentId,1,7,null,f.Regions(source));Assert.Equal(result.Single().Id,replay.Single().Id);
        await restored.StartAsync(strict:false,resource:false);
        using(var get=await restored.SendAsync(HttpMethod.Get,$"/api/mistakes/intakes/{source.Id}/{source.ContentRevision}",token:restored.Token()))Assert.Equal(HttpStatusCode.OK,get.StatusCode);
        using(var post=await restored.SendAsync(HttpMethod.Post,"/api/mistakes/upload",new{sourceUploadId=source.Id,expectedContentRevision=source.ContentRevision,requestKey=key,studentId=source.StudentId,subject=1,grade=7,imagePaths=source.ImagePaths.Select(i=>i.Path).ToArray()},restored.Token(sub:f.Actor.ToString()))){Assert.Equal(HttpStatusCode.OK,post.StatusCode);Assert.Contains(result.Single().Id.ToString(),await post.Content.ReadAsStringAsync());}
        await Assert.ThrowsAsync<PostgresException>(()=>restored.SqlAsync("DELETE FROM mistake_item"));await f.AssertRetainedAsync(source);
    }
    [Fact]
    public async Task AutomaticSecondItemDatabaseFailureRollsBackAllGroupsItemsAndCreatedLogsAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync(["uploads/a.jpg","uploads/b.jpg"]);f.ConfiguredVl=true;
        f.Analyzer.Setup(a=>a.AnalyzeVerifiedAsync(It.IsAny<string>(),It.IsAny<List<string>>(),It.IsAny<IReadOnlyList<byte[]>>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VlAnalysisResult{Groups=[new(){ImageIndices=[0],Subject=1,Grade=7},new(){ImageIndices=[1],Subject=3,Grade=7}]});
        await f.SqlAsync("ALTER TABLE mistake_item ADD CONSTRAINT fixture_write_failure CHECK(subject<>3)");
        await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision));
        await using(var db=f.Context()){Assert.Empty(await db.MistakeItems.ToListAsync());Assert.Empty(await db.ProcessLogs.ToListAsync());Assert.Empty(await db.SourceIntakeGroups.ToListAsync());Assert.False((await db.SourceIntakes.SingleAsync()).Materialized);}
        await f.SqlAsync("ALTER TABLE mistake_item DROP CONSTRAINT fixture_write_failure");
        Assert.Equal(2,(await f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision)).Count);
        await using(var db=f.Context()){Assert.Equal(2,await db.ProcessLogs.CountAsync());Assert.Equal(2,await db.SourceIntakeGroups.CountAsync());}
        await f.AssertRetainedAsync(source);
    }
    [Fact]
    public async Task ConcurrentSameRequestDifferentPayloadFailsBeforeSecondObjectReadAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var key=Guid.NewGuid();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeRead=async _=>{entered.SetResult();await release.Task;};var owner=f.ManualAsync(source,key);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>f.ManualAsync(source,key,subject:2));Assert.Equal("request_payload_conflict",error.ErrorKind);
        release.SetResult();Assert.Single(await owner);await using var db=f.Context();Assert.Single(await db.ProcessLogs.ToListAsync());Assert.Single(await db.SourceIntakeRequests.ToListAsync());await f.AssertRetainedAsync(source);
    }
    private static SourceRegionModel Crop(string path,int x1,int y1,int x2,int y2)=>new(){SourceImagePath=path,BoundingBox=new(){X1=x1,Y1=y1,X2=x2,Y2=y2}};
}
