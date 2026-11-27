namespace Emenda.Database;

/// <summary>
/// Schema v28: drops <c>reanalyze_job</c> (VL reanalysis, schema v3) and <c>product_intake_request</c> (receipts of the
/// retired <c>/api/intake/uploads</c>, schema v15). Both features were removed with the standalone product (schema v19)
/// and nothing has read or written the tables since. Every remaining row is first copied unchanged into
/// <c>legacy_archive</c> (<c>kind</c> <c>row</c>), so an older database that still has reanalysis history upgrades
/// without losing it.
/// </summary>
public static class RetiredIntakeTablesSchema
{
    public const string Migration = "20261130000000_DropRetiredReanalyzeAndIntakeTables";

    public const string ReanalyzeReason = "reanalyze_job_retired";
    public const string IntakeReason = "product_intake_request_retired";

    public const string Up = $"""
        LOCK TABLE reanalyze_job, product_intake_request IN ACCESS EXCLUSIVE MODE;
        INSERT INTO legacy_archive (source_table, source_key, kind, payload, reason, archived_at)
        SELECT 'reanalyze_job', r.id::text, 'row', to_jsonb(r), '{ReanalyzeReason}', now() FROM reanalyze_job r ORDER BY r.id;
        INSERT INTO legacy_archive (source_table, source_key, kind, payload, reason, archived_at)
        SELECT 'product_intake_request', p.request_id, 'row', to_jsonb(p), '{IntakeReason}', now() FROM product_intake_request p ORDER BY p.request_id;
        DROP TABLE reanalyze_job;
        DROP TABLE product_intake_request;
        """;

    // Forward-only like v20 and v22-v27: EF runs each migration in its own transaction, so refusing first keeps a
    // downgrade attempt from changing anything.
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'retired_intake_tables_irreversible'; END $$;";

    /// <summary>Whether schema <paramref name="version"/> has <c>reanalyze_job</c>.</summary>
    internal static bool ReanalyzePresent(int version) => version is >= 3 and < 28;

    /// <summary>Whether schema <paramref name="version"/> has <c>product_intake_request</c>.</summary>
    internal static bool IntakePresent(int version) => version is >= 15 and < 28;
}
