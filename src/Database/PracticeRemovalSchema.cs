namespace Emenda.Database;

internal static class PracticeRemovalSchema
{
    internal const string Migration = "20261201000000_RetireMistakePractice";
    internal const string ItemGuard = """
        DECLARE paths jsonb; object_path text; BEGIN
            IF TG_OP='UPDATE' AND NEW.source_regions IS NOT DISTINCT FROM OLD.source_regions THEN RETURN NEW; END IF;
            SELECT coalesce(jsonb_agg(r->>'sourceImagePath'),'[]'::jsonb) INTO paths FROM jsonb_array_elements(NEW.source_regions) r;
            FOR object_path IN SELECT DISTINCT p FROM jsonb_array_elements_text(paths) p
                WHERE p LIKE 'mistakes/extractions/%/versions/%' OR p LIKE 'mistakes/extractions/%/supplements/%' OR p LIKE 'mistakes/extractions/%/originals/%' ORDER BY p LOOP
                PERFORM pg_advisory_xact_lock(hashtextextended(object_path,0));
                PERFORM 1 FROM extraction_object WHERE path=object_path AND NOT pending FOR KEY SHARE;
                IF NOT FOUND THEN RAISE EXCEPTION 'extraction_object_retired' USING ERRCODE='23514'; END IF;
            END LOOP;
            RETURN NEW;
        END
        """;
}
