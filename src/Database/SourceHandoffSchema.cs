namespace Emenda.Database;

public static class SourceHandoffSchema
{
    public const string Migration="20261003020001_AddSourceHandoffs";
    public const string Tables="""
        CREATE TABLE source_handoff (
          source_upload_id uuid NOT NULL,content_revision uuid NOT NULL,snapshot_id uuid NOT NULL,
          student_id text NOT NULL,operation_id text NOT NULL,actor text NOT NULL,sealed_at timestamptz NOT NULL,
          groups_canonical text NOT NULL,groups_digest text NOT NULL,targets text NOT NULL,payload_canonical text NOT NULL,
          payload_digest text NOT NULL,owner_id uuid NOT NULL,fence bigint NOT NULL,phase text NOT NULL,
          receipt text,completed_at timestamptz,error_kind text,
          CONSTRAINT "PK_source_handoff" PRIMARY KEY(source_upload_id,content_revision),
          CONSTRAINT "FK_source_handoff_intake" FOREIGN KEY(source_upload_id,content_revision)
            REFERENCES source_intake(source_upload_id,content_revision) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_source_handoff_snapshot" ON source_handoff(snapshot_id);
        CREATE UNIQUE INDEX "IX_source_handoff_operation" ON source_handoff(operation_id);
        """;
    public static string SourceLockBody=>SourceIntakeSchema.SourceLockBody.Replace(
        "IF EXISTS(SELECT 1 FROM mistake_item WHERE intake_source_key(source_upload_id)=NEW.source_upload_id::text)","""
        IF EXISTS(SELECT 1 FROM mistake_item i WHERE intake_source_key(i.source_upload_id)=NEW.source_upload_id::text
          AND NOT EXISTS(SELECT 1 FROM source_intake_group g JOIN source_handoff h USING(source_upload_id,content_revision)
            WHERE g.item_id=i.id AND h.phase='Completed' AND h.student_id=NEW.student_id))
        """);
    public static string GroupBody=>SourceIntakeSchema.GroupBody.Replace("IF src.lease_until IS NULL", """
        IF EXISTS(SELECT 1 FROM source_handoff WHERE source_upload_id=NEW.source_upload_id AND content_revision=NEW.content_revision) THEN
          RAISE EXCEPTION 'source_sealed' USING ERRCODE='P0001'; END IF;
        IF src.lease_until IS NULL
        """);
    public static string RequestBody=>SourceIntakeSchema.RequestBody.Replace("IF TG_OP='INSERT' THEN RETURN NEW; END IF;", """
        IF EXISTS(SELECT 1 FROM source_handoff WHERE source_upload_id=NEW.source_upload_id AND content_revision=NEW.content_revision) THEN
          IF TG_OP='INSERT' THEN
            IF NEW.item_ids IS NULL THEN RAISE EXCEPTION 'source_sealed' USING ERRCODE='P0001'; END IF;
          ELSIF OLD.item_ids IS NULL THEN RAISE EXCEPTION 'source_sealed' USING ERRCODE='P0001'; END IF;
        END IF;
        IF TG_OP='INSERT' THEN RETURN NEW; END IF;
        """);
    public static string ItemBody=>SourceReturnSchema.ManagedItemBody.Replace(
        "PERFORM pg_advisory_xact_lock(hashtextextended(source_key,791249));", """
        PERFORM pg_advisory_xact_lock(hashtextextended(source_key,791249));
          IF TG_OP='UPDATE' AND EXISTS(SELECT 1 FROM source_intake_group g JOIN source_handoff h USING(source_upload_id,content_revision) WHERE g.item_id=OLD.id)
            AND NEW.review_status IS DISTINCT FROM OLD.review_status THEN
            RAISE EXCEPTION 'source_sealed' USING ERRCODE='P0001'; END IF;
        """);
    public const string EncodeBody="""
        DECLARE b bytea:=''::bytea; v text; n integer; prefix bytea;
        BEGIN
          FOREACH v IN ARRAY values_in LOOP
            IF v IS NULL THEN RAISE EXCEPTION 'invalid_handoff_canonical'; END IF;
            n:=octet_length(convert_to(v,'UTF8')); prefix:=decode('00000000','hex');
            prefix:=set_byte(set_byte(set_byte(set_byte(prefix,0,n&255),1,(n>>8)&255),2,(n>>16)&255),3,(n>>24)&255);
            b:=b||prefix||convert_to(v,'UTF8');
          END LOOP;
          RETURN replace(encode(b,'base64'),chr(10),'');
        END
        """;
    public const string SortBody="""
        DECLARE result text:=''; n integer; i integer;
        BEGIN
          FOR i IN 1..length(v) LOOP
            n:=ascii(substr(v,i,1));
            IF n>65535 THEN result:=result||lpad(to_hex(55296+((n-65536)/1024)),4,'0')||lpad(to_hex(56320+((n-65536)%1024)),4,'0');
            ELSE result:=result||lpad(to_hex(n),4,'0'); END IF;
          END LOOP;
          RETURN result;
        END
        """;
    public const string PayloadBody="""
        DECLARE encoded text[];
        BEGIN
          SELECT array_agg(handoff_encode(t->>'sourcePath',t->>'targetKey',t->>'length',t->>'sha256') ORDER BY handoff_sort_key(t->>'sourcePath') COLLATE "C")
            INTO encoded FROM jsonb_array_elements(input_targets::jsonb) t;
          RETURN handoff_encode('source-handoff-v1','mistake-handoff:'||input_snapshot::text,input_source::text,input_revision::text,
            input_snapshot::text,input_student,input_groups,handoff_encode(VARIADIC COALESCE(encoded,ARRAY[]::text[])));
        END
        """;
    public const string GuardBody="""
        DECLARE src source_intake; manifest jsonb; canonical text; group_parts text[]; expected text; receipt_json jsonb;
        BEGIN
          IF TG_OP='DELETE' OR TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'source_sealed' USING ERRCODE='P0001'; END IF;
          PERFORM pg_advisory_xact_lock(hashtextextended(NEW.source_upload_id::text,791249));
          SELECT * INTO STRICT src FROM source_intake WHERE source_upload_id=NEW.source_upload_id AND content_revision=NEW.content_revision FOR UPDATE;
          IF (NEW.snapshot_id,NEW.student_id) IS DISTINCT FROM (src.snapshot_id,src.student_id::text)
            OR NEW.operation_id<>'mistake-handoff:'||src.snapshot_id::text OR NEW.actor='' OR NEW.sealed_at<'1970-01-01'
            OR NEW.owner_id<>src.owner_id OR NEW.fence<>src.fence OR src.lease_until IS NULL OR src.lease_until<=clock_timestamp()
            OR NEW.phase NOT IN ('Prepared','Sending','Conflict','Completed') THEN
            RAISE EXCEPTION 'handoff_binding' USING ERRCODE='P0001'; END IF;
          IF TG_OP='UPDATE' THEN
            IF (NEW.source_upload_id,NEW.content_revision,NEW.snapshot_id,NEW.student_id,NEW.operation_id,NEW.actor,NEW.sealed_at,
                NEW.groups_canonical,NEW.groups_digest,NEW.targets,NEW.payload_canonical,NEW.payload_digest)
              IS DISTINCT FROM (OLD.source_upload_id,OLD.content_revision,OLD.snapshot_id,OLD.student_id,OLD.operation_id,OLD.actor,OLD.sealed_at,
                OLD.groups_canonical,OLD.groups_digest,OLD.targets,OLD.payload_canonical,OLD.payload_digest)
              OR OLD.phase IN ('Completed','Conflict') OR (OLD.phase='Sending' AND NEW.phase='Prepared')
              OR NEW.fence<OLD.fence OR (OLD.receipt IS NOT NULL AND NEW.receipt IS DISTINCT FROM OLD.receipt) THEN
              RAISE EXCEPTION 'source_sealed' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.phase<>'Prepared' OR NEW.receipt IS NOT NULL OR NEW.completed_at IS NOT NULL THEN
            RAISE EXCEPTION 'handoff_binding' USING ERRCODE='P0001';
          END IF;
          IF NOT src.materialized OR NOT EXISTS(SELECT 1 FROM source_intake_group WHERE source_upload_id=src.source_upload_id AND content_revision=src.content_revision)
            OR EXISTS(SELECT 1 FROM source_intake_group g JOIN mistake_item i ON i.id=g.item_id
              WHERE g.source_upload_id=src.source_upload_id AND g.content_revision=src.content_revision
              AND NOT (i.review_status=2 OR (i.review_status=3 AND EXISTS(SELECT 1 FROM source_return r WHERE r.item_id=i.id AND r.phase='Completed' AND r.confirmed_record_id IS NOT NULL AND r.receipt_created_at IS NOT NULL)))) THEN
            RAISE EXCEPTION 'handoff_not_ready' USING ERRCODE='P0001'; END IF;
          manifest:=src.manifest::jsonb;
          IF EXISTS(SELECT 1 FROM jsonb_array_elements(manifest->'images') image WHERE image->>'type'='mistake'
            AND NOT EXISTS(SELECT 1 FROM source_intake_group g CROSS JOIN LATERAL jsonb_array_elements(g.regions) region
              WHERE g.source_upload_id=src.source_upload_id AND g.content_revision=src.content_revision AND region->>'sourceImagePath'=image->>'path')) THEN
            RAISE EXCEPTION 'handoff_not_ready' USING ERRCODE='P0001'; END IF;
          SELECT array_agg(handoff_encode(groups.item_id::text,groups.canonical) ORDER BY groups.item_id::text COLLATE "C") INTO group_parts
            FROM source_intake_group groups WHERE groups.source_upload_id=src.source_upload_id AND groups.content_revision=src.content_revision;
          canonical:=handoff_encode(VARIADIC ARRAY['mistake-handoff-groups-v1']||group_parts);
          IF NEW.groups_canonical<>canonical OR NEW.groups_digest<>encode(sha256(convert_to(canonical,'UTF8')),'hex') THEN
            RAISE EXCEPTION 'handoff_binding' USING ERRCODE='P0001'; END IF;
          SELECT jsonb_agg(jsonb_build_object('sourcePath',o.source_path,'targetKey',a.target_key,'length',o.expected_length,'sha256',o.expected_sha256)
            ORDER BY handoff_sort_key(o.source_path) COLLATE "C")::text INTO expected FROM source_target_object o JOIN source_target_attempt a ON a.attempt_id=o.selected_attempt_id
            WHERE o.source_upload_id=src.source_upload_id AND o.content_revision=src.content_revision AND a.phase='Verified';
          IF expected IS NULL OR NEW.targets::jsonb<>expected::jsonb
            OR jsonb_array_length(expected::jsonb)<>(SELECT count(DISTINCT image->>'path') FROM jsonb_array_elements(manifest->'images') image WHERE image->>'type'='mistake') THEN
            RAISE EXCEPTION 'handoff_targets_pending' USING ERRCODE='P0001'; END IF;
          canonical:=handoff_payload(src.source_upload_id,src.content_revision,src.snapshot_id,src.student_id,NEW.groups_digest,NEW.targets);
          IF NEW.payload_canonical<>canonical OR NEW.payload_digest<>encode(sha256(convert_to(canonical,'UTF8')),'hex') THEN
            RAISE EXCEPTION 'handoff_binding' USING ERRCODE='P0001'; END IF;
          IF NEW.phase='Completed' THEN
            receipt_json:=NEW.receipt::jsonb;
            IF NEW.completed_at IS NULL OR receipt_json IS NULL OR NOT (receipt_json ?& ARRAY['receiptId','operationId','sourceUploadId','contentRevision','snapshotId','studentId','groupsDigest','payloadDigest','digestVersion','committedAt','recordDeleted','remainingImageCount','resultingContentRevision']) OR EXISTS(SELECT 1 FROM jsonb_each(receipt_json) p WHERE p.key<>'resultingContentRevision' AND p.value='null'::jsonb) OR receipt_json->>'operationId'<>NEW.operation_id
              OR (receipt_json->>'receiptId')::uuid='00000000-0000-0000-0000-000000000000'
              OR receipt_json->>'sourceUploadId'<>NEW.source_upload_id::text OR receipt_json->>'contentRevision'<>NEW.content_revision::text
              OR receipt_json->>'snapshotId'<>NEW.snapshot_id::text OR receipt_json->>'studentId'<>NEW.student_id
              OR receipt_json->>'groupsDigest'<>NEW.groups_digest OR receipt_json->>'payloadDigest'<>NEW.payload_digest
              OR receipt_json->>'committedAt' !~ '(Z|\+00:00)$' OR receipt_json->>'digestVersion'<>'source-handoff-v1' OR (receipt_json->>'committedAt')::timestamptz<'1970-01-01'
              OR (receipt_json->>'remainingImageCount')::integer<>(SELECT count(*) FROM jsonb_array_elements(manifest->'images') image WHERE image->>'type'<>'mistake')
              OR (receipt_json->>'recordDeleted')::boolean<>((receipt_json->>'remainingImageCount')::integer=0)
              OR ((receipt_json->>'remainingImageCount')::integer>0 AND ((receipt_json->>'resultingContentRevision')::uuid IS NULL OR (receipt_json->>'resultingContentRevision')::uuid IN (src.content_revision,'00000000-0000-0000-0000-000000000000')))
              OR ((receipt_json->>'remainingImageCount')::integer=0 AND receipt_json->>'resultingContentRevision' IS NOT NULL) THEN
              RAISE EXCEPTION 'handoff_receipt_binding' USING ERRCODE='P0001'; END IF;
          ELSIF NEW.receipt IS NOT NULL OR NEW.completed_at IS NOT NULL THEN
            RAISE EXCEPTION 'handoff_receipt_binding' USING ERRCODE='P0001'; END IF;
          RETURN NEW;
        END
        """;
    public static IReadOnlyDictionary<string,string> Bodies=>new Dictionary<string,string>
    {
        ["guard_source_intake"]=SourceLockBody,["guard_intake_group"]=GroupBody,["guard_managed_item"]=ItemBody,["guard_intake_request"]=RequestBody,
        ["handoff_encode"]=EncodeBody,["handoff_sort_key"]=SortBody,["handoff_payload"]=PayloadBody,["guard_source_handoff"]=GuardBody
    };
    public static (string Args,string Return,char Volatile,int Count) Signature(string name)=>name switch
    {
        "handoff_encode"=>("VARIADIC values_in text[]","text",'i',1),
        "handoff_sort_key"=>("v text","text",'i',1),
        "handoff_payload"=>("input_source uuid, input_revision uuid, input_snapshot uuid, input_student text, input_groups text, input_targets text","text",'i',6),
        _=>("","trigger",'v',0)
    };
    public static string Guards=>string.Join('\n',Bodies.Select(p=>
    {
        var signature=Signature(p.Key);return $"CREATE OR REPLACE FUNCTION {p.Key}({signature.Args}) RETURNS {signature.Return} LANGUAGE plpgsql {(signature.Volatile=='i' ? "IMMUTABLE" : "VOLATILE")} AS $body${p.Value}$body$;";
    }))+"""
        CREATE TRIGGER trg_source_handoff BEFORE INSERT OR UPDATE OR DELETE ON source_handoff FOR EACH ROW EXECUTE FUNCTION guard_source_handoff();
        CREATE TRIGGER trg_handoff_truncate BEFORE TRUNCATE ON source_handoff FOR EACH STATEMENT EXECUTE FUNCTION guard_source_handoff();
        """;
    public static string Down=>"""
        LOCK TABLE source_handoff,source_intake IN ACCESS EXCLUSIVE MODE;
        DO $body$ BEGIN IF EXISTS(SELECT 1 FROM source_handoff) THEN RAISE EXCEPTION 'Source handoffs prohibit schema downgrade'; END IF; END $body$;
        DROP TABLE source_handoff;
        DROP FUNCTION guard_source_handoff(),handoff_payload(uuid,uuid,uuid,text,text,text),handoff_sort_key(text),handoff_encode(text[]);
        """+string.Join('\n',new Dictionary<string,string>{["guard_source_intake"]=SourceIntakeSchema.SourceLockBody,["guard_intake_group"]=SourceIntakeSchema.GroupBody,["guard_managed_item"]=SourceReturnSchema.ManagedItemBody,["guard_intake_request"]=SourceIntakeSchema.RequestBody}
            .Select(p=>$"CREATE OR REPLACE FUNCTION {p.Key}() RETURNS trigger LANGUAGE plpgsql AS $body${p.Value}$body$;"));
}
