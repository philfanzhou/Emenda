namespace Emenda.Database;

/// <summary>
/// Schema v26 (unified intake phase C): the unified columns become the record. Drops the source-specific columns
/// <c>analysis_snapshot</c>, <c>conversation_source</c> and <c>conversation_caller</c> together with the v24 trigger
/// <c>trg_mistake_item_source</c> and its function, which derived the unified columns from them.
/// <para>
/// Nothing is lost: since v24 the trigger (and from rc.19 also <see cref="MistakeMaterializer"/>) kept every row's
/// unified columns derived from the old ones, and each old value is exactly recoverable from them. A conversation's
/// snapshot is <c>source_detail</c> byte for byte and its caller is <c>source_caller</c>; an extraction analysis is
/// <c>content</c>'s five text members plus <c>source_detail.sourceVersion</c> and <c>retainedFiles</c>. The
/// migration checks this for every row first and changes nothing if any row differs.
/// </para>
/// </summary>
public static class UnifiedSourceContractionSchema
{
    public const string Migration = "20261128000000_DropSourceSpecificMistakeColumns";

    public const string Up = """
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM mistake_item m WHERE
                -- A conversation's evidence and caller are the unified source and caller.
                (m.conversation_source IS NOT NULL AND (m.source_kind <> 'conversation'
                    OR m.source_detail IS DISTINCT FROM m.conversation_source OR m.source_caller IS DISTINCT FROM m.conversation_caller))
                OR (m.conversation_source IS NULL AND (m.source_kind = 'conversation' OR m.conversation_caller IS NOT NULL))
                -- An extraction analysis is content's text plus source_detail's version and retained files.
                OR (m.analysis_snapshot IS NOT NULL AND (m.source_kind <> 'extraction'
                    OR jsonb_typeof(m.analysis_snapshot) <> 'object'
                    OR jsonb_typeof(m.analysis_snapshot->'sourceVersion') IS DISTINCT FROM 'number'
                    OR EXISTS (SELECT 1 FROM jsonb_object_keys(m.analysis_snapshot) AS k(name) WHERE k.name NOT IN
                        ('statement','studentAnswer','referenceAnswer','explanation','evidence','sourceVersion','retainedFiles'))
                    OR EXISTS (SELECT 1 FROM unnest(ARRAY['statement','studentAnswer','referenceAnswer','explanation','evidence']) AS f(name)
                        WHERE m.content->f.name IS DISTINCT FROM coalesce(m.analysis_snapshot->f.name, 'null'::jsonb))
                    OR m.source_detail->'sourceVersion' IS DISTINCT FROM m.analysis_snapshot->'sourceVersion'
                    OR m.source_detail->'retainedFiles' IS DISTINCT FROM m.analysis_snapshot->'retainedFiles'))
                OR (m.analysis_snapshot IS NULL AND m.source_kind = 'extraction' AND (m.source_detail->'sourceVersion' IS NOT NULL
                    OR m.source_detail->'retainedFiles' IS NOT NULL
                    OR EXISTS (SELECT 1 FROM unnest(ARRAY['statement','studentAnswer','referenceAnswer','explanation','evidence']) AS f(name)
                        WHERE m.content->f.name IS DISTINCT FROM 'null'::jsonb))))
            THEN RAISE EXCEPTION 'unified_mistake_contraction_mismatch'; END IF;
        END $$;
        DROP TRIGGER trg_mistake_item_source ON mistake_item;
        DROP FUNCTION derive_mistake_item_source();
        ALTER TABLE mistake_item DROP COLUMN analysis_snapshot, DROP COLUMN conversation_source, DROP COLUMN conversation_caller;
        """;

    // Forward-only like v20 and v22-v25: a downgrade attempt fails before it changes anything. The previous
    // application cannot run on this schema; recovery is the backup taken before the upgrade (see the design document).
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'unified_mistake_contraction_irreversible'; END $$;";

    internal static readonly string[] DroppedColumns =
    [
        WorksheetAnalysisSchema.Column,
        "mistake_item|conversation_source|jsonb|Y",
        "mistake_item|conversation_caller|text|Y"
    ];
}
