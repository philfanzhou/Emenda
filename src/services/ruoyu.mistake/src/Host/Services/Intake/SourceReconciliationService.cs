using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

/// <summary>
/// Read-only reconciliation of the mistake reference set against the persisted
/// managed-chain state and the actual OSS bytes. The whole database state is
/// computed inside one REPEATABLE READ READ ONLY PostgreSQL transaction; every referenced key is
/// verified by an actual download, never by an existence listing. The report
/// never writes anything and never advances any worker state.
/// </summary>
public sealed class SourceReconciliationService(DbContextOptions<MistakeDbContext> databaseOptions,IOssService oss) : ISourceReconciliationService
{
    private const int EntryLimit = 100;
    private const int ReferenceOriginLimit = 25;

    public async Task<ReconciliationReport> ReportAsync(Guid? sourceUploadId,CancellationToken ct=default)
    {
        try { return await ComputeAsync(sourceUploadId,ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(SourceIntakeException) { throw; }
        catch(Exception error) { throw new SourceIntakeException("reconciliation_unavailable",503,error); }
    }

    private sealed record ItemRow(Guid Id,Guid Source,string SourceText,string[] RegionKeys);
    private sealed record PracticeRow(Guid Id,Guid ItemId,Guid Source,string SourceText,string[] Keys);
    private sealed record IntakeRow(Guid Source,Guid Revision,bool Materialized,HashSet<string> ManifestPaths);

    private async Task<ReconciliationReport> ComputeAsync(Guid? scope,CancellationToken ct)
    {
        // A non-relational swap (test pipeline) has no reconciliation authority.
        await using var probe=new MistakeDbContext(databaseOptions);
        var connectionString=probe.Database.GetConnectionString() ?? throw new SourceIntakeException("reconciliation_unavailable",503);
        await using var connection=new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        // READ ONLY must be set before the first query of the transaction.
        await using(var mark=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction)) await mark.ExecuteNonQueryAsync(ct);
        DateTime clock;
        await using(var clockCommand=new NpgsqlCommand("SELECT clock_timestamp()",connection,transaction))
            clock=DateTime.SpecifyKind((DateTime)(await clockCommand.ExecuteScalarAsync(ct))!,DateTimeKind.Utc);
        var generated=new DateTimeOffset(clock);
        if(scope.HasValue && !(bool)(await ScalarAsync(connection,transaction,
            "SELECT EXISTS(SELECT 1 FROM source_intake WHERE source_upload_id=@source) OR EXISTS(SELECT 1 FROM mistake_item WHERE intake_source_key(source_upload_id)=@source::text)",scope.Value,ct))!)
            throw new SourceIntakeException("source_not_found",404);

        var references=new Dictionary<string,List<ReconciliationReferenceOrigin>>(StringComparer.Ordinal);
        var expectations=new Dictionary<string,HashSet<(long Length,string Sha256)>>(StringComparer.Ordinal);
        void Reference(string table,string row,string column,IEnumerable<string> keys)
        {
            foreach(var key in keys)
            {
                if(!references.TryGetValue(key,out var origins)) references[key]=origins=[];
                origins.Add(new(table,row,column));
            }
        }
        void Expect(string key,long length,string sha)
        {
            if(!expectations.TryGetValue(key,out var set)) expectations[key]=set=new HashSet<(long,string)>();
            set.Add((length,sha));
        }
        static string[] Parse(string format,string value)=>ReferencePaths.Parse(format,value);
        long? Age(DateTimeOffset? since)=>since.HasValue ? (long)(generated-since.Value).TotalSeconds : null;

        var items=new List<ItemRow>();
        await RowsAsync(connection,transaction,"SELECT id,source_upload_id,source_regions::text FROM mistake_item",
            " WHERE intake_source_key(source_upload_id)=@source::text",scope,ct,reader =>
        {
            var id=reader.GetGuid(0);var text=reader.GetString(1);
            var keys=Parse("regions",reader.GetString(2));
            items.Add(new(id,Guid.TryParse(text,out var parsed)?parsed:Guid.Empty,text,keys));
            Reference("mistake_item",id.ToString("D"),"source_regions",keys);
        });
        var practices=new List<PracticeRow>();
        await RowsAsync(connection,transaction,"SELECT p.id,p.mistake_item_id,i.source_upload_id,p.image_paths::text FROM mistake_practice p JOIN mistake_item i ON i.id=p.mistake_item_id",
            " WHERE intake_source_key(i.source_upload_id)=@source::text",scope,ct,reader =>
        {
            var id=reader.GetGuid(0);var text=reader.GetString(2);
            var keys=Parse("paths",reader.GetString(3));
            practices.Add(new(id,reader.GetGuid(1),Guid.TryParse(text,out var parsed)?parsed:Guid.Empty,text,keys));
            Reference("mistake_practice",id.ToString("D"),"image_paths",keys);
        });
        var intakes=new Dictionary<(Guid Source,Guid Revision),IntakeRow>();
        var intakeSources=new HashSet<string>(StringComparer.Ordinal);
        await RowsAsync(connection,transaction,"SELECT source_upload_id,content_revision,manifest,materialized FROM source_intake",
            " WHERE source_upload_id=@source",scope,ct,reader =>
        {
            var source=reader.GetGuid(0);var revision=reader.GetGuid(1);var manifest=reader.GetString(2);
            Reference("source_intake",$"{source:D}/{revision:D}","manifest",Parse("manifest",manifest));
            var images=(JsonSerializer.Deserialize<SourceManifest>(manifest,IntakeCanonical.Json) ?? throw new SourceIntakeException("reconciliation_unavailable",503)).Images;
            foreach(var image in images) Expect(image.Path,image.Length,image.Sha256);
            intakes[(source,revision)]=new(source,revision,reader.GetBoolean(3),new HashSet<string>(images.Select(image=>image.Path),StringComparer.Ordinal));
            intakeSources.Add(source.ToString("D"));
        });
        var groups=new Dictionary<Guid,(Guid Source,Guid Revision)>();
        await RowsAsync(connection,transaction,"SELECT item_id,source_upload_id,content_revision FROM source_intake_group",
            " WHERE source_upload_id=@source",scope,ct,reader => groups[reader.GetGuid(0)]=(reader.GetGuid(1),reader.GetGuid(2)));
        await RowsAsync(connection,transaction,"SELECT source_upload_id,content_revision,source_path,expected_length,expected_sha256 FROM source_target_object",
            " WHERE source_upload_id=@source",scope,ct,reader =>
        {
            var source=reader.GetGuid(0);var revision=reader.GetGuid(1);var path=reader.GetString(2);
            Reference("source_target_object",$"{source:D}/{revision:D}/{path}","source_path",[path]);
            Expect(path,reader.GetInt64(3),reader.GetString(4));
        });
        var pendingAttempts=new List<(Guid Source,Guid Revision,string Phase)>();
        await RowsAsync(connection,transaction,"SELECT source_upload_id,content_revision,target_key,phase,verified_length,verified_sha256 FROM source_target_attempt",
            " WHERE source_upload_id=@source",scope,ct,reader =>
        {
            var source=reader.GetGuid(0);var revision=reader.GetGuid(1);var key=reader.GetString(2);var phase=reader.GetString(3);
            Reference("source_target_attempt",$"{source:D}/{revision:D}/{key}","target_key",[key]);
            if(!reader.IsDBNull(5)) Expect(key,reader.GetInt64(4),reader.GetString(5));
            if(phase is "Reserved" or "Unknown" or "Copied") pendingAttempts.Add((source,revision,phase));
        });
        var selectedTargets=new Dictionary<Guid,HashSet<string>>();
        await RowsAsync(connection,transaction,"SELECT o.source_upload_id,a.target_key FROM source_target_object o JOIN source_target_attempt a ON a.attempt_id=o.selected_attempt_id WHERE a.phase='Verified'",
            " AND o.source_upload_id=@source",scope,ct,reader =>
        {
            var source=reader.GetGuid(0);
            if(!selectedTargets.TryGetValue(source,out var keys)) selectedTargets[source]=keys=new HashSet<string>(StringComparer.Ordinal);
            keys.Add(reader.GetString(1));
        });
        var conflicts=new List<ReconciliationConflict>();
        var pending=new List<ReconciliationPending>();
        await RowsAsync(connection,transaction,"SELECT source_upload_id,content_revision,operation_id,phase,error_kind,sealed_at,targets FROM source_handoff",
            " WHERE source_upload_id=@source",scope,ct,reader =>
        {
            var source=reader.GetGuid(0);var revision=reader.GetGuid(1);
            Reference("source_handoff",$"{source:D}/{revision:D}","targets",Parse("targets",reader.GetString(6)));
            foreach(var target in JsonSerializer.Deserialize<SourceHandoffTarget[]>(reader.GetString(6),IntakeCanonical.Json) ?? throw new SourceIntakeException("reconciliation_unavailable",503))
            { Expect(target.SourcePath,target.Length,target.Sha256); Expect(target.TargetKey,target.Length,target.Sha256); }
            var phase=reader.GetString(3);var sealedAt=new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5),DateTimeKind.Utc));
            if(phase=="Conflict") conflicts.Add(new("handoff",source,revision,null,reader.GetString(2),reader.IsDBNull(4)?null:reader.GetString(4),sealedAt));
            else if(phase is "Prepared" or "Sending") pending.Add(new("handoff_"+phase.ToLowerInvariant(),source,revision,null,phase,sealedAt,Age(sealedAt)));
        });
        await RowsAsync(connection,transaction,"SELECT item_id,operation_id,source_upload_id,content_revision,phase,error_kind,original_item,payload FROM source_return",
            " WHERE source_upload_id=@source",scope,ct,reader =>
        {
            var item=reader.GetGuid(0);var source=reader.GetGuid(2);var revision=reader.GetGuid(3);
            Reference("source_return",item.ToString("D"),"original_item",Parse("original",reader.GetString(6)));
            if(!reader.IsDBNull(7)) Reference("source_return",item.ToString("D"),"payload",Parse("returned",reader.GetString(7)));
            var phase=reader.GetString(4);
            if(phase=="Conflict") conflicts.Add(new("return",source,revision,item,reader.GetString(1),reader.IsDBNull(5)?null:reader.GetString(5),null));
            else if(phase is "Prepared" or "Sending") pending.Add(new("return_"+phase.ToLowerInvariant(),source,revision,item,phase,null,null));
        });
        await RowsAsync(connection,transaction,"SELECT o.item_id,o.position,o.source_path,o.target_key,o.expected_length,o.expected_sha256 FROM source_return_object o JOIN source_return r ON r.item_id=o.item_id",
            " WHERE r.source_upload_id=@source",scope,ct,reader =>
        {
            var row=$"{reader.GetGuid(0):D}/{reader.GetInt32(1)}";
            Reference("source_return_object",row,"source_path",[reader.GetString(2)]);
            Reference("source_return_object",row,"target_key",[reader.GetString(3)]);
            Expect(reader.GetString(2),reader.GetInt64(4),reader.GetString(5));
            Expect(reader.GetString(3),reader.GetInt64(4),reader.GetString(5));
        });
        await RowsAsync(connection,transaction,"SELECT a.attempt_id,a.object_key FROM source_return_attempt a JOIN source_return r ON r.item_id=a.item_id",
            " WHERE r.source_upload_id=@source",scope,ct,reader => Reference("source_return_attempt",reader.GetGuid(0).ToString("D"),"object_key",[reader.GetString(1)]));
        foreach(var intake in intakes.Values.Where(intake=>!intake.Materialized).OrderBy(intake=>intake.Source).ThenBy(intake=>intake.Revision))
            pending.Add(new("intake_not_materialized",intake.Source,intake.Revision,null,"NotMaterialized",null,null));
        foreach(var attempt in pendingAttempts.OrderBy(attempt=>attempt.Source).ThenBy(attempt=>attempt.Revision))
            pending.Add(new("target_attempt_"+attempt.Phase.ToLowerInvariant(),attempt.Source,attempt.Revision,null,attempt.Phase,null,null));

        var unmanaged=new List<ReconciliationUnmanaged>();
        foreach(var item in items.OrderBy(item=>item.Id))
        {
            if(groups.TryGetValue(item.Id,out var group) && group.Source==item.Source)
            {
                var manifest=intakes.GetValueOrDefault((group.Source,group.Revision))?.ManifestPaths ?? [];
                var targets=item.Source==Guid.Empty ? [] : selectedTargets.GetValueOrDefault(item.Source) ?? [];
                foreach(var key in item.RegionKeys.Where(key=>!manifest.Contains(key) && !targets.Contains(key)).Order(StringComparer.Ordinal))
                    unmanaged.Add(new("region_unattributable",item.Id.ToString("D"),item.SourceText,[key]));
            }
            else unmanaged.Add(new(intakeSources.Contains(item.Source==Guid.Empty ? item.SourceText : item.Source.ToString("D")) ? "item_outside_managed_group" : "item_without_intake",
                item.Id.ToString("D"),item.SourceText,[.. item.RegionKeys.Order(StringComparer.Ordinal)]));
        }
        foreach(var practice in practices.OrderBy(practice=>practice.Id))
            if(!groups.TryGetValue(practice.ItemId,out var group) || group.Source!=practice.Source)
                unmanaged.Add(new("practice_without_managed_item",practice.Id.ToString("D"),practice.SourceText,[.. practice.Keys.Order(StringComparer.Ordinal)]));

        // The database transaction is read-only and is never committed.
        await transaction.RollbackAsync(ct);

        var dangling=new List<ReconciliationDanglingReference>();
        foreach(var key in references.Keys.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                using var stream=await oss.DownloadAsync(key);
                using var memory=new MemoryStream();
                await stream.CopyToAsync(memory,ct);
                bytes=memory.ToArray();
            }
            catch(Exception error) when(error is not OperationCanceledException)
            {
                if(!IsMissingObject(error)) throw new SourceIntakeException("reconciliation_unavailable",503,error);
                dangling.Add(new(key,"missing",null,null,references[key].Count>ReferenceOriginLimit,Origins(references[key])));
                continue;
            }
            if(expectations.TryGetValue(key,out var expected))
            {
                var digest=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if(!expected.All(entry=>bytes.LongLength==entry.Length && digest==entry.Sha256))
                {
                    // The judgment covers every persisted expectation, but the displayed
                    // representative is the deterministic minimum (length, then ordinal
                    // sha256) so the same persisted state always reports the same value.
                    var representative=expected.OrderBy(entry=>entry.Length).ThenBy(entry=>entry.Sha256,StringComparer.Ordinal).First();
                    dangling.Add(new(key,"bytes_conflict",representative.Length,representative.Sha256,references[key].Count>ReferenceOriginLimit,Origins(references[key])));
                }
            }
        }

        conflicts.Sort((left,right)=>string.CompareOrdinal(left.OperationId,right.OperationId));
        pending.Sort((left,right)=>string.CompareOrdinal(left.Kind,right.Kind)!=0 ? string.CompareOrdinal(left.Kind,right.Kind)
            : left.SourceUploadId!.Value.CompareTo(right.SourceUploadId!.Value));
        unmanaged.Sort((left,right)=>string.CompareOrdinal(left.Row,right.Row)!=0 ? string.CompareOrdinal(left.Row,right.Row)
            : string.CompareOrdinal(left.Kind,right.Kind));
        var preview=new ReconciliationRepairPreview("unknown",unmanaged.Count,unmanaged.Count==0 ? [] :
            ["Trusted legacy input covering the listed unmanaged records and keys (for example Student-side snapshot provider data), or an explicit owner decision that they are unrecoverable"]);
        return new(generated,new(scope,scope.HasValue,references.Count),Cap(dangling),Cap(conflicts),Cap(pending),Cap(unmanaged),preview);
    }

    private static List<ReconciliationReferenceOrigin> Origins(List<ReconciliationReferenceOrigin> origins)=>[.. origins
        .Distinct().OrderBy(origin=>origin.Table).ThenBy(origin=>origin.Row).ThenBy(origin=>origin.Column).Take(ReferenceOriginLimit)];
    private static ReconciliationCategory<TEntry> Cap<TEntry>(List<TEntry> entries)=>new(entries.Count,entries.Count>EntryLimit,entries.Take(EntryLimit).ToList());

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,Guid source,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand(sql,connection,transaction);
        command.Parameters.AddWithValue("source",source);
        return await command.ExecuteScalarAsync(ct);
    }
    private static async Task RowsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,string filter,Guid? scope,CancellationToken ct,Action<NpgsqlDataReader> read)
    {
        // The filter fragment is only appended when its parameter is actually bound.
        await using var command=new NpgsqlCommand(scope.HasValue?sql+filter:sql,connection,transaction);
        if(scope.HasValue) command.Parameters.AddWithValue("source",scope.Value);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) read(reader);
    }
    private static bool IsMissingObject(Exception error)
    {
        for(var current=(Exception?)error;current!=null;current=current.InnerException)
        {
            if(current is FileNotFoundException) return true;
            if(current.GetType().Name is "ObjectNotFoundException" or "NoSuchKeyException" or "ResourceNotFoundException") return true;
        }
        return false;
    }
}
