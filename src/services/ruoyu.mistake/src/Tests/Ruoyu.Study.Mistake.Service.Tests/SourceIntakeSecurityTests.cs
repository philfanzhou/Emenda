using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host;
using Ruoyu.Study.Mistake.Host.Services.Intake;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceIntakeSecurityTests
{
    [Theory]
    [InlineData("anonymous")][InlineData("issuer")][InlineData("audience")][InlineData("role")][InlineData("forged_student")][InlineData("missing_key")][InlineData("missing_revision")]
    public async Task HttpAuthenticationRoleOwnerAndRequiredInputsRejectBeforeSourceOrObjectCallsAsync(string kind)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();await f.StartAsync();
        var token=kind switch {"anonymous"=>null,"issuer"=>f.Token(issuer:"https://wrong.invalid"),"audience"=>f.Token(audience:"wrong"),"role"=>f.Token("user"),_=>f.Token()};
        using var response=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",new{sourceUploadId=source.Id,studentId=kind=="forged_student" ? Guid.NewGuid().ToString() : source.StudentId,
            expectedContentRevision=kind=="missing_revision" ? (Guid?)null : source.ContentRevision,requestKey=kind=="missing_key" ? (Guid?)null : Guid.NewGuid(),subject=1,grade=7,imagePaths=source.ImagePaths.Select(i=>i.Path).ToArray(),actor="forged"},token);
        Assert.Equal(kind is "anonymous" or "issuer" or "audience" ? HttpStatusCode.Unauthorized : kind is "role" or "forged_student" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest,response.StatusCode);
        if(kind=="missing_key")
        {
            using var nullPaths=await f.SendAsync(HttpMethod.Post,"/api/mistakes/upload",new{sourceUploadId=source.Id,studentId=source.StudentId,expectedContentRevision=source.ContentRevision,requestKey=Guid.NewGuid(),subject=1,grade=7,imagePaths=(string[]?)null},f.Token());Assert.Equal(HttpStatusCode.BadRequest,nullPaths.StatusCode);
            using var nullRegion=await f.SendAsync(HttpMethod.Post,"/api/mistakes",new{sourceUploadId=source.Id,studentId=source.StudentId,expectedContentRevision=source.ContentRevision,requestKey=Guid.NewGuid(),subject=1,grade=7,sourceRegions=new object?[]{null}},f.Token());Assert.Equal(HttpStatusCode.BadRequest,nullRegion.StatusCode);
            Assert.Empty(f.Source.Requests);
        }
        await using var db=f.Context();Assert.Empty(await db.SourceIntakes.ToListAsync());Assert.Empty(await db.MistakeItems.ToListAsync());Assert.Empty(f.Reads);Assert.Empty(f.SideEffects);
    }
    [Fact]
    public async Task ManagedHttpGuardsAndOwnershipRemainActiveWhileNewIntakeDisabledAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var item=(await f.ManualAsync(source)).Single();f.Enabled=false;await f.StartAsync(strict:false,resource:false);
        var attempts=new (HttpMethod Method,string Path,object? Body)[]{
            (HttpMethod.Delete,$"/api/mistakes/{item.Id}",null),
            (HttpMethod.Put,$"/api/mistakes/{item.Id}",new{studentId="other"}),
            (HttpMethod.Put,$"/api/mistakes/{item.Id}",new{sourceRegions=new[]{new{sourceImagePath="uploads/else.jpg"}}}),
            (HttpMethod.Post,$"/api/mistakes/{item.Id}/review",new{reviewStatus=3,reviewerId=f.Actor.ToString()}),
            (HttpMethod.Post,"/api/mistakes/complete-review",new{sourceUploadId=source.Id,reviewerId=f.Actor.ToString()}),
            (HttpMethod.Post,"/api/mistakes/upload",new{sourceUploadId=source.Id,studentId=source.StudentId,subject=1,grade=7,imagePaths=source.ImagePaths.Select(i=>i.Path).ToArray()}),
            (HttpMethod.Post,"/api/mistakes",new{sourceUploadId=source.Id,studentId=source.StudentId,subject=1,grade=7,sourceRegions=new[]{new{sourceImagePath=source.ImagePaths[0].Path}}})};
        foreach(var attempt in attempts){using var response=await f.SendAsync(attempt.Method,attempt.Path,attempt.Body,f.Token());Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);}
        using var cross=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/{item.Id}",token:f.Token("user",sub:Guid.NewGuid().ToString()));Assert.Equal(HttpStatusCode.Forbidden,cross.StatusCode);
        using var anonView=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/intakes/{source.Id}/{source.ContentRevision}");Assert.Equal(HttpStatusCode.Unauthorized,anonView.StatusCode);
        using var userView=await f.SendAsync(HttpMethod.Get,$"/api/mistakes/intakes/{source.Id}/{source.ContentRevision}",token:f.Token("user"));Assert.Equal(HttpStatusCode.Forbidden,userView.StatusCode);
        foreach(var path in new[]{"uploads/arbitrary.jpg",source.ImagePaths[0].Path.ToUpperInvariant()})
        {using var response=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=path,size="full"},f.Token());Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);}
        using var thumbnail=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=source.ImagePaths[0].Path,size="small"},f.Token());Assert.Equal(HttpStatusCode.BadRequest,thumbnail.StatusCode);
        using var crossImage=await f.SendAsync(HttpMethod.Post,"/api/mistakes/presigned-url",new{objectPath=source.ImagePaths[0].Path,size="full"},f.Token("user",sub:Guid.NewGuid().ToString()));Assert.Equal(HttpStatusCode.Forbidden,crossImage.StatusCode);
        using var confirm=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/review",new{reviewStatus=2,reviewerId=f.Actor.ToString()},f.Token());Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        using var practice=await f.SendAsync(HttpMethod.Post,$"/api/mistakes/{item.Id}/practices",new{studentId=source.StudentId,isCorrect=true},f.Token("user"));Assert.Equal(HttpStatusCode.OK,practice.StatusCode);
        await using var db=f.Context();Assert.Single(await db.MistakePractices.ToListAsync());Assert.Equal(3,await db.ProcessLogs.CountAsync());Assert.Empty(f.SideEffects);await f.AssertRetainedAsync(source);
    }
    [Theory]
    [InlineData("missing_images")][InlineData("null_images")][InlineData("owner")][InlineData("revision")][InlineData("snapshot")][InlineData("hash")][InlineData("length")][InlineData("type")][InlineData("rotation")][InlineData("bad_json")]
    public async Task MalformedHttpsProviderManifestNeverRegistersBusinessClaimAsync(string kind)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();
        using var client=new HttpClient(new ResponseFault(kind){InnerHandler=new StudentServiceTokenHandler(f.Identity){InnerHandler=f.Source.CreateTrustedHandler()}}){BaseAddress=f.Source.Client!.BaseAddress};
        var reader=new SourceSnapshotClient(client,f.Identity);
        await Assert.ThrowsAsync<SourceIntakeException>(()=>reader.AcquireAsync(source.Id,source.ContentRevision,CancellationToken.None));
        await using var db=f.Context();Assert.Empty(await db.SourceIntakes.ToListAsync());Assert.Empty(f.Reads);Assert.Empty(f.SideEffects);Assert.Empty(f.Source.Deletes);
    }
    [Fact]
    public async Task RealCommittedPutResponseLossIsResolvedOnlyByAuthenticatedExactGetAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();
        var fault=new ResponseFault("lost_put"){InnerHandler=new StudentServiceTokenHandler(f.Identity){InnerHandler=f.Source.CreateTrustedHandler()}};
        using var client=new HttpClient(fault){BaseAddress=f.Source.Client!.BaseAddress};var reader=new SourceSnapshotClient(client,f.Identity);
        var manifest=await reader.AcquireAsync(source.Id,source.ContentRevision,CancellationToken.None);Assert.Equal(source.Id,manifest.SourceUploadId);Assert.Equal(2,fault.Calls);
        var same=await f.Snapshots.AcquireAsync(source.Id,source.ContentRevision,CancellationToken.None);Assert.Equal(manifest.SnapshotId,same.SnapshotId);
        using var missingScope=await f.Source.SendAsync(HttpMethod.Get,f.Source.Route(source),scope:"uploads.scan");Assert.Equal(HttpStatusCode.Forbidden,missingScope.StatusCode);
        await using var db=f.Context();Assert.Empty(await db.SourceIntakes.ToListAsync());Assert.Empty(f.SideEffects);
    }
    [Fact]
    public async Task HeartbeatOwnerLossCancelsLateResultAndPreservesManifestAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeRead=async _=>{entered.SetResult();await release.Task;};var pending=f.Service().CreateAutomaticAsync(source.Id,source.ContentRevision);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.SqlAsync("UPDATE source_intake SET lease_until=clock_timestamp()-interval '1 second'");
        await f.SqlAsync("UPDATE source_intake SET owner_id=gen_random_uuid(),fence=fence+1,lease_until=clock_timestamp()+interval '60 seconds'");
        await Task.Delay(TimeSpan.FromSeconds(21));release.SetResult();var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>pending);Assert.Equal("lease_lost",error.ErrorKind);
        await using var db=f.Context();Assert.Empty(await db.MistakeItems.ToListAsync());Assert.Empty(await db.ProcessLogs.ToListAsync());Assert.Single(await db.SourceIntakes.ToListAsync());await f.AssertRetainedAsync(source);
    }
    [Fact]
    public async Task OriginalSqlInsertWinsSameCanonicalUuidLockAndBlocksNewClaimAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();var source=await f.SeedAsync();
        await using var connection=new NpgsqlConnection(f.Connection);await connection.OpenAsync();await using var transaction=await connection.BeginTransactionAsync();
        await using var command=new NpgsqlCommand("INSERT INTO mistake_item(id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES(gen_random_uuid(),@student,1,7,@source,'[]',1,now())",connection,transaction);
        command.Parameters.AddWithValue("student",source.StudentId);command.Parameters.AddWithValue("source",source.Id.ToString("D").ToUpperInvariant());await command.ExecuteNonQueryAsync();
        var claim=f.ManualAsync(source);await Task.Delay(150);Assert.False(claim.IsCompleted);await transaction.CommitAsync();
        var error=await Assert.ThrowsAsync<SourceIntakeException>(()=>claim);Assert.Equal("legacy_source_conflict",error.ErrorKind);Assert.Single(error.ConflictingItemIds!);
        await using var db=f.Context();Assert.Single(await db.MistakeItems.ToListAsync());Assert.Empty(await db.SourceIntakes.ToListAsync());Assert.Empty(f.SideEffects);
    }
    [Theory]
    [InlineData("CREATE FUNCTION unrelated() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    [InlineData("ALTER TABLE source_intake ADD COLUMN surprise text")]
    [InlineData("DROP TRIGGER trg_managed_item ON mistake_item")]
    [InlineData("ALTER TABLE source_intake_group ALTER CONSTRAINT \"FK_source_intake_group_item\" NOT DEFERRABLE")]
    [InlineData("CREATE OR REPLACE FUNCTION guard_intake_truncate() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NULL; END'")]
    public async Task StartupExactNewSchemaRejectsUnknownCatalogWithoutRepairAsync(string mutation)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();await f.SqlAsync(mutation);
        await using var db=f.Context();await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));Assert.Equal(6,(await db.Database.GetAppliedMigrationsAsync()).Count());
    }
    [Fact]
    public async Task EmptyIntakeDownAndKnownFiveHistoryUpgradeAreControlledAndRepeatableAsync()
    {
        await using var f=await SourceIntakeFixture.CreateAsync();await using var db=f.Context();await db.GetService<IMigrator>().MigrateAsync("20260930000001_LeaseReanalyzeJobs");Assert.Equal(5,(await db.Database.GetAppliedMigrationsAsync()).Count());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));
        await Task.WhenAll(UpgradeAsync(),UpgradeAsync());await DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance);Assert.Equal(6,(await db.Database.GetAppliedMigrationsAsync()).Count());
        async Task UpgradeAsync(){await using var fresh=f.Context();await DatabaseInitializer.UpgradeKnownAsync(fresh,f.Name,true,true);}
    }
    private sealed class ResponseFault(string kind):DelegatingHandler
    {
        internal int Calls {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;var response=await base.SendAsync(request,ct);
            if(kind=="lost_put" && request.Method==HttpMethod.Put){response.Dispose();throw new HttpRequestException("Injected response loss after committed PUT.");}
            var root=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            switch(kind){
                case "missing_images":root.AsObject().Remove("images");break;
                case "null_images":root["images"]=null;break;
                case "owner":root["acquiredBy"]="other";break;
                case "revision":root["contentRevision"]=Guid.NewGuid();break;
                case "snapshot":root["snapshotId"]=Guid.Empty;break;
                case "hash":root["images"]![0]!["sha256"]="invalid";break;
                case "length":root["images"]![0]!["length"]=-1;break;
                case "type":root["images"]![0]!["type"]="Mistake";break;
                case "rotation":root.AsObject().Remove("imageRotations");break;
            }
            response.Content=new StringContent(kind=="bad_json" ? "invalid" : root.ToJsonString(),System.Text.Encoding.UTF8,"application/json");return response;
        }
    }
}
