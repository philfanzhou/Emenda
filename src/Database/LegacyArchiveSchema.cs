namespace Emenda.Database;

/// <summary>
/// Append-only archive for data that automatic legacy convergence removes or rewrites. Business code never reads it.
/// </summary>
public static class LegacyArchiveSchema
{
    public const string Migration = "20261008000001_AddLegacyArchive";

    public const string GuardBody = """
        BEGIN
          RAISE EXCEPTION 'legacy_archive_append_only';
        END
        """;

    public static readonly string Sql = $"""
        CREATE TABLE legacy_archive (
          id bigint GENERATED ALWAYS AS IDENTITY,
          source_table text NOT NULL,
          source_key text NOT NULL,
          kind text NOT NULL,
          payload jsonb NOT NULL,
          reason text NOT NULL,
          archived_at timestamp with time zone NOT NULL,
          CONSTRAINT "PK_legacy_archive" PRIMARY KEY (id)
        );
        CREATE INDEX "IX_legacy_archive_source_table_source_key" ON legacy_archive (source_table, source_key);
        CREATE FUNCTION guard_legacy_archive() RETURNS trigger LANGUAGE plpgsql AS $body$
        {GuardBody}
        $body$;
        CREATE TRIGGER trg_legacy_archive BEFORE UPDATE OR DELETE ON legacy_archive
          FOR EACH ROW EXECUTE FUNCTION guard_legacy_archive();
        CREATE TRIGGER trg_legacy_archive_truncate BEFORE TRUNCATE ON legacy_archive
          FOR EACH STATEMENT EXECUTE FUNCTION guard_legacy_archive();
        """;

    public const string DownSql = """
        LOCK TABLE legacy_archive IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN
          IF EXISTS(SELECT 1 FROM legacy_archive) THEN RAISE EXCEPTION 'Legacy archive rows prohibit schema downgrade'; END IF;
        END $body$;
        DROP TABLE legacy_archive;
        DROP FUNCTION guard_legacy_archive();
        """;

    internal static readonly string[] Relations = ["legacy_archive", "legacy_archive_id_seq"];

    internal static readonly string[] Columns = [
        "legacy_archive|id|bigint|N",
        "legacy_archive|source_table|text|N",
        "legacy_archive|source_key|text|N",
        "legacy_archive|kind|text|N",
        "legacy_archive|payload|jsonb|N",
        "legacy_archive|reason|text|N",
        "legacy_archive|archived_at|timestamp with time zone|N"];

    internal static readonly string[] Indexes = [
        "PK_legacy_archive|legacy_archive|id|U",
        "IX_legacy_archive_source_table_source_key|legacy_archive|source_table,source_key|N"];

    internal static readonly string[] Constraints = ["PK_legacy_archive|p|id|-|-|-|V"];

    internal static readonly string[] Triggers = [
        "legacy_archive|trg_legacy_archive|27|guard_legacy_archive|False|False",
        "legacy_archive|trg_legacy_archive_truncate|34|guard_legacy_archive|False|False"];
}
