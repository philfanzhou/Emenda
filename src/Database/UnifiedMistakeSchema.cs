namespace Emenda.Database;

/// <summary>
/// Schema v24: every mistake carries one source-independent shape next to its source-specific columns.
/// <list type="bullet">
/// <item><c>source_kind</c>: <c>extraction</c>, <c>conversation</c> or <c>legacy</c> (platform-era rows).</item>
/// <item><c>source_caller</c>: the product caller that owns the row; NULL for legacy rows.</item>
/// <item><c>content</c>: the question as one JSON shape, whose <c>images</c> list matches
/// <c>source_regions</c> one to one and in order, each image marked as a question image or an original.</item>
/// <item><c>source_detail</c>: what only one source has (an extraction's task and retained files,
/// a conversation's complete evidence snapshot byte for byte); NULL for legacy rows.</item>
/// </list>
/// During this phase the existing columns stay authoritative: a trigger derives the new columns on every
/// insert and update, so every writer (including raw SQL) keeps them consistent.
/// </summary>
public static class UnifiedMistakeSchema
{
    public const string Migration = "20261126000000_UnifiedMistakeSource";

    internal const string Body = """
        DECLARE
            regions jsonb := CASE WHEN jsonb_typeof(NEW.source_regions)='array' THEN NEW.source_regions ELSE '[]'::jsonb END;
            evidence jsonb := NEW.conversation_source;
            snapshot jsonb := NULL;
            job_text text := substr(NEW.source_upload_id, 9);
            job uuid := NULL;
            job_caller text := NULL;
            images jsonb;
        BEGIN
            IF evidence IS NOT NULL THEN
                NEW.source_kind := 'conversation';
                NEW.source_caller := NEW.conversation_caller;
                NEW.source_detail := evidence;
            ELSIF NEW.source_upload_id LIKE 'extract:%' THEN
                NEW.source_kind := 'extraction';
                snapshot := NEW.analysis_snapshot;
                IF job_text ~ '^[0-9a-f]{32}$' THEN
                    job := job_text::uuid;
                    SELECT substr(j.caller, 8) INTO job_caller FROM extraction_job j WHERE j.id=job AND j.caller LIKE 'caller:%';
                END IF;
                -- A deleted task leaves only an owner hash, so the caller resolved earlier is kept.
                NEW.source_caller := coalesce(job_caller, NEW.source_caller);
                NEW.source_detail := jsonb_strip_nulls(jsonb_build_object('jobId', job, 'questionId', NEW.question_id,
                    'sourceVersion', snapshot->'sourceVersion', 'retainedFiles', snapshot->'retainedFiles'));
            ELSE
                NEW.source_kind := 'legacy';
                NEW.source_caller := NULL;
                NEW.source_detail := NULL;
            END IF;
            -- A conversation image with an asset id is the question crop; every other conversation image is an original.
            SELECT coalesce(jsonb_agg(jsonb_build_object('path', r.value->>'sourceImagePath', 'role', i.role, 'box', i.box)
                ORDER BY r.ordinality), '[]'::jsonb) INTO images
            FROM jsonb_array_elements(regions) WITH ORDINALITY AS r(value, ordinality)
            CROSS JOIN LATERAL (SELECT (SELECT f.value FROM jsonb_array_elements(CASE WHEN jsonb_typeof(evidence->'images')='array'
                    THEN evidence->'images' ELSE '[]'::jsonb END) AS f(value)
                WHERE f.value->>'path'=r.value->>'sourceImagePath' AND jsonb_typeof(f.value->'assetId')='string' LIMIT 1) AS crop) a
            CROSS JOIN LATERAL (SELECT
                CASE WHEN evidence IS NOT NULL AND a.crop IS NULL THEN 'source' ELSE 'question' END AS role,
                CASE WHEN evidence IS NOT NULL THEN CASE WHEN jsonb_typeof(a.crop->'box')='array' THEN a.crop->'box' END
                     WHEN jsonb_typeof(r.value->'boundingBox')='object' THEN jsonb_build_array(r.value->'boundingBox'->'x1',
                         r.value->'boundingBox'->'y1', r.value->'boundingBox'->'x2', r.value->'boundingBox'->'y2') END AS box) i;
            NEW.content := jsonb_build_object('version', 1,
                'statement', coalesce(evidence->'statement', snapshot->'statement'),
                'studentAnswer', coalesce(evidence->'studentAttempt'->'quote', snapshot->'studentAnswer'),
                'referenceAnswer', coalesce(evidence->'referenceAnswer', snapshot->'referenceAnswer'),
                'explanation', snapshot->'explanation',
                'evidence', coalesce(evidence->'evidence', snapshot->'evidence'),
                'knowledgePoints', CASE WHEN jsonb_typeof(evidence->'knowledgePoints')='array' THEN evidence->'knowledgePoints' ELSE '[]'::jsonb END,
                'images', images);
            RETURN NEW;
        END
        """;

    public const string Up = """
        ALTER TABLE mistake_item ADD COLUMN source_kind text, ADD COLUMN source_caller text,
            ADD COLUMN content jsonb, ADD COLUMN source_detail jsonb;
        CREATE FUNCTION derive_mistake_item_source() RETURNS trigger LANGUAGE plpgsql AS $derive$
        """ + Body + """
        $derive$;
        CREATE TRIGGER trg_mistake_item_source BEFORE INSERT OR UPDATE ON mistake_item FOR EACH ROW EXECUTE FUNCTION derive_mistake_item_source();
        -- Fires the trigger once per row. updated_at is untouched, so optimistic concurrency tokens stay valid.
        UPDATE mistake_item SET source_upload_id=source_upload_id;
        -- An extraction whose task was already deleted keeps only sha256('caller:' || name). Resolve it against every caller
        -- name the database knows; a row that stays NULL keeps its current visibility, which still uses the deletion receipt.
        UPDATE mistake_item m SET source_caller=n.name
        FROM extraction_deletion d, (
            SELECT substr(caller, 8) AS name FROM extraction_job WHERE caller LIKE 'caller:%'
            UNION SELECT conversation_caller FROM mistake_item WHERE conversation_caller IS NOT NULL
            UNION SELECT caller_name FROM conversation_collection_job) n
        WHERE m.source_kind='extraction' AND m.source_caller IS NULL
          AND m.source_upload_id='extract:' || replace(d.job_id::text, '-', '')
          AND d.owner_hash=encode(sha256(convert_to('caller:' || n.name, 'UTF8')), 'hex');
        ALTER TABLE mistake_item ALTER COLUMN source_kind SET NOT NULL, ALTER COLUMN content SET NOT NULL,
            ADD CONSTRAINT "CK_mistake_item_source_kind" CHECK (source_kind IN ('extraction','conversation','legacy')),
            ADD CONSTRAINT "CK_mistake_item_source_detail" CHECK ((source_kind='legacy')=(source_detail IS NULL));
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM mistake_item m WHERE
                    jsonb_array_length(m.content->'images')<>jsonb_array_length(m.source_regions)
                    OR (m.source_kind='conversation' AND m.source_detail IS DISTINCT FROM m.conversation_source)
                    OR EXISTS (SELECT 1 FROM jsonb_array_elements(m.source_regions) WITH ORDINALITY AS r(value, ordinality)
                        WHERE m.content->'images'->((r.ordinality-1)::int)->>'path' IS DISTINCT FROM r.value->>'sourceImagePath'))
            THEN RAISE EXCEPTION 'unified_mistake_backfill_mismatch'; END IF;
        END $$;
        """;

    // Forward-only like v20, v22 and v23: a downgrade attempt fails before it changes anything. The derived
    // columns hold no data of their own, so a manual rollback only needs to drop them (see the design document).
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'unified_mistake_source_irreversible'; END $$;";

    internal static readonly string[] Columns =
    [
        "mistake_item|source_kind|text|N",
        "mistake_item|source_caller|text|Y",
        "mistake_item|content|jsonb|N",
        "mistake_item|source_detail|jsonb|Y"
    ];

    internal static readonly string[] Constraints =
    [
        "CK_mistake_item_source_kind|c|source_kind|-|-|-|V",
        "CK_mistake_item_source_detail|c|source_kind,source_detail|-|-|-|V"
    ];

    internal const string Trigger = "mistake_item|trg_mistake_item_source|23|derive_mistake_item_source|False|False";
}
