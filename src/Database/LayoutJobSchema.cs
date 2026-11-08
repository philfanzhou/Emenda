namespace Emenda.Database;

/// <summary>
/// Thirteenth versioned migration (IKQA2O): durable layout-detection jobs and accepted
/// results over an existing managed source. The job and its per-image rows carry the
/// lease/fence/heartbeat claim protocol (database clock, owner + fencing version, bounded
/// attempts); accepted results and the request-key replay record are immutable facts
/// guarded by triggers. The job stores the credential reference (the configuration key
/// name, never the credential) and the captured non-secret consumer processing
/// configuration. Down rules follow the source-related family: Down is refused while any
/// layout row exists, and an empty layout set drops cleanly.
/// </summary>
public static class LayoutJobSchema
{
    public const string Migration = "20261025000000_AddLayoutJobs";

    public const string Tables = """
        CREATE TABLE layout_job (
          job_id uuid NOT NULL,
          source_upload_id uuid NOT NULL,
          content_revision uuid NOT NULL,
          snapshot_id uuid NOT NULL,
          student_id text NOT NULL,
          actor text NOT NULL,
          result_revision integer NOT NULL,
          status text NOT NULL,
          manifest text NOT NULL,
          config text NOT NULL,
          credential_ref text NOT NULL,
          image_rotations jsonb NOT NULL,
          owner_id uuid NOT NULL,
          fence bigint NOT NULL,
          lease_until timestamptz,
          attempt integer NOT NULL,
          error_kind text,
          created_at timestamptz NOT NULL,
          completed_at timestamptz,
          CONSTRAINT "PK_layout_job" PRIMARY KEY(job_id),
          CONSTRAINT "FK_layout_job_source_intake" FOREIGN KEY(source_upload_id,content_revision)
            REFERENCES source_intake(source_upload_id,content_revision) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_layout_job_revision" ON layout_job(source_upload_id,content_revision,result_revision);
        CREATE INDEX "IX_layout_job_status" ON layout_job(status);
        CREATE TABLE layout_job_image (
          job_id uuid NOT NULL,
          image_path text NOT NULL,
          position integer NOT NULL,
          expected_length bigint NOT NULL,
          expected_sha256 text NOT NULL,
          status text NOT NULL,
          attempts integer NOT NULL,
          last_error text,
          retry_not_before timestamptz,
          updated_at timestamptz NOT NULL,
          CONSTRAINT "PK_layout_job_image" PRIMARY KEY(job_id,image_path),
          CONSTRAINT "FK_layout_job_image_layout_job" FOREIGN KEY(job_id) REFERENCES layout_job(job_id) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_layout_job_image_position" ON layout_job_image(job_id,position);
        CREATE TABLE layout_result (
          result_id uuid NOT NULL,
          job_id uuid NOT NULL,
          source_upload_id uuid NOT NULL,
          content_revision uuid NOT NULL,
          snapshot_id uuid NOT NULL,
          result_revision integer NOT NULL,
          image_path text NOT NULL,
          image_position integer NOT NULL,
          bytes_length bigint NOT NULL,
          bytes_sha256 text NOT NULL,
          source_rotation integer,
          attempt integer NOT NULL,
          upstream_request_id text NOT NULL,
          contract_id text NOT NULL,
          coordinate_frame text NOT NULL,
          width integer NOT NULL,
          height integer NOT NULL,
          orientation integer,
          engine text,
          model text,
          version text,
          diagnostics jsonb NOT NULL,
          config text NOT NULL,
          created_at timestamptz NOT NULL,
          CONSTRAINT "PK_layout_result" PRIMARY KEY(result_id),
          CONSTRAINT "FK_layout_result_layout_job" FOREIGN KEY(job_id) REFERENCES layout_job(job_id) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_layout_result_job_image" ON layout_result(job_id,image_path);
        CREATE UNIQUE INDEX "IX_layout_result_revision_image" ON layout_result(source_upload_id,content_revision,result_revision,image_path);
        CREATE INDEX "IX_layout_result_revision" ON layout_result(source_upload_id,content_revision,result_revision);
        CREATE TABLE layout_result_region (
          result_id uuid NOT NULL,
          region_id integer NOT NULL,
          label text NOT NULL,
          x0 double precision NOT NULL,
          y0 double precision NOT NULL,
          x1 double precision NOT NULL,
          y1 double precision NOT NULL,
          CONSTRAINT "PK_layout_result_region" PRIMARY KEY(result_id,region_id),
          CONSTRAINT "FK_layout_result_region_layout_result" FOREIGN KEY(result_id) REFERENCES layout_result(result_id) ON DELETE RESTRICT
        );
        CREATE TABLE layout_request (
          actor text NOT NULL,
          action text NOT NULL,
          request_key uuid NOT NULL,
          source_upload_id uuid NOT NULL,
          content_revision uuid NOT NULL,
          canonical text NOT NULL,
          job_id uuid NOT NULL,
          CONSTRAINT "PK_layout_request" PRIMARY KEY(actor,action,request_key),
          CONSTRAINT "FK_layout_request_layout_job" FOREIGN KEY(job_id) REFERENCES layout_job(job_id) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_layout_request_source_revision" ON layout_request(source_upload_id,content_revision);
        CREATE INDEX "IX_layout_request_job" ON layout_request(job_id);
        """;

    /// <summary>Accepted results, their regions and the request replay records are
    /// immutable once written: only inserts are legitimate, so any row UPDATE or DELETE
    /// is refused. Job and per-image rows are the mutable lease-protocol state and stay
    /// unguarded; their transitions are fenced by owner + fencing version in SQL.</summary>
    public const string ImmutableBody = """
        BEGIN
          RAISE EXCEPTION 'layout_result_managed' USING ERRCODE='P0001';
        END
        """;

    public const string TruncateBody = """
        BEGIN
          IF EXISTS(SELECT 1 FROM layout_result) OR EXISTS(SELECT 1 FROM layout_result_region)
            OR EXISTS(SELECT 1 FROM layout_request) THEN RAISE EXCEPTION 'layout_managed' USING ERRCODE='P0001'; END IF;
          RETURN NULL;
        END
        """;

    public static IReadOnlyDictionary<string, string> Bodies { get; } = new Dictionary<string, string>
    {
        ["guard_layout_immutable"] = ImmutableBody,
        ["guard_layout_truncate"] = TruncateBody
    };

    public static readonly string[] Triggers = [
        "layout_result|trg_layout_result|27|guard_layout_immutable|False|False",
        "layout_result_region|trg_layout_result_region|27|guard_layout_immutable|False|False",
        "layout_request|trg_layout_request|27|guard_layout_immutable|False|False",
        "layout_result|trg_layout_result_truncate|34|guard_layout_truncate|False|False",
        "layout_result_region|trg_layout_result_region_truncate|34|guard_layout_truncate|False|False",
        "layout_request|trg_layout_request_truncate|34|guard_layout_truncate|False|False"];

    public static string Guards => string.Join("\n", Bodies.Select(p =>
            $"CREATE FUNCTION {p.Key}() RETURNS trigger LANGUAGE plpgsql AS $body${p.Value}$body$;")) + "\n"
        + string.Join("\n", Triggers.Select(t =>
        {
            var v = t.Split('|');
            var events = v[2] == "34" ? "TRUNCATE" : "UPDATE OR DELETE";
            return $"CREATE TRIGGER {v[1]} BEFORE {events} ON {v[0]} FOR EACH {(v[2] == "34" ? "STATEMENT" : "ROW")} EXECUTE FUNCTION {v[3]}();";
        }));

    public static string Down => """
        LOCK TABLE layout_job,layout_job_image,layout_result,layout_result_region,layout_request IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN IF EXISTS(SELECT 1 FROM layout_job) OR EXISTS(SELECT 1 FROM layout_job_image) OR EXISTS(SELECT 1 FROM layout_result) OR EXISTS(SELECT 1 FROM layout_result_region) OR EXISTS(SELECT 1 FROM layout_request) THEN RAISE EXCEPTION 'Layout jobs prohibit schema downgrade while rows exist'; END IF; END $body$;
        """ + string.Join("\n", Triggers.Select(t => { var v = t.Split('|'); return $"DROP TRIGGER {v[1]} ON {v[0]};"; }))
        + "DROP TABLE layout_request,layout_result_region,layout_result,layout_job_image,layout_job;\n"
        + string.Join("\n", Bodies.Keys.Select(n => $"DROP FUNCTION {n}();"));
}
