namespace Emenda.Database;

/// <summary>
/// Schema v19: removes the platform-era managed source structures (source intake, targets,
/// returns, handoffs, legacy returns and layout jobs) together with the guard triggers they
/// installed on the core tables. No current code path writes these structures, so the migration
/// refuses to run when any of them holds a row instead of discarding data. The drop is not reversible.
/// </summary>
public static class RetiredSourceSchema
{
    public const string Migration = "20261122000000_DropManagedSourceTables";

    internal static readonly string[] Tables =
    [
        "source_intake", "source_intake_group", "source_intake_request",
        "source_target_object", "source_target_attempt",
        "source_return", "source_return_object", "source_return_attempt",
        "source_handoff",
        "legacy_return", "legacy_return_object", "legacy_return_attempt",
        "layout_job", "layout_job_image", "layout_request", "layout_result", "layout_result_region"
    ];

    // Guard triggers the retired structures installed on tables that stay.
    private static readonly (string Table, string Trigger)[] CoreTriggers =
    [
        ("mistake_item", "trg_managed_item"), ("mistake_item", "trg_item_truncate"),
        ("mistake_item", "trg_reserved_target_item"), ("mistake_item", "trg_return_integrity_item"),
        ("mistake_item", "trg_return_writer_item"), ("mistake_item", "trg_legacy_return_integrity_item"),
        ("mistake_item", "trg_legacy_return_truncate_item"), ("mistake_item", "trg_legacy_return_writer_item"),
        ("mistake_practice", "trg_reserved_target_practice"), ("mistake_practice", "trg_return_truncate_practice"),
        ("mistake_practice", "trg_return_writer_practice"), ("mistake_practice", "trg_legacy_return_truncate_practice"),
        ("mistake_practice", "trg_legacy_return_writer_practice"),
        ("process_log", "trg_intake_created_log"), ("process_log", "trg_intake_integrity_log"),
        ("process_log", "trg_log_truncate"), ("process_log", "trg_return_integrity_log"),
        ("process_log", "trg_return_writer_log"), ("process_log", "trg_legacy_return_integrity_log"),
        ("process_log", "trg_legacy_return_truncate_log"), ("process_log", "trg_legacy_return_writer_log"),
        ("reanalyze_job", "trg_return_truncate_job"), ("reanalyze_job", "trg_return_writer_job"),
        ("reanalyze_job", "trg_legacy_return_truncate_job"), ("reanalyze_job", "trg_legacy_return_writer_job")
    ];

    private static readonly string[] Functions =
    [
        "guard_source_intake", "guard_intake_group", "guard_intake_request", "guard_intake_integrity",
        "guard_intake_created_log", "guard_intake_truncate", "guard_managed_item", "intake_source_key",
        "guard_target_object", "guard_target_attempt", "guard_target_truncate",
        "guard_reserved_target_input", "reserved_target_path",
        "guard_source_return", "guard_return_object", "guard_return_attempt", "guard_return_integrity",
        "guard_return_truncate", "guard_return_writer", "return_payload_digest",
        "guard_source_handoff", "handoff_encode", "handoff_payload", "handoff_sort_key",
        "guard_legacy_return", "guard_legacy_return_object", "guard_legacy_return_attempt",
        "guard_legacy_return_integrity", "guard_legacy_return_truncate", "guard_legacy_return_writer",
        "guard_layout_immutable", "guard_layout_truncate"
    ];

    public static readonly string Up = string.Join("\n",
    [
        // Fail closed: lock every retired table, then refuse to drop one that holds a row.
        "LOCK TABLE " + string.Join(", ", Tables.Select(table => "public." + table)) + " IN ACCESS EXCLUSIVE MODE;",
        "DO $$ DECLARE retired text; populated boolean; BEGIN",
        "  FOREACH retired IN ARRAY ARRAY['" + string.Join("','", Tables) + "'] LOOP",
        "    EXECUTE format('SELECT EXISTS(SELECT 1 FROM public.%I)', retired) INTO populated;",
        "    IF populated THEN RAISE EXCEPTION 'retired_source_table_not_empty: %', retired; END IF;",
        "  END LOOP;",
        "END $$;",
        .. CoreTriggers.Select(entry => $"DROP TRIGGER {entry.Trigger} ON public.{entry.Table};"),
        "DROP TABLE " + string.Join(", ", Tables.Select(table => "public." + table)) + ";",
        // No CASCADE: a remaining dependent object fails the migration instead of being dropped silently.
        .. Functions.Select(function => $"DROP FUNCTION public.{function};")
    ]);

    public const string Down = """
        DO $$ BEGIN RAISE EXCEPTION 'retired_source_schema_irreversible'; END $$;
        """;
}
