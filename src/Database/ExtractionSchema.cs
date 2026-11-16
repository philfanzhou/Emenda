namespace Emenda.Database;

public static class ExtractionSchema
{
    public const string Migration = "20261116000000_AddExtractions";
    internal const string RevisionGuard = """
        BEGIN
            RAISE EXCEPTION 'extraction_revision_immutable';
        END
        """;
    internal const string RetentionGuard = """
        BEGIN
            IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                RAISE EXCEPTION 'extraction_history_retained';
            END IF;
            IF NEW.id IS DISTINCT FROM OLD.id OR NEW.job_id IS DISTINCT FROM OLD.job_id
                OR (OLD.item_id IS NOT NULL AND NEW.item_id IS DISTINCT FROM OLD.item_id) THEN
                RAISE EXCEPTION 'extraction_question_identity_immutable';
            END IF;
            RETURN NEW;
        END
        """;
    internal static readonly Dictionary<string, string> Bodies = new()
    {
        ["guard_extraction_revision"] = RevisionGuard,
        ["guard_extraction_retention"] = RetentionGuard
    };
    internal static readonly string[] Triggers =
    [
        "extraction_revision|trg_extraction_revision|27|guard_extraction_revision|False|False",
        "extraction_revision|trg_extraction_revision_truncate|34|guard_extraction_revision|False|False",
        "extraction_job|trg_extraction_job_delete|10|guard_extraction_retention|False|False",
        "extraction_job|trg_extraction_job_truncate|34|guard_extraction_retention|False|False",
        "extraction_question|trg_extraction_question|27|guard_extraction_retention|False|False",
        "extraction_question|trg_extraction_question_truncate|34|guard_extraction_retention|False|False"
    ];
    public static readonly string Up = """
        CREATE TABLE extraction_job (
            id uuid NOT NULL, caller text NOT NULL, source_key character varying(64) NOT NULL,
            payload_hash character varying(64) NOT NULL, student_id character varying(36) NOT NULL,
            grade integer NOT NULL, status text NOT NULL, version integer NOT NULL DEFAULT 0,
            sources_json jsonb NOT NULL DEFAULT '[]'::jsonb, layout_json jsonb NOT NULL DEFAULT '[]'::jsonb,
            candidate_json jsonb, error_code text, created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL, owner_id uuid, fence bigint NOT NULL DEFAULT 0,
            lease_until timestamp with time zone, attempt integer NOT NULL DEFAULT 0, last_retry_key uuid,
            retry_keys_json jsonb NOT NULL DEFAULT '[]'::jsonb,
            CONSTRAINT "PK_extraction_job" PRIMARY KEY (id)
        );
        CREATE UNIQUE INDEX "IX_extraction_job_caller_source_key" ON extraction_job (caller, source_key);
        CREATE TABLE extraction_question (
            id uuid NOT NULL, job_id uuid NOT NULL, item_id uuid, active boolean NOT NULL,
            CONSTRAINT "PK_extraction_question" PRIMARY KEY (id),
            CONSTRAINT "FK_extraction_question_job" FOREIGN KEY (job_id) REFERENCES extraction_job(id) ON DELETE RESTRICT
        );
        CREATE INDEX "IX_extraction_question_job_id" ON extraction_question(job_id);
        CREATE TABLE extraction_revision (
            job_id uuid NOT NULL, version integer NOT NULL, request_key uuid NOT NULL,
            payload_hash character varying(64) NOT NULL, snapshot_json jsonb NOT NULL,
            actor text NOT NULL, reason text NOT NULL, model text NOT NULL, created_at timestamp with time zone NOT NULL,
            CONSTRAINT "PK_extraction_revision" PRIMARY KEY (job_id, version),
            CONSTRAINT "FK_extraction_revision_job" FOREIGN KEY (job_id) REFERENCES extraction_job(id) ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_extraction_revision_job_request_key" ON extraction_revision(job_id, request_key);
        """ + $"""
        CREATE FUNCTION guard_extraction_revision() RETURNS trigger LANGUAGE plpgsql AS $guard${RevisionGuard}$guard$;
        CREATE FUNCTION guard_extraction_retention() RETURNS trigger LANGUAGE plpgsql AS $guard${RetentionGuard}$guard$;
        CREATE TRIGGER trg_extraction_revision BEFORE UPDATE OR DELETE ON extraction_revision FOR EACH ROW EXECUTE FUNCTION guard_extraction_revision();
        CREATE TRIGGER trg_extraction_revision_truncate BEFORE TRUNCATE ON extraction_revision FOR EACH STATEMENT EXECUTE FUNCTION guard_extraction_revision();
        CREATE TRIGGER trg_extraction_job_delete BEFORE DELETE ON extraction_job FOR EACH STATEMENT EXECUTE FUNCTION guard_extraction_retention();
        CREATE TRIGGER trg_extraction_job_truncate BEFORE TRUNCATE ON extraction_job FOR EACH STATEMENT EXECUTE FUNCTION guard_extraction_retention();
        CREATE TRIGGER trg_extraction_question BEFORE UPDATE OR DELETE ON extraction_question FOR EACH ROW EXECUTE FUNCTION guard_extraction_retention();
        CREATE TRIGGER trg_extraction_question_truncate BEFORE TRUNCATE ON extraction_question FOR EACH STATEMENT EXECUTE FUNCTION guard_extraction_retention();
        """;
    public const string Down = """
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM extraction_job) OR EXISTS (SELECT 1 FROM extraction_question)
                OR EXISTS (SELECT 1 FROM extraction_revision) THEN
                RAISE EXCEPTION 'extraction_history_present';
            END IF;
        END $$;
        DROP TABLE extraction_revision, extraction_question, extraction_job;
        DROP FUNCTION guard_extraction_revision();
        DROP FUNCTION guard_extraction_retention();
        """;
    internal static readonly string[] Tables = ["extraction_job", "extraction_question", "extraction_revision"];
    internal static readonly string[] Columns =
    [
        "extraction_job|id|uuid|N", "extraction_job|caller|text|N", "extraction_job|source_key|character varying(64)|N",
        "extraction_job|payload_hash|character varying(64)|N", "extraction_job|student_id|character varying(36)|N",
        "extraction_job|grade|integer|N", "extraction_job|status|text|N", "extraction_job|version|integer|N",
        "extraction_job|sources_json|jsonb|N", "extraction_job|layout_json|jsonb|N", "extraction_job|candidate_json|jsonb|Y",
        "extraction_job|error_code|text|Y", "extraction_job|created_at|timestamp with time zone|N",
        "extraction_job|updated_at|timestamp with time zone|N", "extraction_job|owner_id|uuid|Y",
        "extraction_job|fence|bigint|N", "extraction_job|lease_until|timestamp with time zone|Y",
        "extraction_job|attempt|integer|N", "extraction_job|last_retry_key|uuid|Y",
        "extraction_job|retry_keys_json|jsonb|N",
        "extraction_question|id|uuid|N", "extraction_question|job_id|uuid|N", "extraction_question|item_id|uuid|Y", "extraction_question|active|boolean|N",
        "extraction_revision|job_id|uuid|N", "extraction_revision|version|integer|N", "extraction_revision|request_key|uuid|N",
        "extraction_revision|payload_hash|character varying(64)|N", "extraction_revision|snapshot_json|jsonb|N",
        "extraction_revision|actor|text|N", "extraction_revision|reason|text|N", "extraction_revision|model|text|N",
        "extraction_revision|created_at|timestamp with time zone|N"
    ];
    internal static readonly string[] Defaults = ["extraction_job|version|0", "extraction_job|fence|0", "extraction_job|attempt|0", "extraction_job|sources_json|'[]'::jsonb", "extraction_job|layout_json|'[]'::jsonb", "extraction_job|retry_keys_json|'[]'::jsonb"];
    internal static readonly string[] Indexes =
    [
        "PK_extraction_job|extraction_job|id|U", "IX_extraction_job_caller_source_key|extraction_job|caller,source_key|U",
        "PK_extraction_question|extraction_question|id|U", "IX_extraction_question_job_id|extraction_question|job_id|N",
        "PK_extraction_revision|extraction_revision|job_id,version|U", "IX_extraction_revision_job_request_key|extraction_revision|job_id,request_key|U"
    ];
    internal static readonly string[] Constraints =
    [
        "PK_extraction_job|p|id|-|-|-|V", "PK_extraction_question|p|id|-|-|-|V", "PK_extraction_revision|p|job_id,version|-|-|-|V",
        "FK_extraction_question_job|f|job_id|extraction_job|id|r|V", "FK_extraction_revision_job|f|job_id|extraction_job|id|r|V"
    ];
}
