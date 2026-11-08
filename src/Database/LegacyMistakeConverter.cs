using System.Data.Common;

namespace Emenda.Database;

/// <summary>Converts the complete 2026-04-19 through 2026-05-13 old-table layout within the upgrade transaction.</summary>
internal static class LegacyMistakeConverter
{
    private static readonly Dictionary<string, string[]> Columns = new(StringComparer.Ordinal)
    {
        ["mistake"] = [
            "id|uuid|N", "student_id|character varying(36)|N", "subject|integer|N", "grade|integer|N",
            "date|timestamp with time zone|N", "type|integer|N", "question_id|character varying(36)|Y",
            "status|integer|N", "created_at|timestamp with time zone|N", "updated_at|timestamp with time zone|Y"],
        ["mistake_practice"] = [
            "id|uuid|N", "mistake_id|uuid|N", "student_id|character varying(36)|N",
            "practice_date|timestamp with time zone|N", "image_paths|text|N", "student_answer|text|Y",
            "is_correct|boolean|N", "comments|text|Y"],
        ["process_log"] = ["id|uuid|N", "mistake_id|uuid|N", "action|integer|N", "details|text|Y", "created_at|timestamp with time zone|N"],
        ["mistake_detail"] = ["id|uuid|N", "mistake_id|uuid|N", "original_images|text|N", "comments|text|Y"],
        ["mistake_image_fingerprint"] = [
            "id|uuid|N", "hash|character varying(64)|N", "student_id|character varying(36)|N",
            "mistake_id|uuid|N", "image_index|integer|N", "oss_path|text|N",
            "created_at|timestamp with time zone|N"],
    };

    internal static async Task<bool> IsSupportedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        foreach (var (table, expected) in Columns)
        {
            var actual = await ReadAsync(connection, $"""
                SELECT a.attname || '|' || format_type(a.atttypid,a.atttypmod) || '|' || CASE WHEN a.attnotnull THEN 'N' ELSE 'Y' END || '|' || a.attidentity::text || '|' || a.attgenerated::text
                FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
                WHERE c.oid=to_regclass('public.{table}') AND c.relkind='r' AND NOT c.relispartition AND NOT c.relrowsecurity
                    AND a.attnum>0 AND NOT a.attisdropped
                """, cancellationToken).ConfigureAwait(false);
            if (table == "mistake_image_fingerprint" && actual.Count == 0 &&
                (await ReadAsync(connection, "SELECT to_regclass('public.mistake_image_fingerprint')::text", cancellationToken).ConfigureAwait(false)).Count == 0)
                continue;
            if (!actual.SetEquals(expected.Select(column => column + "||"))) return false;
        }
        var attached = await ReadAsync(connection, """
            SELECT 'object' FROM pg_trigger WHERE tgrelid='public.mistake'::regclass AND NOT tgisinternal
            UNION ALL SELECT 'object' FROM pg_rewrite WHERE ev_class='public.mistake'::regclass
            UNION ALL SELECT 'object' FROM pg_policy WHERE polrelid='public.mistake'::regclass
            UNION ALL SELECT 'object' FROM pg_inherits WHERE inhrelid='public.mistake'::regclass OR inhparent='public.mistake'::regclass
            UNION ALL SELECT 'default' FROM pg_attrdef WHERE adrelid IN (to_regclass('public.mistake'),
                to_regclass('public.mistake_practice'),to_regclass('public.process_log'),to_regclass('public.mistake_detail'),
                to_regclass('public.mistake_image_fingerprint'))
            """, cancellationToken).ConfigureAwait(false);
        if (attached.Count > 0) return false;
        var indexes = await ReadAsync(connection, """
            SELECT idx.relname || '|' || CASE WHEN i.indisvalid AND i.indisready AND i.indnatts=i.indnkeyatts
                AND idx.relam=(SELECT oid FROM pg_am WHERE amname='btree') AND i.indpred IS NULL AND i.indexprs IS NULL
                THEN array_to_string(ARRAY(SELECT replace(pg_get_indexdef(i.indexrelid,n,true),'"','')
                    FROM generate_series(1,i.indnkeyatts) n), ',') ELSE '#other' END || '|' || CASE WHEN i.indisunique THEN 'U' ELSE 'N' END
            FROM pg_index i JOIN pg_class idx ON idx.oid=i.indexrelid WHERE i.indrelid='public.mistake'::regclass
            """, cancellationToken).ConfigureAwait(false);
        if (!indexes.SetEquals(["PK_mistake|id|U", "IX_mistake_created_at|created_at|N", "IX_mistake_date|date|N",
            "IX_mistake_status|status|N", "IX_mistake_student_id|student_id|N", "IX_mistake_type|type|N",
            "IX_mistake_student_id_subject_grade_status|student_id,subject,grade,status|N"])) return false;
        var keys = await ReadAsync(connection, """
            SELECT c.conname || '|' || t.relname || '|' || c.contype::text || '|' ||
                array_to_string(ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord), ',') || '|' ||
                CASE WHEN c.contype='f' THEN ns.nspname || '.' || ref.relname || '|' ||
                    array_to_string(ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY k(attnum,ord)
                        JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord), ',') || '|' ||
                    c.confdeltype::text || c.confupdtype::text || c.confmatchtype::text ELSE '-' END || '|' ||
                CASE WHEN c.convalidated AND NOT c.condeferrable THEN 'V' ELSE '#other' END
            FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid LEFT JOIN pg_class ref ON ref.oid=c.confrelid
                LEFT JOIN pg_namespace ns ON ns.oid=ref.relnamespace
            WHERE c.conrelid='public.mistake'::regclass OR c.confrelid='public.mistake'::regclass
            """, cancellationToken).ConfigureAwait(false);
        var allowed = Columns.Keys.Where(t => t != "mistake")
            .Select(t => $"FK_{t}_mistake_mistake_id|{t}|f|mistake_id|public.mistake|id|cas|V")
            .Append("PK_mistake|mistake|p|id|-|V").ToHashSet(StringComparer.Ordinal);
        if (!keys.Contains("PK_mistake|mistake|p|id|-|V") || !keys.IsSubsetOf(allowed)) return false;
        if ((await ReadAsync(connection, "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text", cancellationToken).ConfigureAwait(false)).Count == 0)
            return true;
        var history = await ReadAsync(connection, "SELECT \"MigrationId\" || '|' || \"ProductVersion\" FROM public.\"__EFMigrationsHistory\"", cancellationToken).ConfigureAwait(false);
        return history.SetEquals([]) || history.SetEquals([LegacySchemaNormalizer.LegacyInitial + "|8.0.4"]) ||
            history.SetEquals([LegacySchemaNormalizer.LegacyInitial + "|8.0.4", LegacySchemaNormalizer.LegacyRemoveFingerprint + "|8.0.4"]);
    }

    internal static async Task PrepareAsync(DbConnection connection, HashSet<string> relations, LegacyArchiveStage stage, CancellationToken cancellationToken)
    {
        var locked = Columns.Keys.Concat(["mistake_item", "__EFMigrationsHistory"]).Where(relations.Contains).Select(t => "public.\"" + t + "\"");
        await LegacyArchiveStage.ExecuteAsync(connection, $"LOCK TABLE {string.Join(',', locked)} IN ACCESS EXCLUSIVE MODE", cancellationToken).ConfigureAwait(false);
        // External DDL may have changed the catalog while table locks were being acquired.
        if (!await IsSupportedAsync(connection, cancellationToken).ConfigureAwait(false))
            throw new UpgradeStepException("Mistake legacy conversion structure changed while acquiring table locks.");
        var reason = relations.Contains("mistake_item")
            ? "CASE WHEN EXISTS (SELECT 1 FROM public.mistake_item i WHERE i.id=m.id) THEN 'legacy_mistake_superseded' ELSE 'legacy_mistake_converted' END"
            : "'legacy_mistake_converted'";
        await stage.AddAsync($"""
            SELECT 'mistake', m.id::text, 'row', to_jsonb(m), {reason} FROM public.mistake m ORDER BY m.id
            """, cancellationToken).ConfigureAwait(false);
        if (!relations.Contains("__EFMigrationsHistory"))
            await LegacyArchiveStage.ExecuteAsync(connection, """
                CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"))
                """, cancellationToken).ConfigureAwait(false);
        if (!relations.Contains("mistake_item"))
            await LegacyArchiveStage.ExecuteAsync(connection, """
                CREATE TABLE public.mistake_item (id uuid NOT NULL, student_id character varying(36) NOT NULL,
                    subject integer NOT NULL, grade integer NOT NULL, source_upload_id character varying(64) NOT NULL,
                    source_regions jsonb NOT NULL, review_status integer NOT NULL, reviewer_id character varying(36),
                    reviewed_at timestamp with time zone, review_comment text, question_id character varying(36),
                    created_at timestamp with time zone NOT NULL, updated_at timestamp with time zone,
                    CONSTRAINT "PK_mistake_item" PRIMARY KEY (id))
                """, cancellationToken).ConfigureAwait(false);
        foreach (var table in Columns.Keys.Where(t => t != "mistake" && relations.Contains(t)))
            await LegacyArchiveStage.ExecuteAsync(connection, $"ALTER TABLE public.{table} DROP CONSTRAINT IF EXISTS \"FK_{table}_mistake_mistake_id\"", cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ConvertAsync(DbConnection connection, LegacyArchiveStage stage, CancellationToken cancellationToken)
    {
        await LegacyArchiveStage.ExecuteAsync(connection, """
            INSERT INTO public.mistake_item (id,student_id,subject,grade,question_id,created_at,updated_at,review_status,source_upload_id,source_regions)
            SELECT id,student_id,subject,grade,question_id,created_at,updated_at,status,'','[]'::jsonb
            FROM public.mistake m WHERE NOT EXISTS (SELECT 1 FROM public.mistake_item i WHERE i.id=m.id)
            """, cancellationToken).ConfigureAwait(false);
        await stage.AddAsync("""
            SELECT 'process_log', id::text, 'column_values', jsonb_build_object('action',action), 'legacy_action_renumbered'
            FROM public.process_log WHERE action IN (3,4) ORDER BY id
            """, cancellationToken).ConfigureAwait(false);
        await LegacyArchiveStage.ExecuteAsync(connection, "UPDATE public.process_log SET action=CASE action WHEN 3 THEN 5 WHEN 4 THEN 6 END WHERE action IN (3,4)", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (!reader.IsDBNull(0)) rows.Add(reader.GetString(0));
        return rows;
    }
}
