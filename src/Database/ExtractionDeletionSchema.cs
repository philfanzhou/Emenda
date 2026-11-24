namespace Emenda.Database;

public static class ExtractionDeletionSchema
{
    public const string Migration = "20261124010000_ExtractionDeletion";

    // A committed receipt never serves as a general delete flag. Only rows for the
    // requested job can be removed, and only in the transaction that creates its receipt.
    private const string JobReceipt = """
        EXISTS (SELECT 1 FROM public.extraction_deletion d WHERE d.job_id=target_job
            AND d.xmin=pg_current_xact_id()::xid AND d.deleted_at>=transaction_timestamp())
        """;
    internal static readonly string RevisionGuard = """
        DECLARE target_job uuid;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                target_job := OLD.job_id;
                IF
        """ + " " + JobReceipt + " " + """
                 THEN RETURN OLD; END IF;
            END IF;
            IF TG_OP = 'UPDATE' AND NEW.job_id = OLD.job_id AND NEW.version = OLD.version
                AND NEW.request_key = OLD.request_key AND NEW.payload_hash = OLD.payload_hash
                AND NEW.actor = OLD.actor AND NEW.reason = OLD.reason AND NEW.model = OLD.model
                AND NEW.created_at = OLD.created_at AND NEW.snapshot_json = '{"questions":[]}'::jsonb THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'extraction_revision_immutable';
        END
        """;
    internal static readonly string RetentionGuard = """
        DECLARE target_job uuid;
        BEGIN
            IF TG_OP = 'TRUNCATE' THEN RAISE EXCEPTION 'extraction_history_retained'; END IF;
            IF TG_OP = 'DELETE' THEN
                IF TG_TABLE_NAME = 'extraction_job' THEN target_job := OLD.id;
                ELSE target_job := OLD.job_id; END IF;
                IF
        """ + " " + JobReceipt + " " + """
                 THEN RETURN OLD; END IF;
                RAISE EXCEPTION 'extraction_history_retained';
            END IF;
            IF NEW.id IS DISTINCT FROM OLD.id OR NEW.job_id IS DISTINCT FROM OLD.job_id
                OR (OLD.item_id IS NOT NULL AND NEW.item_id IS DISTINCT FROM OLD.item_id) THEN
                RAISE EXCEPTION 'extraction_question_identity_immutable';
            END IF;
            RETURN NEW;
        END
        """;
    internal const string ReceiptGuard = """
        BEGIN
            RAISE EXCEPTION 'extraction_deletion_receipt_immutable';
        END
        """;
    internal static readonly string ReferenceGuard = ExtractionObjectReferenceSchema.Guard.Replace(
        "OR p LIKE 'mistakes/extractions/%/supplements/%' ORDER BY p LOOP",
        "OR p LIKE 'mistakes/extractions/%/supplements/%' OR p LIKE 'mistakes/extractions/%/originals/%' ORDER BY p LOOP",
        StringComparison.Ordinal);

    internal static readonly string Up = """
        ALTER TABLE public.extraction_job ALTER COLUMN grade DROP NOT NULL;
        ALTER TABLE public.extraction_job ADD COLUMN content_hash character varying(64);
        CREATE INDEX "IX_extraction_job_caller_student_id" ON public.extraction_job(caller, student_id);
        CREATE INDEX "IX_extraction_object_job_id" ON public.extraction_object(job_id);
        CREATE TABLE public.extraction_deletion (
            job_id uuid NOT NULL, admission_hash character varying(64) NOT NULL, source_binding_hash character varying(64) NOT NULL,
            owner_hash character varying(64) NOT NULL,
            deleted_at timestamp with time zone NOT NULL, deleted_mistakes integer NOT NULL, retained_confirmed integer NOT NULL,
            CONSTRAINT "PK_extraction_deletion" PRIMARY KEY (job_id)
        );
        CREATE INDEX "IX_extraction_deletion_admission_hash" ON public.extraction_deletion(admission_hash);
        CREATE INDEX "IX_extraction_deletion_source_binding_hash" ON public.extraction_deletion(source_binding_hash);
        INSERT INTO public.extraction_object(path,job_id,pending,legacy,attempts,next_attempt)
        SELECT s->>'path', j.id, j.status='Staging', false, 0, clock_timestamp()
        FROM public.extraction_job j CROSS JOIN LATERAL jsonb_array_elements(j.sources_json) s
        WHERE s->>'path' LIKE 'mistakes/extractions/' || replace(j.id::text, '-', '') || '/originals/%'
        ON CONFLICT(path) DO NOTHING;
        DROP TRIGGER trg_extraction_job_delete ON public.extraction_job;
        CREATE TRIGGER trg_extraction_job_delete BEFORE DELETE ON public.extraction_job FOR EACH ROW EXECUTE FUNCTION public.guard_extraction_retention();
        CREATE TRIGGER trg_extraction_deletion_immutable BEFORE UPDATE OR DELETE ON public.extraction_deletion FOR EACH ROW EXECUTE FUNCTION public.guard_extraction_deletion_receipt();
        CREATE TRIGGER trg_extraction_deletion_truncate BEFORE TRUNCATE ON public.extraction_deletion FOR EACH STATEMENT EXECUTE FUNCTION public.guard_extraction_deletion_receipt();
        """;

    internal static string Install =>
        $"CREATE FUNCTION public.guard_extraction_deletion_receipt() RETURNS trigger LANGUAGE plpgsql AS $guard${ReceiptGuard}$guard$;" + Up +
        $"CREATE OR REPLACE FUNCTION public.guard_extraction_revision() RETURNS trigger LANGUAGE plpgsql AS $guard${RevisionGuard}$guard$;" +
        $"CREATE OR REPLACE FUNCTION public.guard_extraction_retention() RETURNS trigger LANGUAGE plpgsql AS $guard${RetentionGuard}$guard$;" +
        $"CREATE OR REPLACE FUNCTION public.guard_extraction_object_reference() RETURNS trigger LANGUAGE plpgsql AS $guard${ReferenceGuard}$guard$;";

    internal static readonly string[] Columns =
    [
        "extraction_job|grade|integer|Y", "extraction_job|content_hash|character varying(64)|Y",
        "extraction_deletion|job_id|uuid|N", "extraction_deletion|admission_hash|character varying(64)|N",
        "extraction_deletion|owner_hash|character varying(64)|N",
        "extraction_deletion|source_binding_hash|character varying(64)|N", "extraction_deletion|deleted_at|timestamp with time zone|N",
        "extraction_deletion|deleted_mistakes|integer|N", "extraction_deletion|retained_confirmed|integer|N"
    ];
    internal static readonly string[] Indexes =
    [
        "PK_extraction_deletion|extraction_deletion|job_id|U", "IX_extraction_job_caller_student_id|extraction_job|caller,student_id|N",
        "IX_extraction_object_job_id|extraction_object|job_id|N",
        "IX_extraction_deletion_admission_hash|extraction_deletion|admission_hash|N",
        "IX_extraction_deletion_source_binding_hash|extraction_deletion|source_binding_hash|N"
    ];
    internal static readonly string[] Triggers =
    [
        "extraction_job|trg_extraction_job_delete|11|guard_extraction_retention|False|False",
        "extraction_deletion|trg_extraction_deletion_immutable|27|guard_extraction_deletion_receipt|False|False",
        "extraction_deletion|trg_extraction_deletion_truncate|34|guard_extraction_deletion_receipt|False|False"
    ];
}
