namespace Emenda.Database;

public static class WorksheetAnalysisSchema
{
    public const string Migration = "20261124000000_WorksheetAnalysis";
    internal const string Column = "mistake_item|analysis_snapshot|jsonb|Y";

    // Earlier revision bodies are retired; use the revision that actually supplied the text.
    // Lock candidates in publication order and never update a row changed by a concurrent correction.
    public const string Backfill = """
        WITH candidates AS MATERIALIZED (
            SELECT m.id, jsonb_build_object(
                'statement', draft->'statement', 'studentAnswer', draft->'studentAnswer',
                'referenceAnswer', draft->'referenceAnswer', 'explanation', draft->'explanation',
                'evidence', draft->'evidence', 'sourceVersion', r.version) AS snapshot
            FROM public.mistake_item m
            JOIN public.extraction_question q ON q.item_id=m.id AND q.active AND q.id::text=m.question_id
            JOIN public.extraction_job j ON j.id=q.job_id AND j.student_id=m.student_id
                AND m.source_upload_id='extract:' || replace(j.id::text, '-', '')
            JOIN public.extraction_revision r ON r.job_id=j.id AND r.version=j.version AND r.version>0
            CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(r.snapshot_json->'questions')='array'
                THEN r.snapshot_json->'questions' ELSE '[]'::jsonb END) AS entry(view)
            CROSS JOIN LATERAL (SELECT entry.view->'question' AS draft) AS question
            WHERE m.analysis_snapshot IS NULL AND m.conversation_source IS NULL AND m.review_status IN (1, 2)
                AND entry.view->>'itemId'=m.id::text AND entry.view->'itemDeleted' IS DISTINCT FROM 'true'::jsonb
                AND draft->>'id'=q.id::text AND draft->>'outcome'='incorrect'
                AND jsonb_typeof(draft->'statement')='string' AND draft->>'statement' ~ '[^[:space:]]'
                AND jsonb_typeof(draft->'studentAnswer')='string' AND draft->>'studentAnswer' ~ '[^[:space:]]'
                AND jsonb_typeof(draft->'referenceAnswer')='string' AND draft->>'referenceAnswer' ~ '[^[:space:]]'
                AND jsonb_typeof(draft->'explanation')='string'
                AND jsonb_typeof(draft->'evidence')='string' AND draft->>'evidence' ~ '[^[:space:]]'
            ORDER BY m.id FOR UPDATE OF m
        )
        UPDATE public.mistake_item m SET analysis_snapshot=c.snapshot
        FROM candidates c WHERE m.id=c.id AND m.analysis_snapshot IS NULL;
        """;
}
