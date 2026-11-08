using System.Data.Common;
using System.Text.Json;

namespace Emenda.Database;

/// <summary>
/// Converges structures left by the automatic schema maintenance of 2026-04-19 to 2026-09-29 builds into one of the
/// shapes <see cref="DatabaseInitializer"/> already classifies. It runs inside the upgrade transaction, stages every
/// removed or rewritten value first, and only handles the objects those builds are known to create. Anything else is
/// left for the classifier, which rejects it and rolls the whole transaction back.
/// </summary>
internal static class LegacySchemaNormalizer
{
    internal const string LegacyInitial = "20260501042151_InitialCreate";
    internal const string LegacyRemoveFingerprint = "20260503021905_RemoveImageFingerprint";

    private static readonly string[] LegacyItemColumns =
    [
        "type", "source_image_path", "bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2",
        "question_number", "question_text", "student_answer", "ai_confidence"
    ];

    private static readonly Dictionary<string, string[]> LegacyTables = new(StringComparer.Ordinal)
    {
        ["mistake_detail"] = ["id", "mistake_id", "original_images", "comments"],
        ["mistake_image_fingerprint"] = ["id", "hash", "student_id", "mistake_id", "image_index", "oss_path", "created_at"],
    };

    private static readonly HashSet<string> LegacyTableObjects = new(StringComparer.Ordinal)
    {
        "PK_mistake_detail", "IX_mistake_detail_mistake_id", "FK_mistake_detail_mistake_mistake_id",
        "PK_mistake_image_fingerprint", "IX_mistake_image_fingerprint_hash_student_id",
        "IX_mistake_image_fingerprint_mistake_id", "IX_mistake_image_fingerprint_oss_path", "FK_mistake_image_fingerprint_mistake_mistake_id",
    };

    private static readonly HashSet<string> LegacyConstraints = new(StringComparer.Ordinal)
    {
        "PK_mistake", "pk_mistake_item",
        "fk_process_log_mistake_item_mistake_item_id", "fk_mistake_practice_mistake_item_mistake_item_id",
    };

    /// <summary>Target index, its table and key columns, and the known legacy names with the same definition.</summary>
    private static readonly (string Name, string Table, string Columns, string[] Legacy)[] CoreIndexes =
    [
        ("IX_mistake_item_created_at", "mistake_item", "created_at", ["ix_mistake_item_created_at", "IX_mistake_created_at"]),
        ("IX_mistake_item_review_status", "mistake_item", "review_status", ["ix_mistake_item_review_status", "IX_mistake_status"]),
        ("IX_mistake_item_source_upload_id", "mistake_item", "source_upload_id", ["ix_mistake_item_source_upload_id"]),
        ("IX_mistake_item_student_id", "mistake_item", "student_id", ["ix_mistake_item_student_id", "IX_mistake_student_id"]),
        ("IX_mistake_item_student_id_subject_grade_review_status", "mistake_item", "student_id,subject,grade,review_status",
            ["ix_mistake_item_student_id_subject_grade_review_status", "IX_mistake_student_id_subject_grade_status"]),
        ("IX_mistake_practice_mistake_item_id", "mistake_practice", "mistake_item_id", ["IX_mistake_practice_mistake_id"]),
        ("IX_mistake_practice_practice_date", "mistake_practice", "practice_date", []),
        ("IX_mistake_practice_student_id", "mistake_practice", "student_id", []),
        ("IX_process_log_created_at", "process_log", "created_at", []),
        ("IX_process_log_mistake_item_id_created_at", "process_log", "mistake_item_id,created_at", ["IX_process_log_mistake_id_created_at"]),
    ];

    /// <summary>Indexes that disappear together with a dropped legacy column.</summary>
    private static readonly HashSet<string> LegacyColumnIndexes = new(StringComparer.Ordinal)
    {
        "IX_mistake_type", "IX_mistake_date", "ix_process_log_student_id", "IX_process_log_student_id",
    };

    private static readonly JsonSerializerOptions RegionJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Returns the steps performed; an empty list means no legacy marker was found and nothing was read for writing.
    /// </summary>
    public static async Task<List<string>> NormalizeAsync(DbConnection connection, LegacyArchiveStage stage,
        IReadOnlyCollection<string> eraHistory, string initialMigration, CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        var relations = await ReadAsync(connection, """
            SELECT relname FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind IN ('r','p','v','m','f','S')
            """, cancellationToken).ConfigureAwait(false);
        // The older table must qualify before any table lock or write. Unknown layouts stay with the classifier.
        var converting = relations.Contains("mistake") &&
            await LegacyMistakeConverter.IsSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
        if (relations.Contains("mistake") && !converting)
            return steps;
        if (converting)
        {
            await LegacyMistakeConverter.PrepareAsync(connection, relations, stage, cancellationToken).ConfigureAwait(false);
            relations.Add("mistake_item");
            relations.Add("__EFMigrationsHistory");
            steps.Add("stage public.mistake and prepare the item table");
        }
        if (!relations.Contains("mistake_item") || !relations.Contains("__EFMigrationsHistory"))
            return steps;
        var history = await ReadAsync(connection, "SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\"",
            cancellationToken).ConfigureAwait(false);
        // Only databases whose history the old maintenance could have written; versioned later histories never qualify.
        if (!history.All(id => id is LegacyInitial or LegacyRemoveFingerprint || eraHistory.Contains(id)))
            return steps;
        var columns = await ReadAsync(connection, """
            SELECT c.relname || '.' || a.attname FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
            WHERE c.relnamespace='public'::regnamespace AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
            """, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname='public'",
            cancellationToken).ConfigureAwait(false);
        var constraints = await ReadAsync(connection,
            "SELECT conname FROM pg_constraint WHERE connamespace='public'::regnamespace", cancellationToken).ConfigureAwait(false);

        var marked = history.Contains(LegacyInitial) || history.Contains(LegacyRemoveFingerprint)
            || LegacyTables.Keys.Any(relations.Contains)
            || constraints.Any(LegacyConstraints.Contains)
            || CoreIndexes.Any(index => index.Legacy.Any(indexes.Contains))
            || LegacyItemColumns.Any(column => columns.Contains($"mistake_item.{column}"))
            || columns.Contains("mistake_practice.mistake_id") || columns.Contains("process_log.mistake_id");
        if (!marked)
            return steps;

        var locked = new[] { "mistake", "mistake_item", "mistake_practice", "process_log", "__EFMigrationsHistory" }
            .Concat(LegacyTables.Keys).Where(relations.Contains).Select(table => "public." + Quote(table));
        await ExecuteAsync(connection, $"LOCK TABLE {string.Join(',', locked)} IN ACCESS EXCLUSIVE MODE", cancellationToken)
            .ConfigureAwait(false);

        // 1. History rows written by the 2026-05-01/05-03 migrations of the earlier service layout.
        var removedHistory = await stage.AddAsync($"""
            SELECT '__EFMigrationsHistory', h."MigrationId", 'history_row', to_jsonb(h), 'legacy_history_removed'
            FROM public."__EFMigrationsHistory" h WHERE h."MigrationId" IN ('{LegacyInitial}','{LegacyRemoveFingerprint}')
            """, cancellationToken).ConfigureAwait(false);
        if (removedHistory > 0)
        {
            await ExecuteAsync(connection, $"""
                DELETE FROM public."__EFMigrationsHistory" WHERE "MigrationId" IN ('{LegacyInitial}','{LegacyRemoveFingerprint}')
                """, cancellationToken).ConfigureAwait(false);
            steps.Add($"archive and remove {removedHistory} legacy history rows");
        }

        // 2. Parent key columns that the old maintenance renamed only partially.
        foreach (var table in new[] { "mistake_practice", "process_log" })
        {
            if (columns.Contains($"{table}.mistake_id") && !columns.Contains($"{table}.mistake_item_id"))
            {
                await ExecuteAsync(connection, $"ALTER TABLE public.{table} RENAME COLUMN mistake_id TO mistake_item_id",
                    cancellationToken).ConfigureAwait(false);
                columns.Remove($"{table}.mistake_id");
                columns.Add($"{table}.mistake_item_id");
                steps.Add($"rename {table}.mistake_id to mistake_item_id");
            }
        }

        // 3. Item columns of the 2026-05-10 layouts.
        var legacyItemColumns = LegacyItemColumns.Where(column => columns.Contains($"mistake_item.{column}")).ToList();
        if (!columns.Contains("mistake_item.source_regions"))
        {
            await ExecuteAsync(connection,
                "ALTER TABLE public.mistake_item ADD COLUMN source_regions jsonb NOT NULL DEFAULT '[]'::jsonb", cancellationToken)
                .ConfigureAwait(false);
            if (columns.Contains("mistake_item.source_image_path"))
            {
                var box = new[] { "bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2" }.All(c => columns.Contains($"mistake_item.{c}"))
                    ? "CASE WHEN bbox_x1 IS NOT NULL AND bbox_y1 IS NOT NULL AND bbox_x2 IS NOT NULL AND bbox_y2 IS NOT NULL " +
                      "THEN jsonb_build_object('x1',bbox_x1,'y1',bbox_y1,'x2',bbox_x2,'y2',bbox_y2) END"
                    : "NULL::jsonb";
                await ExecuteAsync(connection, $"""
                    UPDATE public.mistake_item SET source_regions =
                      jsonb_build_array(jsonb_build_object('sourceImagePath', source_image_path, 'boundingBox', {box}))
                    WHERE source_image_path IS NOT NULL AND source_image_path <> ''
                    """, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(connection, "ALTER TABLE public.mistake_item ALTER COLUMN source_regions DROP DEFAULT",
                cancellationToken).ConfigureAwait(false);
            columns.Add("mistake_item.source_regions");
            steps.Add("add mistake_item.source_regions from source_image_path and bounding box");
        }
        if (legacyItemColumns.Count > 0)
        {
            await DropColumnsAsync(connection, stage, "mistake_item", legacyItemColumns, cancellationToken).ConfigureAwait(false);
            steps.Add($"archive and drop mistake_item columns {string.Join(',', legacyItemColumns)}");
        }
        foreach (var (column, expression) in new[] { ("source_upload_id", "''::character varying"), ("source_regions", "'[]'::jsonb") })
        {
            var defaults = await ReadAsync(connection, $"""
                SELECT pg_get_expr(d.adbin,d.adrelid) FROM pg_attrdef d
                JOIN pg_attribute a ON a.attrelid=d.adrelid AND a.attnum=d.adnum
                WHERE d.adrelid='public.mistake_item'::regclass AND a.attname='{column}'
                """, cancellationToken).ConfigureAwait(false);
            if (defaults.SetEquals([expression]))
            {
                await ExecuteAsync(connection, $"ALTER TABLE public.mistake_item ALTER COLUMN {column} DROP DEFAULT",
                    cancellationToken).ConfigureAwait(false);
                steps.Add($"drop legacy default of mistake_item.{column}");
            }
        }

        // Insert after stripping old item columns, before orphan detection and new foreign keys.
        if (converting)
        {
            await LegacyMistakeConverter.ConvertAsync(connection, stage, cancellationToken).ConfigureAwait(false);
            steps.Add("convert public.mistake rows and renumber legacy process actions");
        }

        // 4. Rows whose item was dropped by the old maintenance; they block the foreign keys.
        foreach (var table in new[] { "mistake_practice", "process_log" })
        {
            if (!columns.Contains($"{table}.mistake_item_id"))
                continue;
            var orphanFilter = $"NOT EXISTS (SELECT 1 FROM public.mistake_item i WHERE i.id=t.mistake_item_id)";
            var orphans = await stage.AddAsync($"""
                SELECT '{table}', t.id::text, 'row', to_jsonb(t), 'orphan_row_removed' FROM public.{table} t WHERE {orphanFilter}
                """, cancellationToken).ConfigureAwait(false);
            if (orphans == 0)
                continue;
            await ExecuteAsync(connection, $"DELETE FROM public.{table} t WHERE {orphanFilter}", cancellationToken)
                .ConfigureAwait(false);
            steps.Add($"archive and remove {orphans} orphan {table} rows");
        }

        // 5. Primary and foreign key names.
        await NormalizeKeysAsync(connection, steps, cancellationToken).ConfigureAwait(false);

        // 6. Legacy tables: images become item regions where the item still has none; everything is archived.
        foreach (var (table, tableColumns) in LegacyTables)
        {
            if (!relations.Contains(table))
                continue;
            await VerifyLegacyTableAsync(connection, table, tableColumns, cancellationToken).ConfigureAwait(false);
            var backfilled = table == "mistake_detail"
                ? await BackfillDetailImagesAsync(connection, cancellationToken).ConfigureAwait(false)
                : [];
            var archived = await stage.AddAsync($"""
                SELECT '{table}', t.id::text, 'row', to_jsonb(t),
                       CASE WHEN t.id = ANY(@backfilled) THEN 'legacy_table_dropped_images_backfilled' ELSE 'legacy_table_dropped' END
                FROM public.{table} t ORDER BY t.id
                """, cancellationToken, ("backfilled", backfilled.ToArray())).ConfigureAwait(false);
            await ExecuteAsync(connection, $"DROP TABLE public.{table}", cancellationToken).ConfigureAwait(false);
            steps.Add(backfilled.Count > 0
                ? $"archive {archived} {table} rows, backfill images of {backfilled.Count} items, drop {table}"
                : $"archive {archived} {table} rows, drop {table}");
        }

        if (converting)
        {
            await ExecuteAsync(connection, "DROP TABLE public.mistake", cancellationToken).ConfigureAwait(false);
            steps.Add("drop archived public.mistake");
        }

        // 7. A database whose only history came from the earlier layout: its structure is InitialCreate plus the
        //    renames of the old maintenance, which the versioned migrations will now repeat.
        if (history.All(id => id is LegacyInitial or LegacyRemoveFingerprint))
        {
            if (columns.Contains("process_log.student_id"))
            {
                await DropColumnsAsync(connection, stage, "process_log", ["student_id"], cancellationToken).ConfigureAwait(false);
                steps.Add("archive and drop process_log.student_id added without history");
            }
            if (columns.Contains("mistake_item.root_cause") && !columns.Contains("mistake_item.review_comment"))
            {
                await ExecuteAsync(connection, "ALTER TABLE public.mistake_item RENAME COLUMN root_cause TO review_comment",
                    cancellationToken).ConfigureAwait(false);
                steps.Add("rename mistake_item.root_cause back to review_comment");
            }
            await ExecuteAsync(connection,
                $"INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('{initialMigration}','8.0.11')",
                cancellationToken).ConfigureAwait(false);
            steps.Add($"record {initialMigration} for the converged initial structure");
        }

        // 8. Index names.
        await NormalizeIndexesAsync(connection, steps, cancellationToken).ConfigureAwait(false);
        return steps;
    }

    /// <summary>
    /// Replaces each <c>mistake_practice.image_paths</c> text value that is not a JSON array of strings with an empty
    /// array, staging the original text first. Returns the number of reset rows; no-op unless the column is text.
    /// </summary>
    public static async Task<int> ResetInvalidTextPathsAsync(DbConnection connection, LegacyArchiveStage stage,
        CancellationToken cancellationToken)
    {
        var type = await ReadAsync(connection, """
            SELECT format_type(a.atttypid,a.atttypmod) FROM pg_attribute a
            WHERE a.attrelid=to_regclass('public.mistake_practice') AND a.attname='image_paths' AND NOT a.attisdropped
            """, cancellationToken).ConfigureAwait(false);
        if (!type.SetEquals(["text"]))
            return 0;
        await ExecuteAsync(connection, "LOCK TABLE public.mistake_practice IN ACCESS EXCLUSIVE MODE", cancellationToken)
            .ConfigureAwait(false);
        var invalid = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, image_paths FROM public.mistake_practice";
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (!IsStringArray(reader.GetString(1)))
                    invalid.Add(reader.GetGuid(0));
        }
        if (invalid.Count == 0)
            return 0;
        var ids = invalid.ToArray();
        await stage.AddAsync("""
            SELECT 'mistake_practice', t.id::text, 'invalid_value', jsonb_build_object('image_paths', t.image_paths),
                   'invalid_image_paths_reset'
            FROM public.mistake_practice t WHERE t.id = ANY(@ids) ORDER BY t.id
            """, cancellationToken, ("ids", ids)).ConfigureAwait(false);
        await ExecuteAsync(connection, "UPDATE public.mistake_practice SET image_paths='[]' WHERE id = ANY(@ids)",
            cancellationToken, ("ids", ids)).ConfigureAwait(false);
        return invalid.Count;
    }

    internal static bool IsStringArray(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                   document.RootElement.EnumerateArray().All(element => element.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task NormalizeKeysAsync(DbConnection connection, List<string> steps, CancellationToken cancellationToken)
    {
        var keys = await ReadAsync(connection, """
            SELECT c.conname || '|' || t.relname || '|' || c.contype::text || '|' ||
                   array_to_string(ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                       JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord), ',') || '|' ||
                   COALESCE(ref.relname,'-') || '|' ||
                   CASE WHEN c.contype='f' THEN array_to_string(ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY k(attnum,ord)
                       JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord), ',') ELSE '-' END || '|' ||
                   CASE WHEN c.contype='f' THEN c.confdeltype::text ELSE '-' END || '|' ||
                   CASE WHEN c.convalidated THEN 'V' ELSE 'U' END
            FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid LEFT JOIN pg_class ref ON ref.oid=c.confrelid
            WHERE c.connamespace='public'::regnamespace AND t.relname IN ('mistake_item','mistake_practice','process_log')
            """, cancellationToken).ConfigureAwait(false);
        var byName = keys.Select(key => key.Split('|')).ToDictionary(parts => parts[0], parts => string.Join('|', parts.Skip(1)),
            StringComparer.Ordinal);

        const string primary = "mistake_item|p|id|-|-|-|V";
        if (!byName.ContainsKey("PK_mistake_item"))
        {
            var legacy = new[] { "PK_mistake", "pk_mistake_item" }.FirstOrDefault(name =>
                byName.TryGetValue(name, out var definition) && definition == primary);
            if (legacy is not null)
            {
                await ExecuteAsync(connection, $"ALTER TABLE public.mistake_item RENAME CONSTRAINT {Quote(legacy)} TO \"PK_mistake_item\"",
                    cancellationToken).ConfigureAwait(false);
                steps.Add($"rename constraint {legacy} to PK_mistake_item");
            }
        }

        foreach (var table in new[] { "mistake_practice", "process_log" })
        {
            var target = $"FK_{table}_mistake_item_mistake_item_id";
            var definition = $"{table}|f|mistake_item_id|mistake_item|id|c|V";
            if (byName.ContainsKey(target))
                continue;
            var lower = target.ToLowerInvariant();
            if (byName.TryGetValue(lower, out var existing))
            {
                if (existing != definition)
                    continue;
                await ExecuteAsync(connection, $"ALTER TABLE public.{table} RENAME CONSTRAINT {lower} TO {Quote(target)}",
                    cancellationToken).ConfigureAwait(false);
                steps.Add($"rename constraint {lower} to {target}");
            }
            else if (!byName.Values.Any(value => value.StartsWith($"{table}|f|", StringComparison.Ordinal)))
            {
                await ExecuteAsync(connection, $"""
                    ALTER TABLE public.{table} ADD CONSTRAINT {Quote(target)} FOREIGN KEY (mistake_item_id)
                    REFERENCES public.mistake_item (id) ON DELETE CASCADE
                    """, cancellationToken).ConfigureAwait(false);
                steps.Add($"add constraint {target}");
            }
        }
    }

    private static async Task NormalizeIndexesAsync(DbConnection connection, List<string> steps, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(connection, """
            SELECT idx.relname || '|' || tbl.relname || '|' ||
                   CASE WHEN i.indisvalid AND i.indisready AND i.indnatts=i.indnkeyatts AND idx.relam=(SELECT oid FROM pg_am WHERE amname='btree')
                        AND i.indpred IS NULL AND i.indexprs IS NULL AND NOT i.indisunique THEN
                       array_to_string(ARRAY(SELECT replace(pg_get_indexdef(i.indexrelid,n,true), '"', '')
                           FROM generate_series(1,i.indnkeyatts) n), ',') ELSE '#other' END
            FROM pg_index i JOIN pg_class idx ON idx.oid=i.indexrelid JOIN pg_class tbl ON tbl.oid=i.indrelid
            WHERE tbl.relnamespace='public'::regnamespace
            """, cancellationToken).ConfigureAwait(false);
        var indexes = rows.Select(row => row.Split('|')).ToDictionary(parts => parts[0], parts => $"{parts[1]}|{parts[2]}",
            StringComparer.Ordinal);
        foreach (var (name, table, keyColumns, legacy) in CoreIndexes)
        {
            var definition = $"{table}|{keyColumns}";
            var same = legacy.Where(candidate => indexes.TryGetValue(candidate, out var value) && value == definition).ToList();
            if (!indexes.ContainsKey(name) && same.Count > 0)
            {
                await ExecuteAsync(connection, $"ALTER INDEX public.{Quote(same[0])} RENAME TO {Quote(name)}", cancellationToken)
                    .ConfigureAwait(false);
                indexes.Remove(same[0]);
                indexes[name] = definition;
                steps.Add($"rename index {same[0]} to {name}");
                same.RemoveAt(0);
            }
            if (indexes.TryGetValue(name, out var current) && current == definition)
            {
                foreach (var duplicate in same)
                {
                    await ExecuteAsync(connection, $"DROP INDEX public.{Quote(duplicate)}", cancellationToken).ConfigureAwait(false);
                    indexes.Remove(duplicate);
                    steps.Add($"drop duplicate index {duplicate}");
                }
            }
            if (!indexes.ContainsKey(name))
            {
                await ExecuteAsync(connection, $"CREATE INDEX {Quote(name)} ON public.{table} ({keyColumns})", cancellationToken)
                    .ConfigureAwait(false);
                indexes[name] = definition;
                steps.Add($"create index {name}");
            }
        }
    }

    private static async Task DropColumnsAsync(DbConnection connection, LegacyArchiveStage stage, string table,
        IReadOnlyList<string> dropped, CancellationToken cancellationToken)
    {
        // Dropping a column silently drops indexes and constraints on it; only the known legacy ones may go that way.
        var dependents = await ReadAsync(connection, $"""
            SELECT idx.relname FROM pg_index i JOIN pg_class idx ON idx.oid=i.indexrelid
            WHERE i.indrelid='public.{table}'::regclass AND EXISTS (
              SELECT 1 FROM pg_attribute a WHERE a.attrelid=i.indrelid AND a.attname = ANY(@columns)
                AND (a.attnum = ANY(i.indkey::int2[]) OR pg_get_expr(i.indexprs,i.indrelid) LIKE '%' || a.attname || '%'
                     OR pg_get_expr(i.indpred,i.indrelid) LIKE '%' || a.attname || '%'))
            UNION ALL
            SELECT c.conname FROM pg_constraint c WHERE c.conrelid='public.{table}'::regclass AND EXISTS (
              SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.conrelid AND a.attname = ANY(@columns) AND a.attnum = ANY(c.conkey))
            """, cancellationToken, ("columns", dropped.ToArray())).ConfigureAwait(false);
        var unexpected = dependents.Where(name => !LegacyColumnIndexes.Contains(name)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unexpected is not null)
            throw new UpgradeStepException($"Mistake legacy convergence found unexpected object {unexpected} on a legacy column of {table}.");

        var values = string.Join(',', dropped.Select(column => $"'{column}',t.{column}"));
        await stage.AddAsync($"""
            SELECT '{table}', t.id::text, 'column_values', v.payload, 'legacy_column_dropped'
            FROM public.{table} t CROSS JOIN LATERAL (SELECT jsonb_strip_nulls(jsonb_build_object({values})) AS payload) v
            WHERE v.payload <> jsonb_build_object() ORDER BY t.id
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection,
            $"ALTER TABLE public.{table} {string.Join(',', dropped.Select(column => $"DROP COLUMN {column}"))}", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyLegacyTableAsync(DbConnection connection, string table, string[] expectedColumns,
        CancellationToken cancellationToken)
    {
        var actual = await ReadAsync(connection, $"""
            SELECT a.attname FROM pg_attribute a WHERE a.attrelid='public.{table}'::regclass AND a.attnum>0 AND NOT a.attisdropped
            """, cancellationToken).ConfigureAwait(false);
        if (!actual.SetEquals(expectedColumns))
            throw new UpgradeStepException($"Mistake legacy convergence found an unexpected structure for {table}.");
        // Dropping the table drops what hangs on it; only the objects the old layout created may go that way.
        var attached = await ReadAsync(connection, $"""
            SELECT idx.relname FROM pg_index i JOIN pg_class idx ON idx.oid=i.indexrelid WHERE i.indrelid='public.{table}'::regclass
            UNION ALL SELECT conname FROM pg_constraint WHERE conrelid='public.{table}'::regclass
            UNION ALL SELECT tgname FROM pg_trigger WHERE tgrelid='public.{table}'::regclass AND NOT tgisinternal
            UNION ALL SELECT rulename FROM pg_rewrite WHERE ev_class='public.{table}'::regclass
            UNION ALL SELECT polname FROM pg_policy WHERE polrelid='public.{table}'::regclass
            """, cancellationToken).ConfigureAwait(false);
        var unexpected = attached.Where(name => !LegacyTableObjects.Contains(name)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unexpected is not null)
            throw new UpgradeStepException($"Mistake legacy convergence found unexpected object {unexpected} on {table}.");
    }

    private static async Task<List<Guid>> BackfillDetailImagesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var updates = new List<(Guid Detail, Guid Item, string Regions)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT d.id, d.mistake_id, d.original_images FROM public.mistake_detail d
                JOIN public.mistake_item i ON i.id=d.mistake_id WHERE i.source_regions='[]'::jsonb ORDER BY d.id
                """;
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var images = reader.GetString(2);
                if (!IsStringArray(images))
                    continue;
                var paths = JsonSerializer.Deserialize<string[]>(images)!.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                if (paths.Length == 0)
                    continue;
                var regions = JsonSerializer.Serialize(paths.Select(path => new SourceRegionJson(path, null)), RegionJson);
                updates.Add((reader.GetGuid(0), reader.GetGuid(1), regions));
            }
        }
        var backfilled = new List<Guid>();
        foreach (var (detail, item, regions) in updates)
        {
            var affected = await ExecuteAsync(connection,
                "UPDATE public.mistake_item SET source_regions=@regions::jsonb WHERE id=@item AND source_regions='[]'::jsonb",
                cancellationToken, ("regions", regions), ("item", item)).ConfigureAwait(false);
            if (affected == 1)
                backfilled.Add(detail);
        }
        return backfilled;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static Task<int> ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) =>
        LegacyArchiveStage.ExecuteAsync(connection, sql, cancellationToken, parameters);

    private static async Task<HashSet<string>> ReadAsync(DbConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (!reader.IsDBNull(0))
                rows.Add(reader.GetString(0));
        return rows;
    }

    private sealed record SourceRegionJson(string SourceImagePath, object? BoundingBox);
}
