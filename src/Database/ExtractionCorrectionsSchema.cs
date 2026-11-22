namespace Emenda.Database;

public static class ExtractionCorrectionsSchema
{
    public const string Migration = "20261122010000_ExtractionCorrections";
    internal const string RevisionGuard = """
        BEGIN
            IF TG_OP = 'UPDATE' AND NEW.job_id = OLD.job_id AND NEW.version = OLD.version
                AND NEW.request_key = OLD.request_key AND NEW.payload_hash = OLD.payload_hash
                AND NEW.actor = OLD.actor AND NEW.reason = OLD.reason AND NEW.model = OLD.model
                AND NEW.created_at = OLD.created_at AND NEW.snapshot_json = '{"questions":[]}'::jsonb THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'extraction_revision_immutable';
        END
        """;
    public static readonly string Up = """
        CREATE TABLE extraction_object (path text NOT NULL, job_id uuid NOT NULL, pending boolean NOT NULL, legacy boolean NOT NULL, attempts integer NOT NULL, next_attempt timestamp with time zone NOT NULL, CONSTRAINT "PK_extraction_object" PRIMARY KEY (path));
        CREATE INDEX "IX_extraction_object_pending_next_attempt" ON extraction_object (pending,next_attempt);
        CREATE TABLE extraction_proposal (id uuid NOT NULL, job_id uuid NOT NULL, request_key uuid NOT NULL, payload_hash text NOT NULL, expected_version integer NOT NULL, status text NOT NULL, request_json jsonb, result_json jsonb, owner uuid, lease_until timestamp with time zone, attempts integer NOT NULL, error_code text, created_at timestamp with time zone NOT NULL, CONSTRAINT "PK_extraction_proposal" PRIMARY KEY (id));
        CREATE UNIQUE INDEX "IX_extraction_proposal_job_key" ON extraction_proposal (job_id,request_key);
        CREATE TABLE extraction_supplement (id uuid NOT NULL, job_id uuid NOT NULL, question_id uuid NOT NULL, item_id uuid NOT NULL, request_key uuid NOT NULL, payload_hash text NOT NULL, expected_version integer NOT NULL, accepted_version integer, parent_id uuid, status text NOT NULL, note text NOT NULL, sources_json jsonb NOT NULL, created_at timestamp with time zone NOT NULL, CONSTRAINT "PK_extraction_supplement" PRIMARY KEY (id));
        CREATE UNIQUE INDEX "IX_extraction_supplement_job_key" ON extraction_supplement (job_id,request_key);
        LOCK TABLE extraction_revision, extraction_job, mistake_item, process_log IN ACCESS EXCLUSIVE MODE;
        ALTER TABLE extraction_revision DISABLE TRIGGER trg_extraction_revision;
        INSERT INTO extraction_object(path,job_id,pending,legacy,attempts,next_attempt)
        SELECT DISTINCT i->>'path', r.job_id, true, true, 0, clock_timestamp()
        FROM extraction_revision r CROSS JOIN LATERAL jsonb_array_elements(r.snapshot_json->'questions') q
        CROSS JOIN LATERAL jsonb_array_elements(q->'images') i
        WHERE i->>'path' LIKE 'mistakes/extractions/%/versions/%'
        ON CONFLICT (path) DO NOTHING;
        UPDATE extraction_revision r SET snapshot_json = jsonb_build_object('questions',
            COALESCE((SELECT jsonb_agg(CASE WHEN q->'question'->>'outcome'='incorrect' AND EXISTS(
                SELECT 1 FROM mistake_item m WHERE m.id::text=q->>'itemId') THEN q
                ELSE jsonb_set(q,'{images}','[]'::jsonb) END ORDER BY ordinal)
            FROM jsonb_array_elements(r.snapshot_json->'questions') WITH ORDINALITY AS entries(q,ordinal)), '[]'::jsonb))
        FROM extraction_job j WHERE r.job_id=j.id AND r.version=j.version;
        UPDATE extraction_revision r SET snapshot_json='{"questions":[]}'::jsonb
        FROM extraction_job j WHERE r.job_id=j.id AND r.version<>j.version;
        UPDATE mistake_item m SET source_regions='[]'::jsonb
        WHERE m.source_upload_id LIKE 'extract:%' AND NOT EXISTS(
            SELECT 1 FROM extraction_revision r JOIN extraction_job j ON j.id=r.job_id AND j.version=r.version,
            LATERAL jsonb_array_elements(r.snapshot_json->'questions') q
            WHERE q->>'itemId'=m.id::text AND q->'question'->>'outcome'='incorrect');
        UPDATE extraction_job SET candidate_json=NULL, layout_json='[]'::jsonb WHERE version>0;
        CREATE FUNCTION pg_temp.scrub_extraction_audit(input text) RETURNS text LANGUAGE plpgsql AS $scrub$
        DECLARE value jsonb; BEGIN
            BEGIN value := input::jsonb; EXCEPTION WHEN invalid_text_representation THEN
                RETURN '{"operation":"legacy_extraction_audit","geometryRetired":true}'; END;
            IF jsonb_typeof(value)<>'object' THEN RETURN '{"geometryRetired":true}'; END IF;
            value := value - 'sourceRegions' - 'SourceRegions' - 'regions' - 'images';
            IF jsonb_typeof(value->'previous')='object' THEN
                value := jsonb_set(value,'{previous}', (value->'previous') - 'sourceRegions' - 'SourceRegions' - 'regions' - 'images');
            END IF;
            RETURN value::text;
        END $scrub$;
        UPDATE process_log l SET details=pg_temp.scrub_extraction_audit(l.details)
        FROM mistake_item m WHERE m.id=l.mistake_item_id AND m.source_upload_id LIKE 'extract:%' AND l.details IS NOT NULL;
        UPDATE extraction_object o SET pending=false WHERE EXISTS(
            SELECT 1 FROM extraction_revision r JOIN extraction_job j ON j.id=r.job_id AND j.version=r.version,
            LATERAL jsonb_array_elements(r.snapshot_json->'questions') q,
            LATERAL jsonb_array_elements(q->'images') i WHERE i->>'path'=o.path);
        ALTER TABLE extraction_revision ENABLE TRIGGER trg_extraction_revision;
        """ + "CREATE OR REPLACE FUNCTION guard_extraction_revision() RETURNS trigger LANGUAGE plpgsql AS $guard$" + RevisionGuard + "$guard$;"
        + ExtractionObjectReferenceSchema.Up;
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'extraction_corrections_irreversible'; END $$;";
    internal static readonly string[] Tables = ["extraction_object", "extraction_proposal", "extraction_supplement"];
    internal static readonly string[] Columns =
    [
        "extraction_object|path|text|N",
        "extraction_object|job_id|uuid|N",
        "extraction_object|pending|boolean|N",
        "extraction_object|legacy|boolean|N",
        "extraction_object|attempts|integer|N",
        "extraction_object|next_attempt|timestamp with time zone|N",
        "extraction_proposal|id|uuid|N",
        "extraction_proposal|job_id|uuid|N",
        "extraction_proposal|request_key|uuid|N",
        "extraction_proposal|payload_hash|text|N",
        "extraction_proposal|expected_version|integer|N",
        "extraction_proposal|status|text|N",
        "extraction_proposal|request_json|jsonb|Y",
        "extraction_proposal|result_json|jsonb|Y",
        "extraction_proposal|owner|uuid|Y",
        "extraction_proposal|lease_until|timestamp with time zone|Y",
        "extraction_proposal|attempts|integer|N",
        "extraction_proposal|error_code|text|Y",
        "extraction_proposal|created_at|timestamp with time zone|N",
        "extraction_supplement|id|uuid|N",
        "extraction_supplement|job_id|uuid|N",
        "extraction_supplement|question_id|uuid|N",
        "extraction_supplement|item_id|uuid|N",
        "extraction_supplement|request_key|uuid|N",
        "extraction_supplement|payload_hash|text|N",
        "extraction_supplement|expected_version|integer|N",
        "extraction_supplement|accepted_version|integer|Y",
        "extraction_supplement|parent_id|uuid|Y",
        "extraction_supplement|status|text|N",
        "extraction_supplement|note|text|N",
        "extraction_supplement|sources_json|jsonb|N",
        "extraction_supplement|created_at|timestamp with time zone|N"
    ];
    internal static readonly string[] Indexes =
    [
        "PK_extraction_object|extraction_object|path|U",
        "IX_extraction_object_pending_next_attempt|extraction_object|pending,next_attempt|N",
        "PK_extraction_proposal|extraction_proposal|id|U",
        "IX_extraction_proposal_job_key|extraction_proposal|job_id,request_key|U",
        "PK_extraction_supplement|extraction_supplement|id|U",
        "IX_extraction_supplement_job_key|extraction_supplement|job_id,request_key|U"
    ];
    internal static readonly string[] Constraints =
    [
        "PK_extraction_object|p|path|-|-|-|V",
        "PK_extraction_proposal|p|id|-|-|-|V",
        "PK_extraction_supplement|p|id|-|-|-|V"
    ];
}
