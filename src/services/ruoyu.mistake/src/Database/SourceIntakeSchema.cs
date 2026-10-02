namespace Ruoyu.Study.Mistake.Database;

public static class SourceIntakeSchema
{
    public const string Migration = "20261002120000_AddSourceIntake";
    public const string Tables = """
        CREATE TABLE source_intake (
          source_upload_id uuid NOT NULL,
          content_revision uuid NOT NULL,
          snapshot_id uuid NOT NULL,
          student_id varchar(36) NOT NULL,
          manifest text NOT NULL,
          mode text NOT NULL,
          owner_id uuid NOT NULL,
          fence bigint NOT NULL,
          lease_until timestamptz,
          materialized boolean NOT NULL,
          CONSTRAINT "PK_source_intake" PRIMARY KEY(source_upload_id,content_revision)
        );
        CREATE TABLE source_intake_group (
          item_id uuid NOT NULL,
          source_upload_id uuid NOT NULL,
          content_revision uuid NOT NULL,
          group_hash varchar(64) NOT NULL,
          canonical text NOT NULL,
          atoms text[] NOT NULL,
          regions jsonb NOT NULL,
          payload text NOT NULL,
          result text NOT NULL,
          created_log_id uuid NOT NULL,
          CONSTRAINT "PK_source_intake_group" PRIMARY KEY(item_id),
          CONSTRAINT "FK_source_intake_group_source_intake" FOREIGN KEY(source_upload_id,content_revision)
            REFERENCES source_intake(source_upload_id,content_revision) ON DELETE RESTRICT,
          CONSTRAINT "FK_source_intake_group_item" FOREIGN KEY(item_id)
            REFERENCES mistake_item(id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
          CONSTRAINT "FK_source_intake_group_created_log" FOREIGN KEY(created_log_id)
            REFERENCES process_log(id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
        );
        CREATE INDEX "IX_source_intake_group_source_hash" ON source_intake_group(source_upload_id,content_revision,group_hash);
        CREATE INDEX "IX_source_intake_group_created_log_id" ON source_intake_group(created_log_id);
        CREATE TABLE source_intake_request (
          actor text NOT NULL,
          action text NOT NULL,
          request_key uuid NOT NULL,
          source_upload_id uuid NOT NULL,
          content_revision uuid NOT NULL,
          canonical text NOT NULL,
          item_ids uuid[],
          CONSTRAINT "PK_source_intake_request" PRIMARY KEY(actor,action,request_key),
          CONSTRAINT "FK_source_intake_request_source_intake" FOREIGN KEY(source_upload_id,content_revision)
            REFERENCES source_intake(source_upload_id,content_revision) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_source_intake_request_source_revision" ON source_intake_request(source_upload_id,content_revision);
        """;

    // Both the claim and every legacy insert use the same source lock. It also covers absent rows.
    public const string SourceKeyBody = """
        BEGIN
          RETURN input_source::uuid::text;
        EXCEPTION WHEN invalid_text_representation THEN RETURN input_source;
        END
        """;
    public const string SourceLockBody = """
        BEGIN
          IF TG_OP='INSERT' THEN
            PERFORM pg_advisory_xact_lock(hashtextextended(NEW.source_upload_id::text,791249));
            IF EXISTS(SELECT 1 FROM mistake_item WHERE intake_source_key(source_upload_id)=NEW.source_upload_id::text) THEN
              RAISE EXCEPTION 'legacy_source_conflict' USING ERRCODE='P0001';
            END IF;
            IF NEW.mode NOT IN ('Automatic','Manual') OR NEW.fence<>1 OR NEW.materialized
              OR NEW.lease_until IS NULL OR NEW.lease_until<=clock_timestamp() THEN
              RAISE EXCEPTION 'invalid_intake' USING ERRCODE='P0001';
            END IF;
            RETURN NEW;
          END IF;
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001'; END IF;
          IF (NEW.source_upload_id,NEW.content_revision,NEW.snapshot_id,NEW.student_id,NEW.manifest,NEW.mode)
             IS DISTINCT FROM (OLD.source_upload_id,OLD.content_revision,OLD.snapshot_id,OLD.student_id,OLD.manifest,OLD.mode)
             OR (OLD.materialized AND NOT NEW.materialized) OR NEW.fence<OLD.fence THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          IF NEW.owner_id IS DISTINCT FROM OLD.owner_id THEN
            IF OLD.lease_until>clock_timestamp() OR NEW.fence<>OLD.fence+1 THEN
              RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001';
            END IF;
          ELSIF NEW.fence<>OLD.fence THEN
            RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001';
          END IF;
          RETURN NEW;
        END
        """;
    public const string ItemBody = """
        DECLARE source_key text; managed boolean;
        BEGIN
          source_key := intake_source_key(CASE WHEN TG_OP='DELETE' THEN OLD.source_upload_id ELSE NEW.source_upload_id END);
          PERFORM pg_advisory_xact_lock(hashtextextended(source_key,791249));
          managed := EXISTS(SELECT 1 FROM source_intake WHERE source_upload_id::text=source_key);
          IF TG_OP='INSERT' AND managed THEN
            IF NOT EXISTS(SELECT 1 FROM source_intake_group g JOIN source_intake s
                USING(source_upload_id,content_revision)
                WHERE g.item_id=NEW.id AND s.student_id=NEW.student_id
                  AND g.regions=NEW.source_regions AND s.lease_until>clock_timestamp()) THEN
              RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
            END IF;
          ELSIF TG_OP<>'INSERT' AND EXISTS(SELECT 1 FROM source_intake_group WHERE item_id=OLD.id) THEN
            IF TG_OP='DELETE' THEN RAISE EXCEPTION 'handoff_pending' USING ERRCODE='P0001'; END IF;
            IF (NEW.id,NEW.student_id,NEW.source_upload_id,NEW.source_regions,NEW.created_at)
               IS DISTINCT FROM (OLD.id,OLD.student_id,OLD.source_upload_id,OLD.source_regions,OLD.created_at)
               OR NEW.review_status=3 THEN
              RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
            END IF;
          ELSIF TG_OP='UPDATE' AND managed THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          IF TG_OP='DELETE' THEN RETURN OLD; END IF;
          RETURN NEW;
        END
        """;
    public const string GroupBody = """
        DECLARE src source_intake;
        BEGIN
          IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT src FROM source_intake WHERE source_upload_id=NEW.source_upload_id
            AND content_revision=NEW.content_revision FOR UPDATE;
          IF src.lease_until IS NULL OR src.lease_until<=clock_timestamp()
             OR (src.mode='Automatic' AND src.materialized) THEN
            RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001';
          END IF;
          IF cardinality(NEW.atoms)=0 OR jsonb_typeof(NEW.regions)<>'array'
             OR jsonb_array_length(NEW.regions)=0 THEN
            RAISE EXCEPTION 'invalid_group' USING ERRCODE='P0001';
          END IF;
          IF EXISTS(SELECT 1 FROM source_intake_group WHERE source_upload_id=NEW.source_upload_id
              AND content_revision=NEW.content_revision AND (canonical=NEW.canonical OR atoms && NEW.atoms)) THEN
            RAISE EXCEPTION 'grouping_conflict' USING ERRCODE='P0001';
          END IF;
          RETURN NEW;
        END
        """;
    public const string RequestBody = """
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001'; END IF;
          IF NEW.item_ids IS NOT NULL AND cardinality(NEW.item_ids)=0 THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          IF TG_OP='INSERT' THEN RETURN NEW; END IF;
          IF (NEW.actor,NEW.action,NEW.request_key,NEW.source_upload_id,NEW.content_revision,NEW.canonical)
              IS DISTINCT FROM (OLD.actor,OLD.action,OLD.request_key,OLD.source_upload_id,OLD.content_revision,OLD.canonical)
             OR (OLD.item_ids IS NOT NULL AND NEW.item_ids IS DISTINCT FROM OLD.item_ids) THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          RETURN NEW;
        END
        """;
    public const string IntegrityBody = """
        DECLARE g source_intake_group; logrow process_log; itemrow mistake_item;
        BEGIN
          FOR g IN SELECT * FROM source_intake_group LOOP
            SELECT * INTO STRICT itemrow FROM mistake_item WHERE id=g.item_id;
            SELECT * INTO STRICT logrow FROM process_log WHERE id=g.created_log_id;
            IF itemrow.source_upload_id<>g.source_upload_id::text OR itemrow.source_regions<>g.regions
               OR itemrow.student_id<>(SELECT student_id FROM source_intake WHERE source_upload_id=g.source_upload_id AND content_revision=g.content_revision)
               OR logrow.mistake_item_id<>g.item_id OR logrow.action<>1
               OR (SELECT count(*) FROM process_log WHERE mistake_item_id=g.item_id AND action=1)<>1 THEN
              RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
            END IF;
          END LOOP;
          IF EXISTS(SELECT 1 FROM source_intake_request r CROSS JOIN LATERAL unnest(r.item_ids) i
              WHERE NOT EXISTS(SELECT 1 FROM source_intake_group mapped WHERE mapped.item_id=i
                AND mapped.source_upload_id=r.source_upload_id AND mapped.content_revision=r.content_revision)) THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          RETURN NULL;
        END
        """;
    public const string CreatedLogBody = """
        BEGIN
          IF EXISTS(SELECT 1 FROM source_intake_group WHERE created_log_id=OLD.id) THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          IF TG_OP='DELETE' THEN RETURN OLD; END IF;
          RETURN NEW;
        END
        """;
    public const string TruncateBody = """
        BEGIN
          IF EXISTS(SELECT 1 FROM source_intake) THEN
            RAISE EXCEPTION 'source_managed' USING ERRCODE='P0001';
          END IF;
          RETURN NULL;
        END
        """;

    public static IReadOnlyDictionary<string,string> Bodies { get; } = new Dictionary<string,string>
    {
        ["intake_source_key"] = SourceKeyBody, ["guard_source_intake"] = SourceLockBody, ["guard_managed_item"] = ItemBody,
        ["guard_intake_group"] = GroupBody, ["guard_intake_request"] = RequestBody,
        ["guard_intake_integrity"] = IntegrityBody, ["guard_intake_created_log"] = CreatedLogBody, ["guard_intake_truncate"] = TruncateBody
    };
    public static string Guards => string.Join("\n", Bodies.Select(pair =>
        $"CREATE FUNCTION {pair.Key}({(pair.Key == "intake_source_key" ? "input_source text" : "")}) RETURNS {(pair.Key == "intake_source_key" ? "text LANGUAGE plpgsql IMMUTABLE" : "trigger LANGUAGE plpgsql")} AS $body${pair.Value}$body$;")) + "\n" + """
        CREATE TRIGGER trg_source_intake BEFORE INSERT OR UPDATE OR DELETE ON source_intake FOR EACH ROW EXECUTE FUNCTION guard_source_intake();
        CREATE TRIGGER trg_managed_item BEFORE INSERT OR UPDATE OR DELETE ON mistake_item FOR EACH ROW EXECUTE FUNCTION guard_managed_item();
        CREATE TRIGGER trg_intake_group BEFORE INSERT OR UPDATE OR DELETE ON source_intake_group FOR EACH ROW EXECUTE FUNCTION guard_intake_group();
        CREATE TRIGGER trg_intake_request BEFORE INSERT OR UPDATE OR DELETE ON source_intake_request FOR EACH ROW EXECUTE FUNCTION guard_intake_request();
        CREATE CONSTRAINT TRIGGER trg_intake_integrity_group AFTER INSERT ON source_intake_group DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION guard_intake_integrity();
        CREATE CONSTRAINT TRIGGER trg_intake_integrity_log AFTER INSERT OR UPDATE OR DELETE ON process_log DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION guard_intake_integrity();
        CREATE CONSTRAINT TRIGGER trg_intake_integrity_request AFTER INSERT OR UPDATE ON source_intake_request DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION guard_intake_integrity();
        CREATE TRIGGER trg_intake_created_log BEFORE UPDATE OR DELETE ON process_log FOR EACH ROW EXECUTE FUNCTION guard_intake_created_log();
        CREATE TRIGGER trg_intake_truncate BEFORE TRUNCATE ON source_intake FOR EACH STATEMENT EXECUTE FUNCTION guard_intake_truncate();
        CREATE TRIGGER trg_group_truncate BEFORE TRUNCATE ON source_intake_group FOR EACH STATEMENT EXECUTE FUNCTION guard_intake_truncate();
        CREATE TRIGGER trg_request_truncate BEFORE TRUNCATE ON source_intake_request FOR EACH STATEMENT EXECUTE FUNCTION guard_intake_truncate();
        CREATE TRIGGER trg_item_truncate BEFORE TRUNCATE ON mistake_item FOR EACH STATEMENT EXECUTE FUNCTION guard_intake_truncate();
        CREATE TRIGGER trg_log_truncate BEFORE TRUNCATE ON process_log FOR EACH STATEMENT EXECUTE FUNCTION guard_intake_truncate();
        """;
    public static string Down => """
        LOCK TABLE source_intake,mistake_item IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN
          IF EXISTS(SELECT 1 FROM source_intake) THEN RAISE EXCEPTION 'Source intakes prohibit schema downgrade'; END IF;
        END $body$;
        DROP TRIGGER trg_managed_item ON mistake_item;
        DROP TRIGGER trg_item_truncate ON mistake_item;
        DROP TRIGGER trg_intake_integrity_log ON process_log;
        DROP TRIGGER trg_intake_created_log ON process_log;
        DROP TRIGGER trg_log_truncate ON process_log;
        DROP TABLE source_intake_request,source_intake_group,source_intake;
        """ + string.Join("\n", Bodies.Keys.Select(name => $"DROP FUNCTION {name}({(name == "intake_source_key" ? "text" : "")});"));
}
