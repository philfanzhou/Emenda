namespace Ruoyu.Study.Mistake.Database;

public static class LegacyReturnSchema
{
    public const string Migration = "20261010000001_AddLegacyReturns";
    public const string Tables = """
        CREATE TABLE legacy_return (
          item_id uuid NOT NULL, operation_id text NOT NULL, student_id text NOT NULL, actor text NOT NULL, canonical text NOT NULL, original_item text NOT NULL,
          return_reason text NOT NULL, root_cause text, grade integer, subject integer, owner_id uuid NOT NULL, fence bigint NOT NULL,
          created_at timestamptz NOT NULL, lease_until timestamptz, phase text NOT NULL, payload text, payload_digest text, error_kind text, confirmed_record_id uuid,
          receipt_created_at timestamptz, completed_at timestamptz, final_log_id uuid,
          CONSTRAINT "PK_legacy_return" PRIMARY KEY(item_id),
          CONSTRAINT "FK_legacy_return_item" FOREIGN KEY(item_id) REFERENCES mistake_item(id) ON DELETE RESTRICT,
          CONSTRAINT "FK_legacy_return_log" FOREIGN KEY(final_log_id) REFERENCES process_log(id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
        );
        CREATE UNIQUE INDEX "IX_legacy_return_operation" ON legacy_return(operation_id);
        CREATE INDEX "IX_legacy_return_phase" ON legacy_return(phase);
        CREATE INDEX "IX_legacy_return_FinalLogId" ON legacy_return(final_log_id);
        CREATE TABLE legacy_return_object (
          item_id uuid NOT NULL, position integer NOT NULL, source_path text NOT NULL,
          expected_length bigint, expected_sha256 text, selected_attempt_id uuid,
          CONSTRAINT "PK_legacy_return_object" PRIMARY KEY(item_id,position),
          CONSTRAINT "FK_legacy_return_object_return" FOREIGN KEY(item_id) REFERENCES legacy_return(item_id) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_legacy_return_object_SelectedAttemptId" ON legacy_return_object(selected_attempt_id);
        CREATE TABLE legacy_return_attempt (
          attempt_id uuid NOT NULL, item_id uuid NOT NULL, position integer NOT NULL, object_key text NOT NULL,
          owner_id uuid NOT NULL, fence bigint NOT NULL, phase text NOT NULL, copy_settled_at timestamptz,
          verified_length bigint, verified_sha256 text,
          CONSTRAINT "PK_legacy_return_attempt" PRIMARY KEY(attempt_id),
          CONSTRAINT "FK_legacy_return_attempt_object" FOREIGN KEY(item_id,position) REFERENCES legacy_return_object(item_id,position) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_legacy_return_attempt_key" ON legacy_return_attempt(object_key);
        CREATE INDEX "IX_legacy_return_attempt_object" ON legacy_return_attempt(item_id,position);
        ALTER TABLE legacy_return_object ADD CONSTRAINT "FK_legacy_return_object_selected" FOREIGN KEY(selected_attempt_id) REFERENCES legacy_return_attempt(attempt_id) ON DELETE RESTRICT;

        """;
    public const string OperationBody = """
        DECLARE m mistake_item; original jsonb;
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT m FROM mistake_item WHERE id=NEW.item_id FOR UPDATE;
          original:=NEW.original_item::jsonb;
          IF NEW.item_id='00000000-0000-0000-0000-000000000000'::uuid
            OR NEW.owner_id='00000000-0000-0000-0000-000000000000'::uuid OR NEW.fence<1
            OR NEW.operation_id IS DISTINCT FROM 'mistake-return:'||NEW.item_id::text
            OR btrim(NEW.student_id)='' OR btrim(NEW.actor)='' OR NEW.student_id<>m.student_id
            OR (NEW.grade NOT BETWEEN 1 AND 12 AND NEW.grade IS DISTINCT FROM (original->>'grade')::int)
            OR (NEW.subject NOT BETWEEN 1 AND 9 AND NEW.subject IS DISTINCT FROM (original->>'subject')::int)
            OR NEW.grade IS NULL OR NEW.subject IS NULL OR NOT isfinite(NEW.created_at)
            OR NEW.phase NOT IN ('Capturing','Prepared','Sending','Completed','Conflict')
            OR NEW.canonical::jsonb IS DISTINCT FROM jsonb_build_array(NEW.actor,NEW.return_reason,NEW.root_cause,NEW.grade,NEW.subject) THEN
            RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF EXISTS(SELECT 1 FROM source_intake_group WHERE item_id=NEW.item_id)
            OR EXISTS(SELECT 1 FROM source_return WHERE item_id=NEW.item_id) THEN
            RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='INSERT' THEN
            IF original->>'id' IS DISTINCT FROM m.id::text OR original->>'studentId' IS DISTINCT FROM m.student_id
              OR original->>'sourceUploadId' IS DISTINCT FROM m.source_upload_id OR original->'sourceRegions' IS DISTINCT FROM m.source_regions
              OR (original->>'grade')::int IS DISTINCT FROM m.grade OR (original->>'subject')::int IS DISTINCT FROM m.subject
              OR (original->>'reviewStatus')::int IS DISTINCT FROM m.review_status OR original->>'reviewerId' IS DISTINCT FROM m.reviewer_id
              OR (original->>'reviewedAt')::timestamptz IS DISTINCT FROM m.reviewed_at OR original->>'rootCause' IS DISTINCT FROM m.root_cause
              OR original->>'questionId' IS DISTINCT FROM m.question_id OR (original->>'createdAt')::timestamptz IS DISTINCT FROM m.created_at
              OR (original->>'updatedAt')::timestamptz IS DISTINCT FROM m.updated_at
              OR NOT original ?& ARRAY['id','studentId','sourceUploadId','sourceRegions','grade','subject','reviewStatus','reviewerId','reviewedAt','rootCause','questionId','createdAt','updatedAt']
              OR m.review_status<>1 OR NEW.phase<>'Capturing' OR NEW.fence<>1
              OR NEW.payload IS NOT NULL OR NEW.payload_digest IS NOT NULL OR NEW.error_kind IS NOT NULL
              OR NEW.lease_until IS NULL OR NEW.lease_until<=clock_timestamp()
              OR NEW.lease_until>clock_timestamp()+interval '60 seconds'
              OR jsonb_typeof(m.source_regions)<>'array' OR jsonb_array_length(m.source_regions)=0 THEN
              RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
            IF EXISTS(SELECT 1 FROM reanalyze_job WHERE item_id=NEW.item_id AND status IN (1,2)) THEN
              RAISE EXCEPTION 'reanalysis_active' USING ERRCODE='P0001'; END IF;
          ELSE
            IF (NEW.item_id,NEW.operation_id,NEW.student_id,NEW.actor,NEW.canonical,NEW.original_item,NEW.return_reason,NEW.root_cause,NEW.grade,NEW.subject,NEW.created_at)
              IS DISTINCT FROM (OLD.item_id,OLD.operation_id,OLD.student_id,OLD.actor,OLD.canonical,OLD.original_item,OLD.return_reason,OLD.root_cause,OLD.grade,OLD.subject,OLD.created_at)
              OR OLD.phase IN ('Completed','Conflict')
              OR (OLD.payload IS NOT NULL AND (NEW.payload,NEW.payload_digest) IS DISTINCT FROM (OLD.payload,OLD.payload_digest))
              OR (OLD.phase='Prepared' AND NEW.phase NOT IN ('Prepared','Sending','Conflict')) OR (OLD.phase='Sending' AND NEW.phase NOT IN ('Sending','Completed','Conflict'))
              OR (OLD.phase='Capturing' AND NEW.phase NOT IN ('Capturing','Prepared','Conflict')) THEN
              RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
            IF (NEW.owner_id,NEW.fence) IS DISTINCT FROM (OLD.owner_id,OLD.fence) THEN
              IF OLD.lease_until>clock_timestamp() OR NEW.fence<>OLD.fence+1 OR NEW.owner_id=OLD.owner_id
                OR (to_jsonb(NEW)-ARRAY['owner_id','fence','lease_until']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['owner_id','fence','lease_until']) THEN
                RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
            ELSIF OLD.lease_until IS NULL OR OLD.lease_until<=clock_timestamp() THEN
              RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
            IF NEW.lease_until IS NOT NULL AND (NEW.lease_until<=clock_timestamp() OR NEW.lease_until>clock_timestamp()+interval '60 seconds') THEN
              RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          END IF;
          IF (NEW.payload IS NULL)<>(NEW.payload_digest IS NULL) OR (NEW.payload_digest IS NOT NULL AND NEW.payload_digest !~ '^[0-9a-f]{64}$')
            OR (NEW.phase IN ('Capturing','Prepared') AND NEW.payload IS NOT NULL)
            OR (NEW.phase IN ('Sending','Completed') AND NEW.payload IS NULL) THEN
            RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
          IF NEW.phase='Completed' THEN
            IF TG_OP='INSERT' THEN RAISE EXCEPTION 'return_receipt_binding' USING ERRCODE='P0001'; END IF;
            IF OLD.phase<>'Sending' OR NEW.confirmed_record_id IS NULL OR NEW.confirmed_record_id='00000000-0000-0000-0000-000000000000'::uuid
              OR NEW.receipt_created_at IS NULL OR NOT isfinite(NEW.receipt_created_at)
              OR NEW.completed_at IS NULL OR NOT isfinite(NEW.completed_at) OR NEW.final_log_id IS NULL THEN
              RAISE EXCEPTION 'return_receipt_binding' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.confirmed_record_id IS NOT NULL OR NEW.receipt_created_at IS NOT NULL OR NEW.completed_at IS NOT NULL OR NEW.final_log_id IS NOT NULL THEN
            RAISE EXCEPTION 'return_receipt_binding' USING ERRCODE='P0001'; END IF;
          RETURN NEW;
        END
        """;
    public const string ObjectBody = """
        DECLARE op legacy_return; candidate legacy_return_attempt; paths text[];
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          PERFORM 1 FROM mistake_item WHERE id=NEW.item_id FOR UPDATE;
          SELECT * INTO STRICT op FROM legacy_return WHERE item_id=NEW.item_id FOR UPDATE;
          IF op.lease_until IS NULL OR op.lease_until<=clock_timestamp() THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          SELECT array_agg(path ORDER BY ordinal) INTO paths FROM
            (SELECT r->>'sourceImagePath' COLLATE "C" path,min(ord) ordinal FROM jsonb_array_elements(op.original_item::jsonb->'sourceRegions') WITH ORDINALITY p(r,ord)
              GROUP BY r->>'sourceImagePath' COLLATE "C") ordered;
          IF NEW.position<0 OR NEW.source_path IS DISTINCT FROM paths[NEW.position+1]
            OR btrim(NEW.source_path)='' OR NEW.source_path !~ '^(uploads|mistakes)/[^/]'
            OR reserved_target_path(NEW.source_path) OR NEW.source_path LIKE 'uploads/mistake-returns/%'
            OR NEW.source_path ~ '(^|/)[.][.]?(/|$)' OR NEW.source_path LIKE '%\%' THEN
            RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF (NEW.expected_length IS NULL)<>(NEW.expected_sha256 IS NULL)
            OR NEW.expected_length<0 OR NEW.expected_sha256 !~ '^[0-9a-f]{64}$' THEN
            RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='INSERT' THEN
            IF op.phase<>'Capturing' OR NEW.expected_length IS NOT NULL OR NEW.selected_attempt_id IS NOT NULL THEN
              RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSE
            IF (NEW.item_id,NEW.position,NEW.source_path) IS DISTINCT FROM (OLD.item_id,OLD.position,OLD.source_path)
              OR OLD.selected_attempt_id IS NOT NULL
              OR (OLD.expected_length IS NOT NULL AND (NEW.expected_length,NEW.expected_sha256) IS DISTINCT FROM (OLD.expected_length,OLD.expected_sha256))
              OR (OLD.expected_length IS NULL AND (op.phase<>'Capturing' OR NEW.expected_length IS NULL)) THEN
              RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
            IF NEW.selected_attempt_id IS NOT NULL THEN
              SELECT * INTO STRICT candidate FROM legacy_return_attempt WHERE attempt_id=NEW.selected_attempt_id;
              IF op.phase<>'Prepared' OR (candidate.item_id,candidate.position) IS DISTINCT FROM (NEW.item_id,NEW.position)
                OR candidate.phase<>'Verified' OR candidate.copy_settled_at IS NULL
                OR candidate.verified_length IS DISTINCT FROM NEW.expected_length OR candidate.verified_sha256 IS DISTINCT FROM NEW.expected_sha256 THEN
                RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
            END IF;
          END IF;
          RETURN NEW;
        END
        """;
    public const string AttemptBody = """
        DECLARE op legacy_return; obj legacy_return_object;
        BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          PERFORM 1 FROM mistake_item WHERE id=NEW.item_id FOR UPDATE;
          SELECT * INTO STRICT op FROM legacy_return WHERE item_id=NEW.item_id FOR UPDATE;
          IF op.lease_until IS NULL OR op.lease_until<=clock_timestamp() THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          SELECT * INTO STRICT obj FROM legacy_return_object WHERE item_id=NEW.item_id AND position=NEW.position;
          IF op.phase<>'Prepared' OR obj.selected_attempt_id IS NOT NULL
            OR NEW.attempt_id='00000000-0000-0000-0000-000000000000'::uuid
            OR NEW.object_key<>'uploads/mistake-returns/'||NEW.item_id::text||'/'||NEW.attempt_id::text||'/original.jpg'
            OR NEW.phase NOT IN ('Reserved','Unknown','CopySettled','Verified') THEN RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='INSERT' THEN
            IF NEW.phase<>'Reserved' OR NEW.copy_settled_at IS NOT NULL OR NEW.verified_length IS NOT NULL OR NEW.verified_sha256 IS NOT NULL
              OR NEW.owner_id<>op.owner_id OR NEW.fence<>op.fence THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSE
            IF (NEW.attempt_id,NEW.item_id,NEW.position,NEW.object_key,NEW.owner_id,NEW.fence)
              IS DISTINCT FROM (OLD.attempt_id,OLD.item_id,OLD.position,OLD.object_key,OLD.owner_id,OLD.fence)
              OR OLD.phase IN ('Verified','Unknown')
              OR (OLD.copy_settled_at IS NOT NULL AND NEW.copy_settled_at IS DISTINCT FROM OLD.copy_settled_at)
              OR (OLD.phase='Reserved' AND NEW.phase NOT IN ('Unknown','CopySettled'))
              OR (OLD.phase='CopySettled' AND NEW.phase<>'Verified') THEN
              RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
            IF OLD.phase='Reserved' AND NEW.phase='CopySettled' AND (NEW.owner_id<>op.owner_id OR NEW.fence<>op.fence) THEN
              RAISE EXCEPTION 'lease_lost' USING ERRCODE='P0001'; END IF;
          END IF;
          IF NEW.phase IN ('CopySettled','Verified') THEN
            IF NEW.copy_settled_at IS NULL OR NOT isfinite(NEW.copy_settled_at) THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.copy_settled_at IS NOT NULL THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          IF NEW.phase='Verified' THEN
            IF NEW.verified_length IS DISTINCT FROM obj.expected_length OR NEW.verified_sha256 IS DISTINCT FROM obj.expected_sha256 THEN
              RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.verified_length IS NOT NULL OR NEW.verified_sha256 IS NOT NULL THEN RAISE EXCEPTION 'return_unverified' USING ERRCODE='P0001'; END IF;
          RETURN NEW;
        END
        """;
    public const string WriterBody = """
        DECLARE itemid uuid; olditem uuid; op legacy_return;
        BEGIN
          IF TG_TABLE_NAME='mistake_item' THEN
            itemid:=CASE WHEN TG_OP='DELETE' THEN OLD.id ELSE NEW.id END;
            IF TG_OP='UPDATE' THEN olditem:=OLD.id; END IF;
          ELSIF TG_TABLE_NAME IN ('reanalyze_job','source_intake_group') THEN
            itemid:=CASE WHEN TG_OP='DELETE' THEN OLD.item_id ELSE NEW.item_id END;
            IF TG_OP='UPDATE' THEN olditem:=OLD.item_id; END IF;
          ELSE
            itemid:=CASE WHEN TG_OP='DELETE' THEN OLD.mistake_item_id ELSE NEW.mistake_item_id END;
            IF TG_OP='UPDATE' THEN olditem:=OLD.mistake_item_id; END IF;
          END IF;
          PERFORM id FROM mistake_item WHERE id IN (itemid,olditem) ORDER BY id FOR UPDATE;
          SELECT * INTO op FROM legacy_return WHERE item_id IN (itemid,olditem) ORDER BY item_id LIMIT 1;
          IF NOT FOUND THEN IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF; END IF;
          IF TG_TABLE_NAME='mistake_item' AND TG_OP='UPDATE' THEN
            IF op.phase='Completed' AND OLD.review_status=1
            AND NEW.review_status=3 AND NEW.reviewer_id IS NOT DISTINCT FROM op.actor
            AND NEW.reviewed_at IS NOT DISTINCT FROM op.completed_at AND NEW.updated_at IS NOT DISTINCT FROM op.completed_at
            AND NEW.root_cause IS NOT DISTINCT FROM op.root_cause AND NEW.grade=op.grade AND NEW.subject=op.subject
            AND (to_jsonb(NEW)-ARRAY['review_status','reviewer_id','reviewed_at','updated_at','root_cause','grade','subject'])
              IS NOT DISTINCT FROM (to_jsonb(OLD)-ARRAY['review_status','reviewer_id','reviewed_at','updated_at','root_cause','grade','subject']) THEN RETURN NEW; END IF;
          END IF;
          IF TG_TABLE_NAME='process_log' AND TG_OP='INSERT' THEN
            IF op.phase='Completed' AND NEW.id=op.final_log_id
            AND NEW.action=4 AND NEW.student_id=op.student_id AND NEW.created_at=op.completed_at
            AND NEW.details='Legacy return completed; OriginalRetained' THEN RETURN NEW; END IF;
          END IF;
          RAISE EXCEPTION 'return_in_progress' USING ERRCODE='P0001';
        END
        """;
    public const string IntegrityBody = """
        DECLARE op legacy_return; m mistake_item; paths text[]; obj legacy_return_object; candidate legacy_return_attempt; entries jsonb;
        BEGIN
          FOR op IN SELECT * FROM legacy_return LOOP
            SELECT * INTO STRICT m FROM mistake_item WHERE id=op.item_id;
            SELECT array_agg(path ORDER BY ordinal) INTO paths FROM
              (SELECT r->>'sourceImagePath' COLLATE "C" path,min(ord) ordinal FROM jsonb_array_elements(op.original_item::jsonb->'sourceRegions') WITH ORDINALITY p(r,ord)
                GROUP BY r->>'sourceImagePath' COLLATE "C") ordered;
            IF paths IS NULL OR cardinality(paths)=0 OR (SELECT count(*) FROM legacy_return_object WHERE item_id=op.item_id)<>cardinality(paths) THEN
              RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
            FOR obj IN SELECT * FROM legacy_return_object WHERE item_id=op.item_id LOOP
              IF obj.source_path IS DISTINCT FROM paths[obj.position+1] OR obj.position<0 OR obj.position>=cardinality(paths)
                OR (op.phase IN ('Prepared','Sending','Completed') AND obj.expected_length IS NULL) THEN
                RAISE EXCEPTION 'return_binding' USING ERRCODE='P0001'; END IF;
            END LOOP;
            IF op.payload IS NOT NULL THEN
              entries:=op.payload::jsonb->'imageEntries';
              IF op.payload_digest IS DISTINCT FROM return_payload_digest(op.student_id,op.payload)
                OR op.payload::jsonb->>'operationId' IS DISTINCT FROM op.operation_id OR op.payload::jsonb->>'comments' IS DISTINCT FROM 'Rejected'
                OR op.payload::jsonb->>'returnReason' IS DISTINCT FROM op.return_reason
                OR jsonb_typeof(entries) IS DISTINCT FROM 'array' OR jsonb_array_length(entries)<>cardinality(paths) THEN
                RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
              FOR obj IN SELECT * FROM legacy_return_object WHERE item_id=op.item_id LOOP
                SELECT * INTO STRICT candidate FROM legacy_return_attempt WHERE attempt_id=obj.selected_attempt_id;
                IF candidate.phase<>'Verified' OR candidate.copy_settled_at IS NULL
                  OR (candidate.item_id,candidate.position) IS DISTINCT FROM (obj.item_id,obj.position)
                  OR candidate.verified_length IS DISTINCT FROM obj.expected_length OR candidate.verified_sha256 IS DISTINCT FROM obj.expected_sha256
                  OR entries->obj.position->>'path' IS DISTINCT FROM candidate.object_key OR entries->obj.position->>'type' IS DISTINCT FROM 'mistake' THEN
                  RAISE EXCEPTION 'return_payload_binding' USING ERRCODE='P0001'; END IF;
              END LOOP;
            END IF;
            IF op.phase='Completed' THEN
              IF m.review_status<>3 OR m.reviewer_id IS DISTINCT FROM op.actor OR m.reviewed_at IS DISTINCT FROM op.completed_at
                OR m.updated_at IS DISTINCT FROM op.completed_at OR m.grade<>op.grade OR m.subject<>op.subject
                OR m.root_cause IS DISTINCT FROM op.root_cause
                OR NOT EXISTS(SELECT 1 FROM process_log WHERE id=op.final_log_id AND mistake_item_id=op.item_id AND action=4
                  AND student_id=op.student_id AND created_at=op.completed_at AND details='Legacy return completed; OriginalRetained')
                OR (SELECT count(*) FROM process_log WHERE mistake_item_id=op.item_id AND details='Legacy return completed; OriginalRetained')<>1 THEN
                RAISE EXCEPTION 'return_final_binding' USING ERRCODE='P0001'; END IF;
            ELSIF m.review_status<>1 OR EXISTS(SELECT 1 FROM process_log WHERE mistake_item_id=op.item_id AND details='Legacy return completed; OriginalRetained') THEN
              RAISE EXCEPTION 'return_final_binding' USING ERRCODE='P0001'; END IF;
          END LOOP;
          RETURN NULL;
        END
        """;
    public const string TruncateBody = """
        BEGIN
          IF EXISTS(SELECT 1 FROM legacy_return) THEN RAISE EXCEPTION 'return_managed' USING ERRCODE='P0001'; END IF;
          RETURN NULL;
        END
        """;
    public static IReadOnlyDictionary<string,string> Bodies { get; } = new Dictionary<string,string>
    {
        ["guard_legacy_return"]=OperationBody,["guard_legacy_return_object"]=ObjectBody,["guard_legacy_return_attempt"]=AttemptBody,
        ["guard_legacy_return_writer"]=WriterBody,["guard_legacy_return_integrity"]=IntegrityBody,["guard_legacy_return_truncate"]=TruncateBody
    };
    public static readonly string[] Triggers = [
        "legacy_return|trg_legacy_return|31|guard_legacy_return|False|False",
        "legacy_return_object|trg_legacy_return_object|31|guard_legacy_return_object|False|False",
        "legacy_return_attempt|trg_legacy_return_attempt|31|guard_legacy_return_attempt|False|False",
        "legacy_return|trg_legacy_return_integrity_operation|21|guard_legacy_return_integrity|True|True",
        "legacy_return_object|trg_legacy_return_integrity_object|21|guard_legacy_return_integrity|True|True",
        "legacy_return_attempt|trg_legacy_return_integrity_attempt|21|guard_legacy_return_integrity|True|True",
        "mistake_item|trg_legacy_return_integrity_item|29|guard_legacy_return_integrity|True|True",
        "process_log|trg_legacy_return_integrity_log|29|guard_legacy_return_integrity|True|True",
        "mistake_item|trg_legacy_return_writer_item|31|guard_legacy_return_writer|False|False",
        "mistake_practice|trg_legacy_return_writer_practice|31|guard_legacy_return_writer|False|False",
        "process_log|trg_legacy_return_writer_log|31|guard_legacy_return_writer|False|False",
        "reanalyze_job|trg_legacy_return_writer_job|31|guard_legacy_return_writer|False|False",
        "source_intake_group|trg_legacy_return_writer_group|31|guard_legacy_return_writer|False|False",
        "legacy_return|trg_legacy_return_truncate_operation|34|guard_legacy_return_truncate|False|False",
        "legacy_return_object|trg_legacy_return_truncate_object|34|guard_legacy_return_truncate|False|False",
        "legacy_return_attempt|trg_legacy_return_truncate_attempt|34|guard_legacy_return_truncate|False|False",
        "mistake_item|trg_legacy_return_truncate_item|34|guard_legacy_return_truncate|False|False",
        "mistake_practice|trg_legacy_return_truncate_practice|34|guard_legacy_return_truncate|False|False",
        "process_log|trg_legacy_return_truncate_log|34|guard_legacy_return_truncate|False|False",
        "reanalyze_job|trg_legacy_return_truncate_job|34|guard_legacy_return_truncate|False|False"];
    public static string Guards => string.Join("\n",Bodies.Select(p=>$"CREATE FUNCTION {p.Key}() RETURNS trigger LANGUAGE plpgsql AS $body${p.Value}$body$;")) + "\n"
        + string.Join("\n",Triggers.Select(t =>
        {
            var v=t.Split('|');var deferred=v[4]=="True";
            var events=v[2] switch { "21"=>"INSERT OR UPDATE", "34"=>"TRUNCATE", _=>"INSERT OR UPDATE OR DELETE" };
            return $"CREATE {(deferred ? "CONSTRAINT " : "")}TRIGGER {v[1]} {(deferred ? "AFTER" : "BEFORE")} {events} ON {v[0]} {(deferred ? "DEFERRABLE INITIALLY DEFERRED " : "")}FOR EACH {(v[2]=="34" ? "STATEMENT" : "ROW")} EXECUTE FUNCTION {v[3]}();";
        }));
    public static string Down => """
        LOCK TABLE legacy_return,legacy_return_object,legacy_return_attempt IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN IF EXISTS(SELECT 1 FROM legacy_return) THEN RAISE EXCEPTION 'Legacy returns prohibit schema downgrade'; END IF; END $body$;
        """ + string.Join("\n",Triggers.Select(t=>{var v=t.Split('|');return $"DROP TRIGGER {v[1]} ON {v[0]};";}))
        + "ALTER TABLE legacy_return_object DROP CONSTRAINT \"FK_legacy_return_object_selected\"; DROP TABLE legacy_return_attempt,legacy_return_object,legacy_return;\n"
        + string.Join("\n",Bodies.Keys.Select(n=>$"DROP FUNCTION {n}();"));
}
