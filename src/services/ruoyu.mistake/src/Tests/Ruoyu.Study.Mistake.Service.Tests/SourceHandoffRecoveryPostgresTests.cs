using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Service.Intake;
using Npgsql;
namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceHandoffRecoveryPostgresTests
{
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)]
    public async Task EveryExactKnownSchemaUpgradesExplicitlyToNineAndReadOnlyCatalogRejectsCorruptionAsync(int version)
    {
        await using var f=await SourceIntakeFixture.CreateAsync(initialize:false);await using var db=f.Context();
        var target=db.Database.GetMigrations().Take(version).Last();await db.GetService<IMigrator>().MigrateAsync(target);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));
        await DatabaseInitializer.UpgradeKnownAsync(db,f.Name,true,true);await DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance);
        Assert.Equal(9,(await db.Database.GetAppliedMigrationsAsync()).Count());
        await f.SqlAsync("ALTER TABLE source_handoff ADD COLUMN unrecognized text");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.InitializeAsync(db,NullLoggerFactory.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>DatabaseInitializer.UpgradeKnownAsync(db,f.Name,true,true));
    }
    [Fact]
    public async Task RealPgDumpRestoreAllHandoffPhasesAndPermanentStudentArchiveRetainsAndRecoversAsync()
    {
        await using var f=await SourceHandoffPostgresTests.FixtureAsync();var saved=new List<(Guid Source,Guid Revision,string Phase)>();
        foreach(var phase in new[]{"Prepared","Sending","Conflict","Completed"})
        {
            var record=await SourceHandoffPostgresTests.ReadyAsync(f,mixed:phase=="Completed");
            using var http=new HttpClient(new PhaseFault(phase){InnerHandler=new Ruoyu.Study.Mistake.Host.StudentServiceTokenHandler(f.Identity){InnerHandler=f.Source.CreateTrustedHandler()}}){BaseAddress=f.Source.Client!.BaseAddress};
            var service=new SourceIntakeService(f.DatabaseOptions,new(f.Enabled),f.Snapshots,f.Oss.Object,f.Analyzer.Object,Microsoft.Extensions.Options.Options.Create(new Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis.VlAnalyzerOptions()),new(f.TargetsEnabled),new(false),null,new(true),new(http,f.Identity));
            if(phase=="Prepared")f.BeforeRead=_=>throw new IOException("Injected pre-send target outage.");
            try{await service.HandoffAsync(record.Id,record.ContentRevision,f.Actor.ToString());}catch(SourceIntakeException){}f.BeforeRead=null;
            await using(var db=f.Context())Assert.Equal(phase,(await db.SourceHandoffs.SingleAsync(h=>h.SourceUploadId==record.Id)).Phase);
            saved.Add((record.Id,record.ContentRevision,phase));
        }
        await f.Source.SqlAsync("DELETE FROM image_upload_record WHERE id IN(SELECT source_upload_id FROM source_handoff_receipt)");
        static async Task<string> FactsAsync(DbContext db)=>await db.Database.SqlQueryRaw<string>("""
            SELECT string_agg(name||':'||rows,'|' ORDER BY name) AS "Value" FROM (
              SELECT table_name name,query_to_xml(format('SELECT * FROM %I ORDER BY to_jsonb(%I)::text',table_name,table_name),true,false,'')::text rows
              FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE') facts
            """).SingleAsync();
        await using var beforeM=f.Context();await using var beforeS=f.Source.Context();var m=await FactsAsync(beforeM);var s=await FactsAsync(beforeS);
        var dumpM=await f.BackupAsync();var dumpS=await f.Source.BackupAsync();
        await f.SqlAsync("DROP SCHEMA public CASCADE;CREATE SCHEMA public");await f.RestoreAsync(dumpM);
        await f.Source.SqlAsync("DROP SCHEMA public CASCADE;CREATE SCHEMA public");await f.Source.RestoreAsync(dumpS);
        await using var checkM=f.Context();await using var checkS=f.Source.Context();Assert.Equal(m,await FactsAsync(checkM));Assert.Equal(s,await FactsAsync(checkS));
        await DatabaseInitializer.InitializeAsync(checkM,NullLoggerFactory.Instance);await Ruoyu.Study.Student.Database.DatabaseInitializer.InitializeAsync(checkS,NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<PostgresException>(()=>checkM.GetService<IMigrator>().MigrateAsync("20261003010000_AddSourceReturns"));
        await Assert.ThrowsAsync<PostgresException>(()=>checkS.GetService<IMigrator>().MigrateAsync("20261002100000_AddUploadSourceSnapshot"));
        foreach(var entry in saved)
        {
            if(entry.Phase=="Conflict"){await Assert.ThrowsAsync<SourceIntakeException>(()=>f.Service().HandoffAsync(entry.Source,entry.Revision,f.Actor.ToString()));continue;}
            Assert.Equal("Completed",(await f.Service().HandoffAsync(entry.Source,entry.Revision,f.Actor.ToString())).State);
        }
        await using var final=f.Source.Context();Assert.Equal(4,await final.SourceHandoffReceipts.CountAsync());Assert.Empty(f.Source.Deletes);Assert.DoesNotContain("delete",f.SideEffects);
    }
    private sealed class PhaseFault(string phase):DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(phase=="Sending" && request.Method==HttpMethod.Put)return new(HttpStatusCode.ServiceUnavailable);
            var response=await base.SendAsync(request,ct);
            if(phase=="Conflict" && request.Method==HttpMethod.Get && response.IsSuccessStatusCode)
            {var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;json["snapshotId"]=Guid.NewGuid().ToString();response.Content=new StringContent(json.ToJsonString(),System.Text.Encoding.UTF8,"application/json");}
            return response;
        }
    }
}
