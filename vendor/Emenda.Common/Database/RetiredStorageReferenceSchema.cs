namespace Emenda.Common.Database;

/// <summary>
/// The retired StorageReferences v1 snapshot table (ADR-0030 step 4). Kept only so the historical
/// <c>AddStorageReferenceSnapshots</c> migrations, their reversal and Mistake's exact schema check
/// for versions that still had the table keep working; nothing reads or writes the table any more.
/// </summary>
public static class RetiredStorageReferenceSchema
{
    public const string Table = "storage_reference_snapshot";

    public static readonly string[] Columns = [
        "storage_reference_snapshot|snapshot_id|uuid|N", "storage_reference_snapshot|requester|text|N",
        "storage_reference_snapshot|provider|text|N", "storage_reference_snapshot|version|text|N",
        "storage_reference_snapshot|created_at|timestamp with time zone|N", "storage_reference_snapshot|expires_at|timestamp with time zone|N",
        "storage_reference_snapshot|coverage|text|N", "storage_reference_snapshot|keys|jsonb|N",
        "storage_reference_snapshot|entry_count|integer|N", "storage_reference_snapshot|sha256|text|N"];
    public static readonly string[] Indexes = ["PK_storage_reference_snapshot|storage_reference_snapshot|snapshot_id|U", "IX_storage_reference_snapshot_expiry|storage_reference_snapshot|expires_at|N"];
    public static readonly string[] Constraints = ["PK_storage_reference_snapshot|p|snapshot_id|-|-|-|V"];
    public static readonly string[] Triggers = [
        "storage_reference_snapshot|trg_reference_snapshot|27|guard_storage_reference_snapshot|False|False",
        "storage_reference_snapshot|trg_reference_snapshot_truncate|34|guard_storage_reference_snapshot|False|False"];
    public const string GuardBody = """
        BEGIN
          IF TG_OP='DELETE' AND OLD.expires_at <= clock_timestamp() THEN RETURN OLD; END IF;
          RAISE EXCEPTION 'reference_snapshot_immutable';
        END
        """;
    public static readonly string Sql = """
        CREATE TABLE IF NOT EXISTS storage_reference_snapshot (
          snapshot_id uuid NOT NULL,
          requester text NOT NULL,
          provider text NOT NULL,
          version text NOT NULL,
          created_at timestamp with time zone NOT NULL,
          expires_at timestamp with time zone NOT NULL,
          coverage text NOT NULL,
          keys jsonb NOT NULL,
          entry_count integer NOT NULL,
          sha256 text NOT NULL,
          CONSTRAINT "PK_storage_reference_snapshot" PRIMARY KEY(snapshot_id)
        );
        CREATE INDEX IF NOT EXISTS "IX_storage_reference_snapshot_expiry" ON storage_reference_snapshot(expires_at);
        """ + $"""
        CREATE OR REPLACE FUNCTION guard_storage_reference_snapshot() RETURNS trigger LANGUAGE plpgsql AS $body$
        {GuardBody}
        $body$;
        DROP TRIGGER IF EXISTS trg_reference_snapshot ON storage_reference_snapshot;
        CREATE TRIGGER trg_reference_snapshot BEFORE UPDATE OR DELETE ON storage_reference_snapshot
          FOR EACH ROW EXECUTE FUNCTION guard_storage_reference_snapshot();
        DROP TRIGGER IF EXISTS trg_reference_snapshot_truncate ON storage_reference_snapshot;
        CREATE TRIGGER trg_reference_snapshot_truncate BEFORE TRUNCATE ON storage_reference_snapshot
          FOR EACH STATEMENT EXECUTE FUNCTION guard_storage_reference_snapshot();
        """;

    public const string DownSql = """
        DO $body$ BEGIN
          IF EXISTS(SELECT 1 FROM storage_reference_snapshot) THEN RAISE EXCEPTION 'nonempty_reference_snapshot_downgrade'; END IF;
        END $body$;
        DROP TABLE storage_reference_snapshot;
        DROP FUNCTION guard_storage_reference_snapshot();
        """;

    /// <summary>Drops the table and its guard function if they exist; snapshots are not business data.</summary>
    public const string DropSql = """
        DROP TABLE IF EXISTS storage_reference_snapshot;
        DROP FUNCTION IF EXISTS guard_storage_reference_snapshot();
        """;
}
