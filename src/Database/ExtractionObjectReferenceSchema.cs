namespace Emenda.Database;

/// <summary>Object-level fencing covers all writers, including independent practice/item APIs.</summary>
internal static class ExtractionObjectReferenceSchema
{
    internal const string Guard = """
        DECLARE paths jsonb; object_path text; BEGIN
            IF TG_TABLE_NAME='mistake_item' THEN
                IF TG_OP='UPDATE' AND NEW.source_regions IS NOT DISTINCT FROM OLD.source_regions THEN RETURN NEW; END IF;
                SELECT coalesce(jsonb_agg(r->>'sourceImagePath'),'[]'::jsonb) INTO paths FROM jsonb_array_elements(NEW.source_regions) r;
            ELSE
                IF TG_OP='UPDATE' AND NEW.image_paths IS NOT DISTINCT FROM OLD.image_paths THEN RETURN NEW; END IF;
                -- Match publication/FK lock order: parent item before referenced objects.
                PERFORM 1 FROM mistake_item WHERE id=NEW.mistake_item_id FOR KEY SHARE;
                paths := NEW.image_paths;
            END IF;
            FOR object_path IN SELECT DISTINCT p FROM jsonb_array_elements_text(paths) p
                WHERE p LIKE 'mistakes/extractions/%/versions/%' OR p LIKE 'mistakes/extractions/%/supplements/%' ORDER BY p LOOP
                PERFORM pg_advisory_xact_lock(hashtextextended(object_path,0));
                PERFORM 1 FROM extraction_object WHERE path=object_path AND NOT pending FOR KEY SHARE;
                IF NOT FOUND THEN RAISE EXCEPTION 'extraction_object_retired' USING ERRCODE='23514'; END IF;
            END LOOP;
            RETURN NEW;
        END
        """;
    internal const string Up = "CREATE FUNCTION guard_extraction_object_reference() RETURNS trigger LANGUAGE plpgsql AS $guard$" + Guard + "$guard$;" + """
        CREATE TRIGGER trg_extraction_item_reference BEFORE INSERT OR UPDATE ON mistake_item FOR EACH ROW EXECUTE FUNCTION guard_extraction_object_reference();
        CREATE TRIGGER trg_extraction_practice_reference BEFORE INSERT OR UPDATE ON mistake_practice FOR EACH ROW EXECUTE FUNCTION guard_extraction_object_reference();
        """;
    internal static readonly string[] Triggers =
    [
        "mistake_item|trg_extraction_item_reference|23|guard_extraction_object_reference|False|False",
        "mistake_practice|trg_extraction_practice_reference|23|guard_extraction_object_reference|False|False"
    ];
}
