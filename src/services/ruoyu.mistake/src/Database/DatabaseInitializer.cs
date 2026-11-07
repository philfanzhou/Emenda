using Ruoyu.Study.Common.Database;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Mistake.Database;

public static class DatabaseInitializer
{
    private const string Initial = "20260514051544_InitialCreate";
    private const string ProcessLog = "20260612030015_AddStudentIdToProcessLog";
    private const string Reanalyze = "20260626000000_AddReanalyzeJob";
    private const string RootCause = "20260929000001_RenameReviewCommentToRootCause";
    private const string Lease = "20260930000001_LeaseReanalyzeJobs";
    private const long UpgradeLockKey = 0x52554F59554D4953;
    private const int TargetVersion = 14;

    private static readonly HashSet<string> ExpectedTables = Set("""
        mistake_item
        mistake_practice
        process_log
        reanalyze_job
        __EFMigrationsHistory
        """);

    private static readonly HashSet<string> ExpectedColumns = Set("""
        mistake_item|id|uuid|N
        mistake_item|student_id|character varying(36)|N
        mistake_item|subject|integer|N
        mistake_item|grade|integer|N
        mistake_item|source_upload_id|character varying(64)|N
        mistake_item|source_regions|jsonb|N
        mistake_item|review_status|integer|N
        mistake_item|reviewer_id|character varying(36)|Y
        mistake_item|reviewed_at|timestamp with time zone|Y
        mistake_item|root_cause|text|Y
        mistake_item|question_id|character varying(36)|Y
        mistake_item|created_at|timestamp with time zone|N
        mistake_item|updated_at|timestamp with time zone|Y
        mistake_practice|id|uuid|N
        mistake_practice|mistake_item_id|uuid|N
        mistake_practice|student_id|character varying(36)|N
        mistake_practice|practice_date|timestamp with time zone|N
        mistake_practice|image_paths|jsonb|N
        mistake_practice|student_answer|text|Y
        mistake_practice|is_correct|boolean|N
        mistake_practice|comments|text|Y
        process_log|id|uuid|N
        process_log|mistake_item_id|uuid|N
        process_log|student_id|character varying(36)|Y
        process_log|action|integer|N
        process_log|details|text|Y
        process_log|created_at|timestamp with time zone|N
        reanalyze_job|id|uuid|N
        reanalyze_job|item_id|uuid|N
        reanalyze_job|reviewer_id|character varying(36)|N
        reanalyze_job|student_id|character varying(36)|N
        reanalyze_job|teacher_description|text|Y
        reanalyze_job|previous_root_cause|text|Y
        reanalyze_job|status|integer|N
        reanalyze_job|error_message|text|Y
        reanalyze_job|new_root_cause|text|Y
        reanalyze_job|created_at|timestamp with time zone|N
        reanalyze_job|started_at|timestamp with time zone|Y
        reanalyze_job|completed_at|timestamp with time zone|Y
        reanalyze_job|vl_duration_ms|bigint|Y
        reanalyze_job|owner_id|uuid|Y
        reanalyze_job|lease_until|timestamp with time zone|Y
        reanalyze_job|fencing_version|bigint|N
        reanalyze_job|attempt|integer|N
        reanalyze_job|execution_root_cause|text|Y
        reanalyze_job|execution_input_digest|character varying(64)|Y
        __EFMigrationsHistory|MigrationId|character varying(150)|N
        __EFMigrationsHistory|ProductVersion|character varying(32)|N
        """);

    private static readonly HashSet<string> ExpectedIndexes = Set("""
        PK_mistake_item|mistake_item|id|U
        IX_mistake_item_created_at|mistake_item|created_at|N
        IX_mistake_item_review_status|mistake_item|review_status|N
        IX_mistake_item_source_upload_id|mistake_item|source_upload_id|N
        IX_mistake_item_student_id|mistake_item|student_id|N
        IX_mistake_item_student_id_subject_grade_review_status|mistake_item|student_id,subject,grade,review_status|N
        PK_mistake_practice|mistake_practice|id|U
        IX_mistake_practice_mistake_item_id|mistake_practice|mistake_item_id|N
        IX_mistake_practice_practice_date|mistake_practice|practice_date|N
        IX_mistake_practice_student_id|mistake_practice|student_id|N
        PK_process_log|process_log|id|U
        IX_process_log_created_at|process_log|created_at|N
        IX_process_log_mistake_item_id_created_at|process_log|mistake_item_id,created_at|N
        IX_process_log_student_id|process_log|student_id|N
        PK_reanalyze_job|reanalyze_job|id|U
        IX_reanalyze_job_Status_CreatedAt|reanalyze_job|status,created_at|N
        IX_reanalyze_job_ReviewerId_Status|reanalyze_job|reviewer_id,status|N
        IX_reanalyze_job_CompletedAt|reanalyze_job|completed_at|N
        PK___EFMigrationsHistory|__EFMigrationsHistory|MigrationId|U
        """);

    private static readonly HashSet<string> ExpectedConstraints = Set("""
        PK_mistake_item|p|id|-|-|-|V
        PK_mistake_practice|p|id|-|-|-|V
        FK_mistake_practice_mistake_item_mistake_item_id|f|mistake_item_id|mistake_item|id|c|V
        PK_process_log|p|id|-|-|-|V
        FK_process_log_mistake_item_mistake_item_id|f|mistake_item_id|mistake_item|id|c|V
        PK_reanalyze_job|p|id|-|-|-|V
        PK___EFMigrationsHistory|p|MigrationId|-|-|-|V
        """);

    /// <summary>
    /// Normal startup entry: upgrades an empty or known database to the target schema, or verifies an
    /// existing target without writing.
    /// </summary>
    public static async Task InitializeAsync(MistakeDbContext context, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger("DatabaseInitializer");
        var result = await UpgradeToTargetAsync(context, null, cancellationToken).ConfigureAwait(false);
        if (result.ArchivedRows > 0 || result.ResetImagePaths > 0)
            logger.LogWarning(
                "Mistake legacy schema converged: {ArchivedRows} removed or rewritten rows/values archived in legacy_archive; {ResetImagePaths} invalid mistake_practice.image_paths values reset to []",
                result.ArchivedRows, result.ResetImagePaths);
        if (result.Upgraded)
            logger.LogInformation(
                "Mistake database schema verified at target version {TargetVersion} after automatic upgrade from version {FromVersion}; steps: {Steps}",
                result.ToVersion, result.FromVersion, string.Join(", ", result.Steps));
        else
            logger.LogInformation("Mistake database schema verified at target version {TargetVersion}; no upgrade needed",
                result.ToVersion);
    }

    /// <summary>
    /// Upgrades an empty, known or legacy auto-maintained database to the target schema in one transaction under the
    /// shared advisory lock. A target database is left unchanged. Legacy structures are first converged to a known
    /// shape, with every removed or rewritten value archived in <c>legacy_archive</c>. Unsupported structures are
    /// rejected; a failed upgrade rolls back as a whole. Commands inside the upgrade are not bound by the connection's
    /// command timeout and stop only on cancellation or failure.
    /// </summary>
    /// <param name="expectedDatabase">When not null, the connected database name must match exactly.</param>
    public static async Task<DatabaseUpgradeResult> UpgradeToTargetAsync(MistakeDbContext context,
        string? expectedDatabase = null, CancellationToken cancellationToken = default)
    {
        if (expectedDatabase is not null &&
            !string.Equals(expectedDatabase, context.Database.GetDbConnection().Database, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Mistake database upgrade refused: --database does not match the connected database. Database unchanged.");

        var commandTimeout = context.Database.GetCommandTimeout();
        context.Database.SetCommandTimeout(0);
        try
        {
            return await UpgradeInTransactionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context.Database.SetCommandTimeout(commandTimeout);
        }
    }

    private static async Task<DatabaseUpgradeResult> UpgradeInTransactionAsync(MistakeDbContext context,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        // Instances starting together serialize here; a waiter reclassifies after the winner commits.
        await context.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({UpgradeLockKey})", cancellationToken)
            .ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();
        var stage = new LegacyArchiveStage(connection);
        List<string> steps;
        try
        {
            steps = await LegacySchemaNormalizer.NormalizeAsync(connection, stage, [Initial, ProcessLog, Reanalyze], Initial,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Mistake legacy schema convergence failed before commit ({Reason(error)}); the transaction rolls back and the database stays unchanged.");
        }
        var before = await ClassifyAsync(connection, cancellationToken).ConfigureAwait(false);
        if (before.Kind == SchemaKind.Invalid)
            throw new InvalidOperationException(steps.Count == 0 ? before.Diagnostic :
                $"{before.Diagnostic} Legacy convergence was rolled back.");
        if (before.Target && steps.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseUpgradeResult(before.Version, before.Version, []);
        }

        var resetPaths = 0;
        long archived;
        try
        {
            resetPaths = await LegacySchemaNormalizer.ResetInvalidTextPathsAsync(connection, stage, cancellationToken)
                .ConfigureAwait(false);
            if (resetPaths > 0)
                steps.Add($"archive and reset {resetPaths} invalid mistake_practice.image_paths values");
            if (before.Kind == SchemaKind.Empty)
            {
                // Npgsql 10 probes a missing history table by querying it. Create
                // the canonical table inside our transaction first so that probe
                // cannot abort the transaction; rollback still covers all DDL.
                await context.Database.ExecuteSqlRawAsync(
                    context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), cancellationToken)
                    .ConfigureAwait(false);
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                steps.Add("apply all migrations");
            }
            else
            {
                if (before.Kind is SchemaKind.MisstampedTextPaths or SchemaKind.MisstampedLowercaseIndex)
                {
                    if (before.Kind == SchemaKind.MisstampedLowercaseIndex)
                    {
                        await context.Database.ExecuteSqlRawAsync(
                            "ALTER INDEX public.ix_process_log_student_id RENAME TO \"IX_process_log_student_id\"",
                            cancellationToken).ConfigureAwait(false);
                        steps.Add("rename ix_process_log_student_id");
                    }
                    // Keep the source rows stable while checking their semantics and changing the column type.
                    await context.Database.ExecuteSqlRawAsync(
                        "LOCK TABLE public.mistake_practice IN ACCESS EXCLUSIVE MODE", cancellationToken).ConfigureAwait(false);
                    var paths = await ReadPathsAsync(connection, cancellationToken).ConfigureAwait(false);
                    await context.Database.ExecuteSqlRawAsync(
                        "ALTER TABLE public.mistake_practice ALTER COLUMN image_paths TYPE jsonb USING image_paths::jsonb",
                        cancellationToken).ConfigureAwait(false);
                    var convertedPaths = await ReadPathsAsync(connection, cancellationToken).ConfigureAwait(false);
                    if (!SamePaths(paths, convertedPaths))
                        throw new UpgradeStepException("Mistake practice paths changed during conversion.");
                    steps.Add("convert mistake_practice.image_paths to jsonb");
                    await InsertRootCauseHistoryAsync(context, cancellationToken).ConfigureAwait(false);
                    steps.Add($"record {RootCause}");
                }
                else if (before.Kind == SchemaKind.LegacyRuntime)
                {
                    // The column was already renamed by an older runtime. Only the verified history row is missing.
                    await InsertRootCauseHistoryAsync(context, cancellationToken).ConfigureAwait(false);
                    steps.Add($"record {RootCause}");
                }
                else if (before.Version < 4)
                {
                    await context.GetService<IMigrator>().MigrateAsync(RootCause, cancellationToken).ConfigureAwait(false);
                    steps.Add($"migrate to {RootCause}");
                }

                await context.GetService<IMigrator>().MigrateAsync(StorageReferenceRemovalSchema.Migration, cancellationToken)
                    .ConfigureAwait(false);
                steps.Add($"migrate to {StorageReferenceRemovalSchema.Migration}");
            }

            archived = await stage.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (archived > 0)
                steps.Add($"archive {archived} legacy rows/values in legacy_archive");
            var after = await ClassifyAsync(connection, cancellationToken).ConfigureAwait(false);
            if (after.Kind != SchemaKind.Ready || !after.Target)
                throw new UpgradeStepException($"Mistake upgrade verification failed: {after.Diagnostic}");
            steps.Add("verify target schema");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Mistake database upgrade from version {before.Version} failed before commit ({Reason(error)}); the transaction rolls back and the database stays unchanged.");
        }
        // A connection failure during COMMIT has an unknown outcome. The next start reclassifies the database.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseUpgradeResult(before.Version, TargetVersion, steps)
        {
            ArchivedRows = archived,
            ResetImagePaths = resetPaths,
        };
    }

    // Provider messages can quote row values, so only our own diagnostics and the SQLSTATE are surfaced.
    private static string Reason(Exception error) =>
        error is UpgradeStepException ? error.Message :
        error is DbException { SqlState: not null } dbError ? $"SQLSTATE {dbError.SqlState}" : error.GetType().Name;

    private static Task<int> InsertRootCauseHistoryAsync(MistakeDbContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('{RootCause}','8.0.11')",
            cancellationToken);

    private static async Task<Dictionary<Guid, string[]>> ReadPathsAsync(DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, image_paths::text FROM public.mistake_practice ORDER BY id";
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var paths = new Dictionary<Guid, string[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string[] values;
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(1));
                if (document.RootElement.ValueKind != JsonValueKind.Array ||
                    document.RootElement.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new UpgradeStepException("Mistake practice paths must be JSON arrays of strings.");
                values = document.RootElement.EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            catch (JsonException)
            {
                throw new UpgradeStepException("Mistake practice paths must be JSON arrays of strings.");
            }
            paths.Add(reader.GetGuid(0), values);
        }
        return paths;
    }

    private static bool SamePaths(Dictionary<Guid, string[]> before, Dictionary<Guid, string[]> after) =>
        before.Count == after.Count && before.All(row =>
            after.TryGetValue(row.Key, out var values) && row.Value.SequenceEqual(values, StringComparer.Ordinal));

    private static async Task<SchemaState> ClassifyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var relations = await ReadSetAsync(connection, """
            SELECT relname FROM pg_class WHERE relnamespace = 'public'::regnamespace
            AND relkind IN ('r','p','v','m','f','S') ORDER BY relname
            """, cancellationToken).ConfigureAwait(false);
        var routines = await ReadSetAsync(connection,
            "SELECT proname FROM pg_proc WHERE pronamespace='public'::regnamespace ORDER BY proname",
            cancellationToken).ConfigureAwait(false);

        if (relations.Count == 0)
            return new SchemaState(SchemaKind.Empty, false, 0, "Mistake database is empty.");
        if (relations.Contains("mistake"))
            return Invalid("legacy relation public.mistake exists.");
        if (!relations.Contains("__EFMigrationsHistory"))
            return Invalid("missing relation __EFMigrationsHistory.");
        var history = await ReadSetAsync(connection,
            // EF 10 stamps newly applied migrations with its own version. Compare
            // that supported stamp with the legacy chain without rewriting rows.
            """
            SELECT "MigrationId" || '|' || CASE "ProductVersion"
                WHEN '10.0.13' THEN '8.0.11' ELSE "ProductVersion" END
            FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
            """,
            cancellationToken).ConfigureAwait(false);
        var initialHistory = Set($"{Initial}|8.0.11");
        var processHistory = new HashSet<string>(initialHistory, StringComparer.Ordinal) { $"{ProcessLog}|8.0.11" };
        var reanalyzeHistory = new HashSet<string>(processHistory, StringComparer.Ordinal) { $"{Reanalyze}|8.0.11" };
        var targetHistory = new HashSet<string>(reanalyzeHistory, StringComparer.Ordinal) { $"{RootCause}|8.0.11" };
        var leaseHistory = new HashSet<string>(targetHistory, StringComparer.Ordinal) { $"{Lease}|8.0.11" };
        var intakeHistory = new HashSet<string>(leaseHistory, StringComparer.Ordinal) { $"{SourceIntakeSchema.Migration}|8.0.11" };
        var sourceTargetHistory=new HashSet<string>(intakeHistory,StringComparer.Ordinal) { $"{SourceTargetSchema.Migration}|8.0.11" };
        var sourceReturnHistory=new HashSet<string>(sourceTargetHistory,StringComparer.Ordinal) { $"{SourceReturnSchema.Migration}|8.0.11" };
        var sourceHandoffHistory=new HashSet<string>(sourceReturnHistory,StringComparer.Ordinal) { $"{SourceHandoffSchema.Migration}|8.0.11" };
        var referenceHistory = new HashSet<string>(sourceHandoffHistory,StringComparer.Ordinal) { $"{StorageReferenceRemovalSchema.Added}|8.0.11" };
        var archiveHistory = new HashSet<string>(referenceHistory, StringComparer.Ordinal) { $"{LegacyArchiveSchema.Migration}|8.0.11" };
        var legacyReturnHistory = new HashSet<string>(archiveHistory, StringComparer.Ordinal) { $"{LegacyReturnSchema.Migration}|8.0.11" };
        var layoutJobHistory = new HashSet<string>(legacyReturnHistory, StringComparer.Ordinal) { $"{LayoutJobSchema.Migration}|8.0.11" };
        var referenceRemovalHistory = new HashSet<string>(layoutJobHistory, StringComparer.Ordinal) { $"{StorageReferenceRemovalSchema.Migration}|8.0.11" };
        var version = history.SetEquals(initialHistory) ? 1 :
            history.SetEquals(processHistory) ? 2 :
            history.SetEquals(reanalyzeHistory) ? 3 :
            history.SetEquals(targetHistory) ? 4 :
            history.SetEquals(leaseHistory) ? 5 : history.SetEquals(intakeHistory) ? 6 : history.SetEquals(sourceTargetHistory) ? 7 : history.SetEquals(sourceReturnHistory) ? 8 : history.SetEquals(sourceHandoffHistory) ? 9 : history.SetEquals(referenceHistory) ? 10 : history.SetEquals(archiveHistory) ? 11 : history.SetEquals(legacyReturnHistory) ? 12 : history.SetEquals(layoutJobHistory) ? 13 : history.SetEquals(referenceRemovalHistory) ? 14 : 0;
        if (version == 0)
            return Invalid("migration history does not match a supported exact version chain.");
        // Name the leftover directly instead of reporting it as a routine or relation mismatch.
        if (version >= 10 && !StorageReferenceRemovalSchema.Present(version) &&
            (relations.Contains(RetiredStorageReferenceSchema.Table) || routines.Contains("guard_storage_reference_snapshot")))
            return Invalid("retired relation storage_reference_snapshot or function guard_storage_reference_snapshot still exists after its drop migration.");

        if (version >= 6)
        {
            var diagnostic = await SourceIntakeCatalog.VerifyRoutinesAsync(connection, cancellationToken,version>=7,version>=8,version>=9,StorageReferenceRemovalSchema.Present(version),version>=11,version>=12,version>=13);
            if (diagnostic != null) return Invalid(diagnostic);
        }
        else if (routines.Count > 0) return Invalid($"unexpected public routine {routines.Order(StringComparer.Ordinal).First()}.");
        var expectedTables = version >= 3 ? ExpectedTables :
            Without(ExpectedTables, "reanalyze_job");
        expectedTables = new HashSet<string>(expectedTables, StringComparer.Ordinal);
        if (version >= 6) expectedTables.UnionWith(SourceIntakeCatalog.Tables);
        if (version >= 7) expectedTables.UnionWith(SourceTargetCatalog.Tables);
        if (version >= 8) expectedTables.UnionWith(SourceReturnCatalog.Tables);
        if (version >= 9) expectedTables.UnionWith(SourceHandoffCatalog.Tables);
        if (StorageReferenceRemovalSchema.Present(version)) expectedTables.Add(RetiredStorageReferenceSchema.Table);
        if (version >= 11) expectedTables.UnionWith(LegacyArchiveSchema.Relations);
        if (version >= 12) expectedTables.UnionWith(LegacyReturnCatalog.Tables);
        if (version >= 13) expectedTables.UnionWith(LayoutJobCatalog.Tables);
        var mismatch = Compare("relation", expectedTables, relations);
        if (mismatch is not null) return Invalid(mismatch);

        var columns = await ReadSetAsync(connection, """
            SELECT c.relname || '|' || a.attname || '|' || format_type(a.atttypid,a.atttypmod) || '|' ||
                   CASE WHEN a.attnotnull THEN 'N' ELSE 'Y' END
            FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
            WHERE c.relnamespace='public'::regnamespace AND c.relkind='r'
              AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname,a.attnum
            """, cancellationToken).ConfigureAwait(false);
        var expectedColumns = new HashSet<string>(ExpectedColumns, StringComparer.Ordinal);
        if (version >= 6) expectedColumns.UnionWith(SourceIntakeCatalog.Columns);
        if (version >= 7) expectedColumns.UnionWith(SourceTargetCatalog.Columns);
        if (version >= 8) expectedColumns.UnionWith(SourceReturnCatalog.Columns);
        if (version >= 9) expectedColumns.UnionWith(SourceHandoffCatalog.Columns);
        if (StorageReferenceRemovalSchema.Present(version)) expectedColumns.UnionWith(RetiredStorageReferenceSchema.Columns);
        if (version >= 11) expectedColumns.UnionWith(LegacyArchiveSchema.Columns);
        if (version >= 12) expectedColumns.UnionWith(LegacyReturnCatalog.Columns);
        if (version >= 13) expectedColumns.UnionWith(LayoutJobCatalog.Columns);
        if (version < 3)
            expectedColumns.RemoveWhere(column => column.StartsWith("reanalyze_job|", StringComparison.Ordinal));
        else if (version < 5)
            expectedColumns.RemoveWhere(column => column.StartsWith("reanalyze_job|owner_id|", StringComparison.Ordinal)
                || column.StartsWith("reanalyze_job|lease_until|", StringComparison.Ordinal)
                || column.StartsWith("reanalyze_job|fencing_version|", StringComparison.Ordinal)
                || column.StartsWith("reanalyze_job|attempt|", StringComparison.Ordinal)
                || column.StartsWith("reanalyze_job|execution_root_cause|", StringComparison.Ordinal)
                || column.StartsWith("reanalyze_job|execution_input_digest|", StringComparison.Ordinal));
        if (version < 2)
        {
            expectedColumns.Remove("process_log|student_id|character varying(36)|Y");
            expectedColumns.Remove("mistake_practice|image_paths|jsonb|N");
            expectedColumns.Add("mistake_practice|image_paths|text|N");
        }
        if (version < 4)
        {
            expectedColumns.Remove("mistake_item|root_cause|text|Y");
            expectedColumns.Add("mistake_item|review_comment|text|Y");
        }
        var legacyColumns = new HashSet<string>(expectedColumns, StringComparer.Ordinal);
        legacyColumns.Remove("mistake_item|review_comment|text|Y");
        legacyColumns.Add("mistake_item|root_cause|text|Y");
        var misstampedColumns = new HashSet<string>(legacyColumns, StringComparer.Ordinal);
        misstampedColumns.Remove("mistake_practice|image_paths|jsonb|N");
        misstampedColumns.Add("mistake_practice|image_paths|text|N");
        // The variant is chosen by its distinguishing columns, so a deviation is reported against that variant.
        var rootCauseVariant = version == 3 && columns.Contains("mistake_item|root_cause|text|Y");
        var misstampedTextPaths = rootCauseVariant && columns.Contains("mistake_practice|image_paths|text|N");
        var legacyRuntime = rootCauseVariant && !misstampedTextPaths;
        mismatch = Compare("column/type/nullability",
            misstampedTextPaths ? misstampedColumns : legacyRuntime ? legacyColumns : expectedColumns, columns);
        if (mismatch is not null) return Invalid(mismatch);

        var defaults = await ReadSetAsync(connection, """
            SELECT c.relname || '|' || a.attname || '|' || pg_get_expr(d.adbin,d.adrelid)
            FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid=d.adrelid AND a.attnum=d.adnum
                JOIN pg_class c ON c.oid=d.adrelid
            WHERE c.relnamespace='public'::regnamespace ORDER BY c.relname,a.attname
            """, cancellationToken).ConfigureAwait(false);
        mismatch = Compare("column default", version >= 5
            ? Set("reanalyze_job|status|1\nreanalyze_job|attempt|0\nreanalyze_job|fencing_version|0")
            : version >= 3 ? Set("reanalyze_job|status|1") : Set(""), defaults);
        if (mismatch is not null) return Invalid(mismatch);

        var triggers = await ReadSetAsync(connection, """
            SELECT t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
            WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal ORDER BY t.tgname
            """, cancellationToken).ConfigureAwait(false);
        if (version >= 6)
        {
            var diagnostic = await SourceIntakeCatalog.VerifyTriggersAsync(connection,cancellationToken,version>=7,version>=8,version>=9,StorageReferenceRemovalSchema.Present(version),version>=11,version>=12,version>=13);
            if (diagnostic != null) return Invalid(diagnostic);
        }
        else if (triggers.Count > 0) return Invalid($"unexpected trigger {triggers.Order(StringComparer.Ordinal).First()}.");

        var indexes = await ReadSetAsync(connection, """
            SELECT idx.relname || '|' || tbl.relname || '|' ||
                   CASE WHEN i.indisvalid AND i.indisready AND i.indnatts=i.indnkeyatts AND idx.relam=(SELECT oid FROM pg_am WHERE amname='btree') AND i.indpred IS NULL AND i.indexprs IS NULL THEN
                       array_to_string(ARRAY(SELECT replace(pg_get_indexdef(i.indexrelid,n,true), '"', '')
                           FROM generate_series(1,i.indnkeyatts) n), ',') ELSE '#invalid' END || '|' ||
                   CASE WHEN i.indisunique THEN 'U' ELSE 'N' END
            FROM pg_index i JOIN pg_class idx ON idx.oid=i.indexrelid
                JOIN pg_class tbl ON tbl.oid=i.indrelid
            WHERE tbl.relnamespace='public'::regnamespace ORDER BY idx.relname
            """, cancellationToken).ConfigureAwait(false);
        var expectedIndexes = new HashSet<string>(ExpectedIndexes, StringComparer.Ordinal);
        if (version >= 6) expectedIndexes.UnionWith(SourceIntakeCatalog.Indexes);
        if (version >= 7) expectedIndexes.UnionWith(SourceTargetCatalog.Indexes);
        if (version >= 8) expectedIndexes.UnionWith(SourceReturnCatalog.Indexes);
        if (version >= 9) expectedIndexes.UnionWith(SourceHandoffCatalog.Indexes);
        if (StorageReferenceRemovalSchema.Present(version)) expectedIndexes.UnionWith(RetiredStorageReferenceSchema.Indexes);
        if (version >= 11) expectedIndexes.UnionWith(LegacyArchiveSchema.Indexes);
        if (version >= 12) expectedIndexes.UnionWith(LegacyReturnCatalog.Indexes);
        if (version >= 13) expectedIndexes.UnionWith(LayoutJobCatalog.Indexes);
        if (version < 3)
            expectedIndexes.RemoveWhere(index => index.Contains("|reanalyze_job|", StringComparison.Ordinal));
        if (version < 2)
            expectedIndexes.Remove("IX_process_log_student_id|process_log|student_id|N");
        var lowercaseIndexes = new HashSet<string>(expectedIndexes, StringComparer.Ordinal);
        lowercaseIndexes.Remove("IX_process_log_student_id|process_log|student_id|N");
        lowercaseIndexes.Add("ix_process_log_student_id|process_log|student_id|N");
        var misstampedLowercaseIndex = misstampedTextPaths && indexes.Contains("ix_process_log_student_id|process_log|student_id|N");
        if (misstampedLowercaseIndex)
        {
            var definitions = await ReadSetAsync(connection,
                "SELECT pg_get_indexdef(to_regclass('public.ix_process_log_student_id'))",
                cancellationToken).ConfigureAwait(false);
            if (!definitions.SetEquals(Set(
                "CREATE INDEX ix_process_log_student_id ON public.process_log USING btree (student_id)")))
                return Invalid("unexpected definition for index ix_process_log_student_id.");
        }
        mismatch = Compare("index", misstampedLowercaseIndex ? lowercaseIndexes : expectedIndexes, indexes);
        if (mismatch is not null) return Invalid(mismatch);

        var constraints = await ReadSetAsync(connection, """
            SELECT c.conname || '|' || c.contype::text || '|' ||
                   array_to_string(ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                       JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord), ',') || '|' ||
                   COALESCE(ref.relname,'-') || '|' ||
                   CASE WHEN c.contype='f' THEN array_to_string(ARRAY(SELECT a.attname
                       FROM unnest(c.confkey) WITH ORDINALITY k(attnum,ord)
                       JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord), ',') ELSE '-' END || '|' ||
                   CASE WHEN c.contype='f' THEN c.confdeltype::text ELSE '-' END || '|' ||
                   CASE WHEN c.convalidated THEN 'V' ELSE 'U' END
            FROM pg_constraint c LEFT JOIN pg_class ref ON ref.oid=c.confrelid
            WHERE c.connamespace='public'::regnamespace ORDER BY c.conname
            """, cancellationToken).ConfigureAwait(false);
        var expectedConstraints = version >= 3 ? ExpectedConstraints :
            Without(ExpectedConstraints, "PK_reanalyze_job|p|id|-|-|-|V");
        expectedConstraints = new HashSet<string>(expectedConstraints,StringComparer.Ordinal);
        if (version >= 6) expectedConstraints.UnionWith(SourceIntakeCatalog.Constraints);
        if (version >= 7) expectedConstraints.UnionWith(SourceTargetCatalog.Constraints);
        if (version >= 8) expectedConstraints.UnionWith(SourceReturnCatalog.Constraints);
        if (version >= 9) expectedConstraints.UnionWith(SourceHandoffCatalog.Constraints);
        if (StorageReferenceRemovalSchema.Present(version)) expectedConstraints.UnionWith(RetiredStorageReferenceSchema.Constraints);
        if (version >= 11) expectedConstraints.UnionWith(LegacyArchiveSchema.Constraints);
        if (version >= 12) expectedConstraints.UnionWith(LegacyReturnCatalog.Constraints);
        if (version >= 13) expectedConstraints.UnionWith(LayoutJobCatalog.Constraints);
        mismatch = Compare("constraint", expectedConstraints, constraints);
        if (mismatch is not null) return Invalid(mismatch);

        if (version >= 6)
            return new SchemaState(version == TargetVersion ? SchemaKind.Ready : SchemaKind.Known, version == TargetVersion, version, "Mistake target schema and history are consistent.");
        if (legacyRuntime)
            return new SchemaState(SchemaKind.LegacyRuntime, false, version,
                "Mistake legacy runtime shape accepted: root_cause replaces InitialCreate.review_comment without a versioned history row.");
        if (misstampedLowercaseIndex)
            return new SchemaState(SchemaKind.MisstampedLowercaseIndex, false, version,
                "Mistake exact three-history text paths and lowercase index shape.");
        if (misstampedTextPaths)
            return new SchemaState(SchemaKind.MisstampedTextPaths, false, version,
                "Mistake exact three-history text paths shape.");
        return new SchemaState(SchemaKind.Known, false, version, $"Mistake known EF migration version {version}.");
    }

    private static SchemaState Invalid(string diagnostic) => new(SchemaKind.Invalid, false, 0,
        $"Mistake database incompatible: {diagnostic} Automatic upgrade does not support this structure; database unchanged.");

    private static string? Compare(string kind, HashSet<string> expected, HashSet<string> actual)
    {
        var missing = expected.Except(actual).Order(StringComparer.Ordinal).FirstOrDefault();
        if (missing is not null) return $"missing {kind} {missing}.";
        var unexpected = actual.Except(expected).Order(StringComparer.Ordinal).FirstOrDefault();
        return unexpected is null ? null : $"unexpected {kind} {unexpected}.";
    }

    private static async Task<HashSet<string>> ReadSetAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(reader.GetString(0));
        return rows;
    }

    private static HashSet<string> Set(string lines) =>
        lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Without(HashSet<string> source, string member)
    {
        var result = new HashSet<string>(source, StringComparer.Ordinal);
        result.Remove(member);
        return result;
    }

    private enum SchemaKind { Empty, Known, LegacyRuntime, MisstampedTextPaths, MisstampedLowercaseIndex, Ready, Invalid }
    private sealed record SchemaState(SchemaKind Kind, bool Target, int Version, string Diagnostic);
}
