namespace Ruoyu.Study.Mistake.Database;

public static class SourceTargetSchema
{
    public const string Migration = "20261003000001_AddSourceTargets";
    public const string Tables = """
        CREATE TABLE source_target_object (
          source_upload_id uuid NOT NULL, content_revision uuid NOT NULL, source_path text NOT NULL,
          snapshot_id uuid NOT NULL, student_id varchar(36) NOT NULL,
          expected_length bigint NOT NULL, expected_sha256 varchar(64) NOT NULL, selected_attempt_id uuid,
          CONSTRAINT "PK_source_target_object" PRIMARY KEY(source_upload_id,content_revision,source_path),
          CONSTRAINT "FK_source_target_object_intake" FOREIGN KEY(source_upload_id,content_revision)
            REFERENCES source_intake(source_upload_id,content_revision) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_source_target_object_selected_attempt_id" ON source_target_object(selected_attempt_id);
        CREATE TABLE source_target_attempt (
          attempt_id uuid NOT NULL, source_upload_id uuid NOT NULL, content_revision uuid NOT NULL, source_path text NOT NULL,
          target_key text NOT NULL, owner_id uuid NOT NULL, fence bigint NOT NULL, phase text NOT NULL,
          verified_length bigint, verified_sha256 varchar(64),
          CONSTRAINT "PK_source_target_attempt" PRIMARY KEY(attempt_id),
          CONSTRAINT "FK_source_target_attempt_object" FOREIGN KEY(source_upload_id,content_revision,source_path)
            REFERENCES source_target_object(source_upload_id,content_revision,source_path) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_source_target_attempt_key" ON source_target_attempt(target_key);
        CREATE INDEX "IX_source_target_attempt_object" ON source_target_attempt(source_upload_id,content_revision,source_path);
        ALTER TABLE source_target_object ADD CONSTRAINT "FK_source_target_object_selected" FOREIGN KEY(selected_attempt_id)
          REFERENCES source_target_attempt(attempt_id) ON DELETE RESTRICT;
        """;
    public const string ReservedBody = """
        BEGIN
          RETURN lower(input_path) LIKE 'mistakes/source-targets/%' OR lower(input_path) LIKE 'uploads/source-targets/%';
        END
        """;
    public const string ObjectBody = """
        DECLARE src source_intake; candidate source_target_attempt;
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'target_managed' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT src FROM source_intake WHERE source_upload_id=NEW.source_upload_id
            AND content_revision=NEW.content_revision FOR UPDATE;
          IF src.lease_until IS NULL OR src.lease_until<=clock_timestamp() THEN
            RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001';
          END IF;
          IF NEW.snapshot_id<>src.snapshot_id OR NEW.student_id<>src.student_id OR reserved_target_path(NEW.source_path)
            OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(src.manifest::jsonb->'images') i
              WHERE i->>'path'=NEW.source_path AND i->>'type'='mistake' AND (i->>'length')::bigint=NEW.expected_length
                AND i->>'sha256'=NEW.expected_sha256)
            OR NOT EXISTS(SELECT 1 FROM source_intake_group g JOIN mistake_item m ON m.id=g.item_id
              CROSS JOIN LATERAL jsonb_array_elements(g.regions) r
              WHERE g.source_upload_id=NEW.source_upload_id AND g.content_revision=NEW.content_revision
                AND r->>'sourceImagePath'=NEW.source_path AND m.source_regions=g.regions
                AND m.source_upload_id=NEW.source_upload_id::text AND m.student_id=NEW.student_id) THEN
            RAISE EXCEPTION 'target_binding' USING ERRCODE='P0001';
          END IF;
          IF TG_OP='INSERT' THEN
            IF NEW.selected_attempt_id IS NOT NULL THEN RAISE EXCEPTION 'target_unverified' USING ERRCODE='P0001'; END IF;
            RETURN NEW;
          END IF;
          IF (NEW.source_upload_id,NEW.content_revision,NEW.source_path,NEW.snapshot_id,NEW.student_id,NEW.expected_length,NEW.expected_sha256)
              IS DISTINCT FROM (OLD.source_upload_id,OLD.content_revision,OLD.source_path,OLD.snapshot_id,OLD.student_id,OLD.expected_length,OLD.expected_sha256)
            OR OLD.selected_attempt_id IS NOT NULL THEN
            RAISE EXCEPTION 'target_managed' USING ERRCODE='P0001';
          END IF;
          SELECT * INTO STRICT candidate FROM source_target_attempt WHERE attempt_id=NEW.selected_attempt_id;
          IF candidate.phase<>'Verified' OR candidate.verified_length<>NEW.expected_length
            OR candidate.verified_sha256<>NEW.expected_sha256
            OR (candidate.source_upload_id,candidate.content_revision,candidate.source_path)
              IS DISTINCT FROM (NEW.source_upload_id,NEW.content_revision,NEW.source_path)
            OR candidate.owner_id<>src.owner_id OR candidate.fence<>src.fence THEN
            RAISE EXCEPTION 'target_unverified' USING ERRCODE='P0001';
          END IF;
          RETURN NEW;
        END
        """;
    public const string AttemptBody = """
        DECLARE src source_intake; objectrow source_target_object;
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'target_managed' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT src FROM source_intake WHERE source_upload_id=NEW.source_upload_id
            AND content_revision=NEW.content_revision FOR UPDATE;
          SELECT * INTO STRICT objectrow FROM source_target_object WHERE source_upload_id=NEW.source_upload_id
            AND content_revision=NEW.content_revision AND source_path=NEW.source_path;
          IF src.lease_until IS NULL OR src.lease_until<=clock_timestamp() OR NEW.owner_id<>src.owner_id OR NEW.fence<>src.fence THEN
            RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001';
          END IF;
          IF NEW.target_key<>'mistakes/source-targets/'||NEW.source_upload_id::text||'/'||NEW.content_revision::text||'/'||NEW.attempt_id::text||'/original'
            OR NEW.phase NOT IN ('Reserved','Unknown','Copied','Verified') THEN
            RAISE EXCEPTION 'target_binding' USING ERRCODE='P0001';
          END IF;
          IF TG_OP='INSERT' THEN
            IF NEW.phase<>'Reserved' OR NEW.verified_length IS NOT NULL OR NEW.verified_sha256 IS NOT NULL THEN
              RAISE EXCEPTION 'target_unverified' USING ERRCODE='P0001';
            END IF;
          ELSE
            IF (NEW.attempt_id,NEW.source_upload_id,NEW.content_revision,NEW.source_path,NEW.target_key)
              IS DISTINCT FROM (OLD.attempt_id,OLD.source_upload_id,OLD.content_revision,OLD.source_path,OLD.target_key)
              OR objectrow.selected_attempt_id IS NOT NULL
              OR (OLD.phase='Verified' AND NEW.phase<>'Verified') THEN
              RAISE EXCEPTION 'target_managed' USING ERRCODE='P0001';
            END IF;
          END IF;
          IF NEW.phase='Verified' THEN
            IF NEW.verified_length IS DISTINCT FROM objectrow.expected_length OR NEW.verified_sha256 IS DISTINCT FROM objectrow.expected_sha256 THEN
              RAISE EXCEPTION 'target_unverified' USING ERRCODE='P0001';
            END IF;
          ELSIF NEW.verified_length IS NOT NULL OR NEW.verified_sha256 IS NOT NULL THEN
            RAISE EXCEPTION 'target_unverified' USING ERRCODE='P0001';
          END IF;
          RETURN NEW;
        END
        """;
    public const string LegacyBody = """
        DECLARE paths jsonb;
        BEGIN
          IF TG_TABLE_NAME='mistake_item' THEN paths:=NEW.source_regions;
          ELSIF TG_TABLE_NAME='mistake_practice' THEN paths:=NEW.image_paths;
          ELSE paths:=NEW.manifest::jsonb->'images'; END IF;
          IF EXISTS(SELECT 1 FROM jsonb_array_elements(paths) p
            WHERE reserved_target_path(CASE WHEN jsonb_typeof(p)='string' THEN p#>>'{}'
              WHEN TG_TABLE_NAME='source_intake' THEN p->>'path' ELSE p->>'sourceImagePath' END)) THEN
            RAISE EXCEPTION 'reserved_target' USING ERRCODE='P0001';
          END IF;
          RETURN NEW;
        END
        """;
    public const string TruncateBody = """
        BEGIN
          IF EXISTS(SELECT 1 FROM source_target_object) OR EXISTS(SELECT 1 FROM source_target_attempt) THEN
            RAISE EXCEPTION 'target_managed' USING ERRCODE='P0001';
          END IF;
          RETURN NULL;
        END
        """;
    public static IReadOnlyDictionary<string,string> Bodies { get; } = new Dictionary<string,string>
    {
        ["reserved_target_path"]=ReservedBody,["guard_target_object"]=ObjectBody,["guard_target_attempt"]=AttemptBody,
        ["guard_reserved_target_input"]=LegacyBody,["guard_target_truncate"]=TruncateBody
    };
    public static readonly string[] Triggers = [
        "source_target_object|trg_target_object|31|guard_target_object|False|False",
        "source_target_attempt|trg_target_attempt|31|guard_target_attempt|False|False",
        "source_target_object|trg_target_object_truncate|34|guard_target_truncate|False|False",
        "source_target_attempt|trg_target_attempt_truncate|34|guard_target_truncate|False|False",
        "mistake_item|trg_reserved_target_item|23|guard_reserved_target_input|False|False",
        "mistake_practice|trg_reserved_target_practice|23|guard_reserved_target_input|False|False",
        "source_intake|trg_reserved_target_intake|23|guard_reserved_target_input|False|False"];
    public static string Guards => string.Join("\n",Bodies.Select(p=>$"CREATE FUNCTION {p.Key}({(p.Key=="reserved_target_path" ? "input_path text" : "")}) RETURNS {(p.Key=="reserved_target_path" ? "boolean LANGUAGE plpgsql IMMUTABLE" : "trigger LANGUAGE plpgsql")} AS $body${p.Value}$body$;")) + "\n" + """
        CREATE TRIGGER trg_target_object BEFORE INSERT OR UPDATE OR DELETE ON source_target_object FOR EACH ROW EXECUTE FUNCTION guard_target_object();
        CREATE TRIGGER trg_target_attempt BEFORE INSERT OR UPDATE OR DELETE ON source_target_attempt FOR EACH ROW EXECUTE FUNCTION guard_target_attempt();
        CREATE TRIGGER trg_target_object_truncate BEFORE TRUNCATE ON source_target_object FOR EACH STATEMENT EXECUTE FUNCTION guard_target_truncate();
        CREATE TRIGGER trg_target_attempt_truncate BEFORE TRUNCATE ON source_target_attempt FOR EACH STATEMENT EXECUTE FUNCTION guard_target_truncate();
        CREATE TRIGGER trg_reserved_target_item BEFORE INSERT OR UPDATE ON mistake_item FOR EACH ROW EXECUTE FUNCTION guard_reserved_target_input();
        CREATE TRIGGER trg_reserved_target_practice BEFORE INSERT OR UPDATE ON mistake_practice FOR EACH ROW EXECUTE FUNCTION guard_reserved_target_input();
        CREATE TRIGGER trg_reserved_target_intake BEFORE INSERT OR UPDATE ON source_intake FOR EACH ROW EXECUTE FUNCTION guard_reserved_target_input();
        """;
    public static string Down => """
        LOCK TABLE source_target_object,source_target_attempt IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN
          IF EXISTS(SELECT 1 FROM source_target_object) OR EXISTS(SELECT 1 FROM source_target_attempt) THEN
            RAISE EXCEPTION 'Source targets prohibit schema downgrade';
          END IF;
        END $body$;
        DROP TRIGGER trg_reserved_target_item ON mistake_item;
        DROP TRIGGER trg_reserved_target_practice ON mistake_practice;
        DROP TRIGGER trg_reserved_target_intake ON source_intake;
        ALTER TABLE source_target_object DROP CONSTRAINT "FK_source_target_object_selected";
        DROP TABLE source_target_attempt,source_target_object;
        """+string.Join("\n",Bodies.Keys.Select(n=>$"DROP FUNCTION {n}({(n=="reserved_target_path" ? "text" : "")});"));
}
