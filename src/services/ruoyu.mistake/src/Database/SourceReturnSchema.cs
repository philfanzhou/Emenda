namespace Ruoyu.Study.Mistake.Database;

public static class SourceReturnSchema
{
    public const string Migration = "20261003010000_AddSourceReturns";
    public const string Tables = """
        CREATE TABLE source_return (
          item_id uuid NOT NULL, operation_id text NOT NULL, source_upload_id uuid NOT NULL, content_revision uuid NOT NULL,
          snapshot_id uuid NOT NULL, student_id text NOT NULL, actor text NOT NULL, canonical text NOT NULL, original_item text NOT NULL,
          return_reason text NOT NULL, root_cause text, grade integer, subject integer, owner_id uuid NOT NULL, fence bigint NOT NULL,
          phase text NOT NULL, payload text, payload_digest text, error_kind text, confirmed_record_id uuid,
          receipt_created_at timestamptz, completed_at timestamptz, final_log_id uuid,
          CONSTRAINT "PK_source_return" PRIMARY KEY(item_id),
          CONSTRAINT "FK_source_return_group" FOREIGN KEY(item_id) REFERENCES source_intake_group(item_id) ON DELETE RESTRICT,
          CONSTRAINT "FK_source_return_intake" FOREIGN KEY(source_upload_id,content_revision) REFERENCES source_intake(source_upload_id,content_revision) ON DELETE RESTRICT,
          CONSTRAINT "FK_source_return_log" FOREIGN KEY(final_log_id) REFERENCES process_log(id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
        );
        CREATE UNIQUE INDEX "IX_source_return_operation" ON source_return(operation_id);
        CREATE INDEX "IX_source_return_phase" ON source_return(phase);
        CREATE INDEX "IX_source_return_SourceUploadId_ContentRevision" ON source_return(source_upload_id,content_revision);
        CREATE INDEX "IX_source_return_FinalLogId" ON source_return(final_log_id);
        CREATE TABLE source_return_object (
          item_id uuid NOT NULL, position integer NOT NULL, source_path text NOT NULL, target_key text NOT NULL,
          expected_length bigint NOT NULL, expected_sha256 text NOT NULL, selected_attempt_id uuid,
          CONSTRAINT "PK_source_return_object" PRIMARY KEY(item_id,position),
          CONSTRAINT "FK_source_return_object_return" FOREIGN KEY(item_id) REFERENCES source_return(item_id) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_source_return_object_SelectedAttemptId" ON source_return_object(selected_attempt_id);
        CREATE TABLE source_return_attempt (
          attempt_id uuid NOT NULL, item_id uuid NOT NULL, position integer NOT NULL, object_key text NOT NULL,
          owner_id uuid NOT NULL, fence bigint NOT NULL, phase text NOT NULL, copy_settled_at timestamptz,
          verified_length bigint, verified_sha256 text,
          CONSTRAINT "PK_source_return_attempt" PRIMARY KEY(attempt_id),
          CONSTRAINT "FK_source_return_attempt_object" FOREIGN KEY(item_id,position) REFERENCES source_return_object(item_id,position) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_source_return_attempt_key" ON source_return_attempt(object_key);
        CREATE INDEX "IX_source_return_attempt_object" ON source_return_attempt(item_id,position);
        ALTER TABLE source_return_object ADD CONSTRAINT "FK_source_return_object_selected" FOREIGN KEY(selected_attempt_id) REFERENCES source_return_attempt(attempt_id) ON DELETE RESTRICT;
        """;
    public const string DigestBody = """
        DECLARE canonical text:=''; part text; utf16length bigint; pieces text[];
        BEGIN
          pieces:=ARRAY[input_student,COALESCE(input_payload::jsonb->>'comments',''),COALESCE(input_payload::jsonb->>'returnReason','')];
          FOR part IN SELECT ordered.segment FROM jsonb_array_elements(input_payload::jsonb->'imageEntries') WITH ORDINALITY images(entry,image_ord)
            CROSS JOIN LATERAL (VALUES(1,entry->>'path'),(2,entry->>'type')) ordered(segment_ord,segment) ORDER BY image_ord,segment_ord LOOP
            pieces:=array_append(pieces,part);
          END LOOP;
          FOREACH part IN ARRAY pieces LOOP
            SELECT COALESCE(sum(CASE WHEN c='' THEN 0 WHEN ascii(c)>65535 THEN 2 ELSE 1 END),0) INTO utf16length FROM regexp_split_to_table(part,'') c;
            canonical:=canonical||utf16length::text||':'||part||'|';
          END LOOP;
          RETURN encode(sha256(convert_to(canonical,'UTF8')),'hex');
        END
        """;
    public const string OperationBody = """
        DECLARE src source_intake; g source_intake_group; m mistake_item;
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          PERFORM pg_advisory_xact_lock(hashtextextended(NEW.source_upload_id::text,791249));
          SELECT * INTO STRICT src FROM source_intake WHERE source_upload_id=NEW.source_upload_id AND content_revision=NEW.content_revision FOR UPDATE;
          IF src.owner_id<>NEW.owner_id OR src.fence<>NEW.fence OR src.lease_until IS NULL OR src.lease_until<=clock_timestamp() THEN
            RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT g FROM source_intake_group WHERE item_id=NEW.item_id;
          SELECT * INTO STRICT m FROM mistake_item WHERE id=NEW.item_id FOR UPDATE;
          IF (NEW.source_upload_id,NEW.content_revision,NEW.snapshot_id,NEW.student_id)
             IS DISTINCT FROM (g.source_upload_id,g.content_revision,src.snapshot_id,src.student_id)
            OR m.student_id<>src.student_id OR m.source_regions<>g.regions OR m.source_upload_id<>src.source_upload_id::text
            OR NEW.operation_id<>'mistake-return:'||NEW.item_id::text OR NEW.actor='' OR NEW.phase NOT IN ('Prepared','Sending','Completed','Conflict')
            OR (NEW.grade IS NOT NULL AND NEW.grade NOT BETWEEN 1 AND 12) OR (NEW.subject IS NOT NULL AND NEW.subject NOT BETWEEN 1 AND 9) THEN
            RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='INSERT' THEN
            IF NEW.original_item::jsonb->>'id' IS DISTINCT FROM m.id::text OR NEW.original_item::jsonb->>'studentId' IS DISTINCT FROM m.student_id
              OR NEW.original_item::jsonb->'sourceRegions' IS DISTINCT FROM m.source_regions
              OR (NEW.original_item::jsonb->>'grade')::int IS DISTINCT FROM m.grade OR (NEW.original_item::jsonb->>'subject')::int IS DISTINCT FROM m.subject
              OR NEW.original_item::jsonb->>'rootCause' IS DISTINCT FROM m.root_cause
              OR m.review_status<>1 OR NEW.phase<>'Prepared' OR NEW.payload IS NOT NULL OR NEW.payload_digest IS NOT NULL
              OR NEW.confirmed_record_id IS NOT NULL OR NEW.receipt_created_at IS NOT NULL OR NEW.completed_at IS NOT NULL OR NEW.final_log_id IS NOT NULL THEN
              RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
            IF EXISTS(SELECT 1 FROM reanalyze_job WHERE item_id=NEW.item_id AND status IN (1,2)) THEN
              RAISE EXCEPTION 'reanalysis_active' USING ERRCODE='P0001'; END IF;
          ELSE
            IF (NEW.item_id,NEW.operation_id,NEW.source_upload_id,NEW.content_revision,NEW.snapshot_id,NEW.student_id,NEW.actor,NEW.canonical,
                NEW.original_item,NEW.return_reason,NEW.root_cause,NEW.grade,NEW.subject)
              IS DISTINCT FROM (OLD.item_id,OLD.operation_id,OLD.source_upload_id,OLD.content_revision,OLD.snapshot_id,OLD.student_id,OLD.actor,OLD.canonical,
                OLD.original_item,OLD.return_reason,OLD.root_cause,OLD.grade,OLD.subject)
              OR OLD.phase IN ('Completed','Conflict') OR (OLD.payload IS NOT NULL AND (NEW.payload,NEW.payload_digest) IS DISTINCT FROM (OLD.payload,OLD.payload_digest))
              OR (OLD.confirmed_record_id IS NOT NULL AND (NEW.confirmed_record_id,NEW.receipt_created_at) IS DISTINCT FROM (OLD.confirmed_record_id,OLD.receipt_created_at))
              OR (OLD.phase='Sending' AND NEW.phase='Prepared') THEN
              RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          END IF;
          IF (NEW.payload IS NULL)<>(NEW.payload_digest IS NULL) OR (NEW.payload_digest IS NOT NULL AND NEW.payload_digest !~ '^[0-9a-f]{64}$') THEN
            RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
          IF NEW.phase IN ('Sending','Completed') AND NEW.payload IS NULL THEN RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
          IF NEW.phase='Completed' THEN
            IF NEW.confirmed_record_id IS NULL OR NEW.confirmed_record_id='00000000-0000-0000-0000-000000000000'::uuid
              OR NEW.receipt_created_at IS NULL OR NOT isfinite(NEW.receipt_created_at) OR NEW.completed_at IS NULL OR NOT isfinite(NEW.completed_at)
              OR NEW.final_log_id IS NULL THEN RAISE EXCEPTION 'return_receipt_binding' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.confirmed_record_id IS NOT NULL OR NEW.receipt_created_at IS NOT NULL OR NEW.completed_at IS NOT NULL OR NEW.final_log_id IS NOT NULL THEN
            RAISE EXCEPTION 'return_receipt_binding' USING ERRCODE='P0001'; END IF;
          RETURN NEW;
        END
        """;
    public const string ObjectBody = """
        DECLARE op source_return; t source_target_object; a source_target_attempt; candidate source_return_attempt; paths text[];
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT op FROM source_return WHERE item_id=NEW.item_id;
          PERFORM pg_advisory_xact_lock(hashtextextended(op.source_upload_id::text,791249));
          IF NOT EXISTS(SELECT 1 FROM source_intake WHERE source_upload_id=op.source_upload_id AND content_revision=op.content_revision
            AND owner_id=op.owner_id AND fence=op.fence AND lease_until>clock_timestamp()) THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          SELECT array_agg(path ORDER BY ordinal) INTO paths FROM
            (SELECT r->>'sourceImagePath' path,min(ord) ordinal FROM source_intake_group g,
              jsonb_array_elements(g.regions) WITH ORDINALITY p(r,ord) WHERE g.item_id=NEW.item_id GROUP BY r->>'sourceImagePath') ordered;
          IF op.phase<>'Prepared' OR NEW.position<0 OR NEW.source_path IS DISTINCT FROM paths[NEW.position+1] THEN RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT t FROM source_target_object WHERE source_upload_id=op.source_upload_id AND content_revision=op.content_revision AND source_path=NEW.source_path;
          SELECT * INTO STRICT a FROM source_target_attempt WHERE attempt_id=t.selected_attempt_id;
          IF a.phase<>'Verified' OR NEW.target_key<>a.target_key OR NEW.expected_length<>t.expected_length OR NEW.expected_sha256<>t.expected_sha256 THEN
            RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='INSERT' THEN
            IF NEW.selected_attempt_id IS NOT NULL THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSE
            IF (NEW.item_id,NEW.position,NEW.source_path,NEW.target_key,NEW.expected_length,NEW.expected_sha256)
              IS DISTINCT FROM (OLD.item_id,OLD.position,OLD.source_path,OLD.target_key,OLD.expected_length,OLD.expected_sha256)
              OR OLD.selected_attempt_id IS NOT NULL THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
            SELECT * INTO STRICT candidate FROM source_return_attempt WHERE attempt_id=NEW.selected_attempt_id;
            IF (candidate.item_id,candidate.position) IS DISTINCT FROM (NEW.item_id,NEW.position) OR candidate.phase<>'Verified'
              OR candidate.copy_settled_at IS NULL OR candidate.verified_length IS DISTINCT FROM NEW.expected_length
              OR candidate.verified_sha256 IS DISTINCT FROM NEW.expected_sha256 THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          END IF;
          RETURN NEW;
        END
        """;
    public const string AttemptBody = """
        DECLARE op source_return; obj source_return_object;
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT op FROM source_return WHERE item_id=NEW.item_id;
          PERFORM pg_advisory_xact_lock(hashtextextended(op.source_upload_id::text,791249));
          IF NOT EXISTS(SELECT 1 FROM source_intake WHERE source_upload_id=op.source_upload_id AND content_revision=op.content_revision
            AND owner_id=op.owner_id AND fence=op.fence AND lease_until>clock_timestamp()) THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT obj FROM source_return_object WHERE item_id=NEW.item_id AND position=NEW.position;
          IF op.phase<>'Prepared' OR obj.selected_attempt_id IS NOT NULL
            OR NEW.object_key<>'uploads/mistake-returns/'||NEW.item_id::text||'/'||NEW.attempt_id::text||'/original.jpg'
            OR NEW.phase NOT IN ('Reserved','Unknown','CopySettled','Verified') THEN RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='INSERT' THEN
            IF NEW.phase<>'Reserved' OR NEW.copy_settled_at IS NOT NULL OR NEW.verified_length IS NOT NULL OR NEW.verified_sha256 IS NOT NULL
              OR NEW.owner_id<>op.owner_id OR NEW.fence<>op.fence THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSE
            IF (NEW.attempt_id,NEW.item_id,NEW.position,NEW.object_key,NEW.owner_id,NEW.fence)
              IS DISTINCT FROM (OLD.attempt_id,OLD.item_id,OLD.position,OLD.object_key,OLD.owner_id,OLD.fence)
              OR OLD.phase='Verified' OR (OLD.copy_settled_at IS NOT NULL AND NEW.copy_settled_at IS DISTINCT FROM OLD.copy_settled_at)
              OR (OLD.phase='Unknown' AND NEW.phase<>'Unknown') THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
            IF OLD.copy_settled_at IS NULL AND NEW.copy_settled_at IS NOT NULL AND
              (OLD.phase<>'Reserved' OR NEW.phase<>'CopySettled' OR NEW.owner_id<>op.owner_id OR NEW.fence<>op.fence) THEN
              RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          END IF;
          IF NEW.phase IN ('CopySettled','Verified') AND (NEW.copy_settled_at IS NULL OR NOT isfinite(NEW.copy_settled_at)) THEN
            RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          IF NEW.phase='Verified' THEN
            IF NEW.verified_length IS DISTINCT FROM obj.expected_length OR NEW.verified_sha256 IS DISTINCT FROM obj.expected_sha256 THEN
              RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.verified_length IS NOT NULL OR NEW.verified_sha256 IS NOT NULL THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          RETURN NEW;
        END
        """;
    public const string WriterBody = """
        DECLARE itemid uuid; op source_return; sourcekey text;
        BEGIN
          IF TG_TABLE_NAME='mistake_item' THEN itemid:=CASE WHEN TG_OP='DELETE' THEN OLD.id ELSE NEW.id END;
          ELSIF TG_TABLE_NAME='reanalyze_job' THEN itemid:=CASE WHEN TG_OP='DELETE' THEN OLD.item_id ELSE NEW.item_id END;
          ELSE itemid:=CASE WHEN TG_OP='DELETE' THEN OLD.mistake_item_id ELSE NEW.mistake_item_id END; END IF;
          SELECT source_upload_id::text INTO sourcekey FROM source_intake_group WHERE item_id=itemid;
          IF sourcekey IS NULL THEN IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF; END IF;
          PERFORM pg_advisory_xact_lock(hashtextextended(sourcekey,791249));
          SELECT * INTO op FROM source_return WHERE item_id=itemid;
          IF NOT FOUND THEN
            IF TG_TABLE_NAME='mistake_item' AND TG_OP='UPDATE' THEN
              IF NEW.review_status=3 THEN RAISE EXCEPTION 'return_receipt_binding' USING ERRCODE='P0001'; END IF;
            END IF;
            IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
          END IF;
          IF TG_TABLE_NAME='mistake_item' AND TG_OP='UPDATE' THEN
            IF op.phase='Completed' AND OLD.review_status=1
            AND NEW.review_status=3 AND NEW.reviewer_id IS NOT DISTINCT FROM op.actor AND NEW.reviewed_at IS NOT DISTINCT FROM op.completed_at
            AND NEW.updated_at IS NOT DISTINCT FROM op.completed_at AND NEW.root_cause IS NOT DISTINCT FROM COALESCE(op.root_cause,OLD.root_cause)
            AND NEW.grade=COALESCE(op.grade,OLD.grade) AND NEW.subject=COALESCE(op.subject,OLD.subject)
            AND NEW.question_id IS NOT DISTINCT FROM OLD.question_id THEN RETURN NEW; END IF;
          END IF;
          IF TG_TABLE_NAME='process_log' AND TG_OP='INSERT' THEN
            IF op.phase='Completed' AND NEW.id=op.final_log_id
            AND NEW.action=4 AND NEW.student_id=op.student_id AND NEW.created_at=op.completed_at AND NEW.details='Managed source return completed; SourceRetained' THEN RETURN NEW; END IF;
          END IF;
          RAISE EXCEPTION 'return_in_progress' USING ERRCODE='P0001';
        END
        """;
    public const string IntegrityBody = """
        DECLARE op source_return; m mistake_item; paths text[]; obj source_return_object; candidate source_return_attempt; entries jsonb;
        BEGIN
          FOR op IN SELECT * FROM source_return LOOP
            SELECT * INTO STRICT m FROM mistake_item WHERE id=op.item_id;
            SELECT array_agg(path ORDER BY ordinal) INTO paths FROM
              (SELECT r->>'sourceImagePath' path,min(ord) ordinal FROM source_intake_group g,
                jsonb_array_elements(g.regions) WITH ORDINALITY p(r,ord) WHERE g.item_id=op.item_id GROUP BY r->>'sourceImagePath') ordered;
            IF (SELECT count(*) FROM source_return_object WHERE item_id=op.item_id)<>cardinality(paths) THEN RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
            IF op.payload IS NOT NULL THEN
              entries:=op.payload::jsonb->'imageEntries';
              IF op.payload_digest IS DISTINCT FROM return_payload_digest(op.student_id,op.payload) THEN RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
              IF op.payload::jsonb->>'operationId'<>op.operation_id OR op.payload::jsonb->>'comments'<>'Rejected'
                OR op.payload::jsonb->>'returnReason' IS DISTINCT FROM op.return_reason OR jsonb_array_length(entries)<>cardinality(paths) THEN
                RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
              FOR obj IN SELECT * FROM source_return_object WHERE item_id=op.item_id LOOP
                SELECT * INTO STRICT candidate FROM source_return_attempt WHERE attempt_id=obj.selected_attempt_id;
                IF candidate.phase<>'Verified' OR candidate.copy_settled_at IS NULL
                  OR (candidate.item_id,candidate.position) IS DISTINCT FROM (obj.item_id,obj.position)
                  OR entries->obj.position->>'path' IS DISTINCT FROM candidate.object_key OR entries->obj.position->>'type'<>'mistake' THEN
                  RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
              END LOOP;
            END IF;
            IF op.phase='Completed' THEN
              IF m.review_status<>3 OR m.reviewer_id IS DISTINCT FROM op.actor OR m.reviewed_at IS DISTINCT FROM op.completed_at
                OR m.updated_at IS DISTINCT FROM op.completed_at OR m.grade<>COALESCE(op.grade,(op.original_item::jsonb->>'grade')::int)
                OR m.subject<>COALESCE(op.subject,(op.original_item::jsonb->>'subject')::int)
                OR m.root_cause IS DISTINCT FROM COALESCE(op.root_cause,op.original_item::jsonb->>'rootCause')
                OR NOT EXISTS(SELECT 1 FROM process_log WHERE id=op.final_log_id AND mistake_item_id=op.item_id AND action=4
                  AND student_id=op.student_id AND created_at=op.completed_at AND details='Managed source return completed; SourceRetained')
                OR (SELECT count(*) FROM process_log WHERE mistake_item_id=op.item_id AND action=4)<>1 THEN
                RAISE EXCEPTION 'return_final_binding' USING ERRCODE='P0001'; END IF;
            ELSIF m.review_status<>1 OR EXISTS(SELECT 1 FROM process_log WHERE mistake_item_id=op.item_id AND action=4) THEN
              RAISE EXCEPTION 'return_final_binding' USING ERRCODE='P0001'; END IF;
          END LOOP;
          RETURN NULL;
        END
        """;
    public const string TruncateBody = """
        BEGIN
          IF EXISTS(SELECT 1 FROM source_return) THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          RETURN NULL;
        END
        """;
    public static string ManagedItemBody => SourceIntakeSchema.ItemBody.Replace("\n       OR NEW.review_status=3", "").Replace("\n               OR NEW.review_status=3", "");
    public static IReadOnlyDictionary<string,string> Bodies { get; } = new Dictionary<string,string>
    {
        ["return_payload_digest"]=DigestBody,["guard_source_return"]=OperationBody,["guard_return_object"]=ObjectBody,["guard_return_attempt"]=AttemptBody,
        ["guard_return_writer"]=WriterBody,["guard_return_integrity"]=IntegrityBody,["guard_return_truncate"]=TruncateBody
    };
    public static readonly string[] Triggers = [
        "source_return|trg_source_return|31|guard_source_return|False|False",
        "source_return_object|trg_return_object|31|guard_return_object|False|False",
        "source_return_attempt|trg_return_attempt|31|guard_return_attempt|False|False",
        "source_return|trg_return_integrity_operation|21|guard_return_integrity|True|True",
        "source_return_object|trg_return_integrity_object|21|guard_return_integrity|True|True",
        "mistake_item|trg_return_integrity_item|29|guard_return_integrity|True|True",
        "process_log|trg_return_integrity_log|29|guard_return_integrity|True|True",
        "mistake_item|trg_return_writer_item|31|guard_return_writer|False|False",
        "mistake_practice|trg_return_writer_practice|31|guard_return_writer|False|False",
        "process_log|trg_return_writer_log|31|guard_return_writer|False|False",
        "reanalyze_job|trg_return_writer_job|31|guard_return_writer|False|False",
        "source_return|trg_return_truncate_operation|34|guard_return_truncate|False|False",
        "source_return_object|trg_return_truncate_object|34|guard_return_truncate|False|False",
        "source_return_attempt|trg_return_truncate_attempt|34|guard_return_truncate|False|False",
        "mistake_practice|trg_return_truncate_practice|34|guard_return_truncate|False|False",
        "reanalyze_job|trg_return_truncate_job|34|guard_return_truncate|False|False"];
    public static string Guards => $"CREATE OR REPLACE FUNCTION guard_managed_item() RETURNS trigger LANGUAGE plpgsql AS $body${ManagedItemBody}$body$;\n"
        + string.Join("\n",Bodies.Select(p=>$"CREATE FUNCTION {p.Key}({(p.Key=="return_payload_digest" ? "input_student text,input_payload text" : "")}) RETURNS {(p.Key=="return_payload_digest" ? "text LANGUAGE plpgsql IMMUTABLE" : "trigger LANGUAGE plpgsql")} AS $body${p.Value}$body$;")) + "\n"
        + string.Join("\n",Triggers.Select(t =>
        {
            var v=t.Split('|');var deferred=v[4]=="True";
            var events=v[2] switch { "21"=>"INSERT OR UPDATE", "29"=>"INSERT OR UPDATE OR DELETE", "34"=>"TRUNCATE", _=>"INSERT OR UPDATE OR DELETE" };
            return $"CREATE {(deferred ? "CONSTRAINT " : "")}TRIGGER {v[1]} {(deferred ? "AFTER" : "BEFORE")} {events} ON {v[0]} {(deferred ? "DEFERRABLE INITIALLY DEFERRED " : "")}FOR EACH {(v[2]=="34" ? "STATEMENT" : "ROW")} EXECUTE FUNCTION {v[3]}();";
        }));
    public static string Down => """
        LOCK TABLE source_return,source_return_object,source_return_attempt IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN IF EXISTS(SELECT 1 FROM source_return) THEN RAISE EXCEPTION 'Source returns prohibit schema downgrade'; END IF; END $body$;
        """ + string.Join("\n",Triggers.Select(t=>{var v=t.Split('|');return $"DROP TRIGGER {v[1]} ON {v[0]};";}))
        + "ALTER TABLE source_return_object DROP CONSTRAINT \"FK_source_return_object_selected\"; DROP TABLE source_return_attempt,source_return_object,source_return;\n"
        + string.Join("\n",Bodies.Keys.Select(n=>$"DROP FUNCTION {n}({(n=="return_payload_digest" ? "text,text" : "")});"))
        + $"CREATE OR REPLACE FUNCTION guard_managed_item() RETURNS trigger LANGUAGE plpgsql AS $body${SourceIntakeSchema.ItemBody}$body$;";
}
