using Emenda.Common.Database;
using System.Data.Common;
namespace Emenda.Database;

internal static class SourceIntakeCatalog
{
    internal static readonly string[] Tables = ["source_intake","source_intake_group","source_intake_request"];
    internal static readonly string[] Columns = [
        "source_intake|source_upload_id|uuid|N", "source_intake|content_revision|uuid|N",
        "source_intake|snapshot_id|uuid|N", "source_intake|student_id|character varying(36)|N",
        "source_intake|manifest|text|N", "source_intake|mode|text|N", "source_intake|owner_id|uuid|N",
        "source_intake|fence|bigint|N", "source_intake|lease_until|timestamp with time zone|Y", "source_intake|materialized|boolean|N",
        "source_intake_group|item_id|uuid|N", "source_intake_group|source_upload_id|uuid|N", "source_intake_group|content_revision|uuid|N",
        "source_intake_group|group_hash|character varying(64)|N", "source_intake_group|canonical|text|N", "source_intake_group|atoms|text[]|N",
        "source_intake_group|regions|jsonb|N", "source_intake_group|payload|text|N", "source_intake_group|result|text|N", "source_intake_group|created_log_id|uuid|N",
        "source_intake_request|actor|text|N", "source_intake_request|action|text|N", "source_intake_request|request_key|uuid|N",
        "source_intake_request|source_upload_id|uuid|N", "source_intake_request|content_revision|uuid|N",
        "source_intake_request|canonical|text|N", "source_intake_request|item_ids|uuid[]|Y"];
    internal static readonly string[] Indexes = [
        "PK_source_intake|source_intake|source_upload_id,content_revision|U", "PK_source_intake_group|source_intake_group|item_id|U",
        "IX_source_intake_group_source_hash|source_intake_group|source_upload_id,content_revision,group_hash|N",
        "IX_source_intake_group_created_log_id|source_intake_group|created_log_id|N",
        "PK_source_intake_request|source_intake_request|actor,action,request_key|U",
        "IX_source_intake_request_source_revision|source_intake_request|source_upload_id,content_revision|N"];
    internal static readonly string[] Constraints = [
        "trg_intake_integrity_group|t||-|-|-|V", "trg_intake_integrity_log|t||-|-|-|V", "trg_intake_integrity_request|t||-|-|-|V",
        "PK_source_intake|p|source_upload_id,content_revision|-|-|-|V", "PK_source_intake_group|p|item_id|-|-|-|V",
        "FK_source_intake_group_source_intake|f|source_upload_id,content_revision|source_intake|source_upload_id,content_revision|r|V",
        "FK_source_intake_group_item|f|item_id|mistake_item|id|r|V",
        "FK_source_intake_group_created_log|f|created_log_id|process_log|id|r|V",
        "PK_source_intake_request|p|actor,action,request_key|-|-|-|V",
        "FK_source_intake_request_source_intake|f|source_upload_id,content_revision|source_intake|source_upload_id,content_revision|r|V"];
    internal static async Task<string?> VerifyRoutinesAsync(DbConnection connection,CancellationToken ct,bool targets=false,bool returns=false,bool handoffs=false,bool references=false,bool archive=false,bool legacyReturns=false,bool layout=false)
    {
        await using var command=connection.CreateCommand();
        command.CommandTimeout=0;
        command.CommandText="""
            SELECT p.proname,p.prosrc,l.lanname,p.prorettype::regtype::text,p.pronargs,p.prosecdef,p.provolatile,p.proconfig IS NULL,pg_get_function_identity_arguments(p.oid)
            FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang WHERE p.pronamespace='public'::regnamespace
            """;
        await using var reader=await command.ExecuteReaderAsync(ct);
        var bodies=new Dictionary<string,string>(SourceIntakeSchema.Bodies);
        if(targets) foreach(var pair in SourceTargetSchema.Bodies) bodies.Add(pair.Key,pair.Value);
        if(returns) { bodies["guard_managed_item"]=SourceReturnSchema.ManagedItemBody; foreach(var pair in SourceReturnSchema.Bodies) bodies.Add(pair.Key,pair.Value); }
        if(handoffs) foreach(var pair in SourceHandoffSchema.Bodies) bodies[pair.Key]=pair.Value;
        if(references) bodies.Add("guard_storage_reference_snapshot",RetiredStorageReferenceSchema.GuardBody);
        if(archive) bodies.Add("guard_legacy_archive",LegacyArchiveSchema.GuardBody);
        if(legacyReturns) foreach(var pair in LegacyReturnSchema.Bodies) bodies.Add(pair.Key,pair.Value);
        if(layout) foreach(var pair in LayoutJobSchema.Bodies) bodies.Add(pair.Key,pair.Value);
        var found=new HashSet<string>(StringComparer.Ordinal);
        while(await reader.ReadAsync(ct))
        {
            var name=reader.GetString(0);
            if (!found.Add(name) || !bodies.TryGetValue(name,out var body)
                || reader.GetString(1).Trim()!=body.Trim() || reader.GetString(2)!="plpgsql" || reader.GetString(3)!=(handoffs && SourceHandoffSchema.Bodies.ContainsKey(name) ? SourceHandoffSchema.Signature(name).Return : name is "intake_source_key" or "return_payload_digest" ? "text" : name=="reserved_target_path" ? "boolean" : "trigger")
                || reader.GetInt16(4)!=(handoffs && SourceHandoffSchema.Bodies.ContainsKey(name) ? SourceHandoffSchema.Signature(name).Count : name=="return_payload_digest" ? 2 : name is "intake_source_key" or "reserved_target_path" ? 1 : 0) || reader.GetBoolean(5) || reader.GetChar(6)!=(handoffs && SourceHandoffSchema.Bodies.ContainsKey(name) ? SourceHandoffSchema.Signature(name).Volatile : name is "intake_source_key" or "reserved_target_path" or "return_payload_digest" ? 'i' : 'v') || !reader.GetBoolean(7) || reader.GetString(8)!=(handoffs && SourceHandoffSchema.Bodies.ContainsKey(name) ? SourceHandoffSchema.Signature(name).Args : name=="intake_source_key" ? "input_source text" : name=="reserved_target_path" ? "input_path text" : name=="return_payload_digest" ? "input_student text, input_payload text" : ""))
                return "unexpected source intake routine definition.";
        }
        return found.SetEquals(bodies.Keys) ? null : "missing source intake routine.";
    }
    internal static async Task<string?> VerifyTriggersAsync(DbConnection connection,CancellationToken ct,bool targets=false,bool returns=false,bool handoffs=false,bool references=false,bool archive=false,bool legacyReturns=false,bool layout=false)
    {
        var expected=new HashSet<string>(StringComparer.Ordinal)
        {
            "source_intake|trg_source_intake|31|guard_source_intake|False|False",
            "mistake_item|trg_managed_item|31|guard_managed_item|False|False",
            "source_intake_group|trg_intake_group|31|guard_intake_group|False|False",
            "source_intake_request|trg_intake_request|31|guard_intake_request|False|False",
            "source_intake_group|trg_intake_integrity_group|5|guard_intake_integrity|True|True",
            "process_log|trg_intake_integrity_log|29|guard_intake_integrity|True|True",
            "process_log|trg_intake_created_log|27|guard_intake_created_log|False|False",
            "source_intake_request|trg_intake_integrity_request|21|guard_intake_integrity|True|True",
            "source_intake|trg_intake_truncate|34|guard_intake_truncate|False|False",
            "source_intake_group|trg_group_truncate|34|guard_intake_truncate|False|False",
            "source_intake_request|trg_request_truncate|34|guard_intake_truncate|False|False",
            "mistake_item|trg_item_truncate|34|guard_intake_truncate|False|False",
            "process_log|trg_log_truncate|34|guard_intake_truncate|False|False"
        };
        if(targets) expected.UnionWith(SourceTargetSchema.Triggers);
        if(returns) expected.UnionWith(SourceReturnSchema.Triggers);
        if(handoffs) expected.UnionWith(SourceHandoffCatalog.Triggers);
        if(references) expected.UnionWith(RetiredStorageReferenceSchema.Triggers);
        if(archive) expected.UnionWith(LegacyArchiveSchema.Triggers);
        if(legacyReturns) expected.UnionWith(LegacyReturnSchema.Triggers);
        if(layout) expected.UnionWith(LayoutJobSchema.Triggers);
        await using var command=connection.CreateCommand();
        command.CommandTimeout=0;
        command.CommandText="""
            SELECT c.relname,t.tgname,t.tgtype,p.proname,t.tgdeferrable,t.tginitdeferred,t.tgnargs,t.tgenabled,t.tgqual IS NULL
            FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_proc p ON p.oid=t.tgfoid
            WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal
            """;
        await using var reader=await command.ExecuteReaderAsync(ct);
        var found=new HashSet<string>(StringComparer.Ordinal);
        while(await reader.ReadAsync(ct))
        {
            if(reader.GetInt16(6)!=0 || reader.GetChar(7)!='O' || !reader.GetBoolean(8)) return "unexpected source intake trigger condition.";
            found.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt16(2)}|{reader.GetString(3)}|{reader.GetBoolean(4)}|{reader.GetBoolean(5)}");
        }
        if (!found.SetEquals(expected)) return "unexpected source intake trigger definition.";
        // Restrictive references must actually remain deferred, otherwise atomic map-before-item is not the same schema.
        await reader.DisposeAsync();
        command.CommandText="""
            SELECT conname,condeferrable,condeferred,confupdtype::text FROM pg_constraint
            WHERE connamespace='public'::regnamespace AND (conname LIKE 'FK_source_intake%' OR conname LIKE 'FK_source_target%' OR conname LIKE 'FK_source_return%' OR conname LIKE 'FK_source_handoff%' OR conname LIKE 'FK_legacy_return%')
            """;
        await using var constraints=await command.ExecuteReaderAsync(ct);
        while(await constraints.ReadAsync(ct))
        {
            var deferred=constraints.GetString(0) is "FK_source_intake_group_item" or "FK_source_intake_group_created_log" or "FK_source_return_log" or "FK_legacy_return_log";
            if(constraints.GetBoolean(1)!=deferred || constraints.GetBoolean(2)!=deferred || constraints.GetString(3)!="a")
                return "unexpected source intake reference definition.";
        }
        return null;
    }
}
