using Emenda.Common.Database;
using System.Data.Common;

namespace Emenda.Database;

internal static class SourceIntakeCatalog
{
    internal static readonly string[] Tables = ["source_intake", "source_intake_group", "source_intake_request"];
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

    internal static async Task<string?> VerifyRoutinesAsync(DbConnection connection, SchemaVersion schema, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT p.proname,p.prosrc,l.lanname,p.prorettype::regtype::text,p.pronargs,p.prosecdef,p.provolatile,p.proconfig IS NULL,pg_get_function_identity_arguments(p.oid)
            FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang WHERE p.pronamespace='public'::regnamespace
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var bodies = schema.ManagedIntake
            ? new Dictionary<string, string>(SourceIntakeSchema.Bodies)
            : new Dictionary<string, string>();
        if (schema.Targets)
            foreach (var pair in SourceTargetSchema.Bodies) bodies.Add(pair.Key, pair.Value);
        if (schema.Returns)
        {
            bodies["guard_managed_item"] = SourceReturnSchema.ManagedItemBody;
            foreach (var pair in SourceReturnSchema.Bodies) bodies.Add(pair.Key, pair.Value);
        }
        if (schema.Handoffs)
            foreach (var pair in SourceHandoffSchema.Bodies) bodies[pair.Key] = pair.Value;
        if (schema.StorageReferences) bodies.Add("guard_storage_reference_snapshot", RetiredStorageReferenceSchema.GuardBody);
        if (schema.Archive) bodies.Add("guard_legacy_archive", LegacyArchiveSchema.GuardBody);
        if (schema.LegacyReturns)
            foreach (var pair in LegacyReturnSchema.Bodies) bodies.Add(pair.Key, pair.Value);
        if (schema.LayoutJobs)
            foreach (var pair in LayoutJobSchema.Bodies) bodies.Add(pair.Key, pair.Value);
        if (schema.Extractions)
            foreach (var pair in ExtractionSchema.Bodies) bodies.Add(pair.Key, pair.Value);
        if (schema.Corrections)
        {
            bodies["guard_extraction_revision"] = ExtractionCorrectionsSchema.RevisionGuard;
            bodies["guard_extraction_object_reference"] = ExtractionObjectReferenceSchema.Guard;
        }
        if (schema.Deletion)
        {
            bodies["guard_extraction_revision"] = ExtractionDeletionSchema.RevisionGuard;
            bodies["guard_extraction_retention"] = schema.ReleasableQuestionBinding ? ReleaseQuestionBinding.Guard : ExtractionDeletionSchema.RetentionGuard;
            bodies["guard_extraction_object_reference"] = schema.RetiredPractice ? PracticeRemovalSchema.ItemGuard : ExtractionDeletionSchema.ReferenceGuard;
            bodies.Add("guard_extraction_deletion_receipt", ExtractionDeletionSchema.ReceiptGuard);
        }
        if (schema.UnifiedSourceTrigger) bodies.Add("derive_mistake_item_source", UnifiedMistakeSchema.Body);
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var signature = RoutineSignature(name, schema);
            if (!found.Add(name) || !bodies.TryGetValue(name, out var body)
                || reader.GetString(1).Trim() != body.Trim()
                || reader.GetString(2) != "plpgsql" || reader.GetString(3) != signature.Return
                || reader.GetInt16(4) != signature.Count || reader.GetBoolean(5)
                || reader.GetChar(6) != signature.Volatile || !reader.GetBoolean(7)
                || reader.GetString(8) != signature.Args)
                return "unexpected source intake routine definition.";
        }
        return found.SetEquals(bodies.Keys) ? null : "missing source intake routine.";
    }

    private static (string Args, string Return, char Volatile, int Count) RoutineSignature(string name, SchemaVersion schema)
    {
        if (schema.Handoffs && SourceHandoffSchema.Bodies.ContainsKey(name))
            return SourceHandoffSchema.Signature(name);

        return name switch
        {
            "intake_source_key" => ("input_source text", "text", 'i', 1),
            "reserved_target_path" => ("input_path text", "boolean", 'i', 1),
            "return_payload_digest" => ("input_student text, input_payload text", "text", 'i', 2),
            _ => ("", "trigger", 'v', 0)
        };
    }

    internal static async Task<string?> VerifyTriggersAsync(DbConnection connection, SchemaVersion schema, CancellationToken ct)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ManagedIntake) expected.UnionWith([
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
        ]);
        if (schema.Targets) expected.UnionWith(SourceTargetSchema.Triggers);
        if (schema.Returns) expected.UnionWith(SourceReturnSchema.Triggers);
        if (schema.Handoffs) expected.UnionWith(SourceHandoffCatalog.Triggers);
        if (schema.StorageReferences) expected.UnionWith(RetiredStorageReferenceSchema.Triggers);
        if (schema.Archive) expected.UnionWith(LegacyArchiveSchema.Triggers);
        if (schema.LegacyReturns) expected.UnionWith(LegacyReturnSchema.Triggers);
        if (schema.LayoutJobs) expected.UnionWith(LayoutJobSchema.Triggers);
        if (schema.Corrections) expected.UnionWith(ExtractionObjectReferenceSchema.Triggers);
        if (schema.RetiredPractice) expected.RemoveWhere(trigger => trigger.StartsWith("mistake_practice|", StringComparison.Ordinal));
        if (schema.Extractions) expected.UnionWith(ExtractionSchema.Triggers);
        if (schema.Deletion)
        {
            expected.Remove("extraction_job|trg_extraction_job_delete|10|guard_extraction_retention|False|False");
            expected.UnionWith(ExtractionDeletionSchema.Triggers);
        }
        if (schema.UnifiedSourceTrigger) expected.Add(UnifiedMistakeSchema.Trigger);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT c.relname,t.tgname,t.tgtype,p.proname,t.tgdeferrable,t.tginitdeferred,t.tgnargs,t.tgenabled,t.tgqual IS NULL
            FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_proc p ON p.oid=t.tgfoid
            WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetInt16(6) != 0 || reader.GetChar(7) != 'O' || !reader.GetBoolean(8)) return "unexpected source intake trigger condition.";
            found.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt16(2)}|{reader.GetString(3)}|{reader.GetBoolean(4)}|{reader.GetBoolean(5)}");
        }
        if (!found.SetEquals(expected)) return "unexpected source intake trigger definition.";
        // Restrictive references must actually remain deferred, otherwise atomic map-before-item is not the same schema.
        await reader.DisposeAsync();
        command.CommandText = """
            SELECT conname,condeferrable,condeferred,confupdtype::text FROM pg_constraint
            WHERE connamespace='public'::regnamespace AND (conname LIKE 'FK_source_intake%' OR conname LIKE 'FK_source_target%' OR conname LIKE 'FK_source_return%' OR conname LIKE 'FK_source_handoff%' OR conname LIKE 'FK_legacy_return%' OR conname LIKE 'FK_extraction%')
            """;
        await using var constraints = await command.ExecuteReaderAsync(ct);
        while (await constraints.ReadAsync(ct))
        {
            var deferred = constraints.GetString(0) is "FK_source_intake_group_item" or "FK_source_intake_group_created_log" or "FK_source_return_log" or "FK_legacy_return_log";
            if (constraints.GetBoolean(1) != deferred || constraints.GetBoolean(2) != deferred || constraints.GetString(3) != "a")
                return "unexpected source intake reference definition.";
        }
        return null;
    }
}
