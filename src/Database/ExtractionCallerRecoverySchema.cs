namespace Emenda.Database;

/// <summary>
/// Schema v25: resolves <c>source_caller</c> of extraction mistakes whose task was deleted before v24.
/// <para>
/// A deletion receipt stores <c>owner_hash</c> as the SHA-256 of the JSON string the application serializes
/// (<c>ExtractionRules.Hash(job.Caller)</c>), i.e. of <c>"caller:name"</c> including the quotes. v24 hashed the
/// bare text instead, so no receipt matched and those rows kept a NULL caller. Visibility was never affected: the
/// caller scope still matches the receipt directly. This migration redoes the lookup with the stored form.
/// </para>
/// <para>
/// System.Text.Json's default encoder escapes non-ASCII characters, the backslash and <c>" &amp; ' + &lt; &gt; `</c>,
/// which plain SQL concatenation does not reproduce. Only names without those characters are
/// compared, so a name the database cannot hash exactly never matches the wrong receipt; such a row keeps NULL.
/// </para>
/// </summary>
public static class ExtractionCallerRecoverySchema
{
    public const string Migration = "20261127000000_ExtractionCallerRecovery";

    // The derivation trigger keeps NEW.source_caller when the task no longer exists, so the recovered value stays.
    public const string Up = """
        UPDATE mistake_item m SET source_caller=n.name
        FROM extraction_deletion d, (
            SELECT substr(caller, 8) AS name FROM extraction_job WHERE caller LIKE 'caller:%'
            UNION SELECT conversation_caller FROM mistake_item WHERE conversation_caller IS NOT NULL
            UNION SELECT source_caller FROM mistake_item WHERE source_caller IS NOT NULL
            UNION SELECT caller_name FROM conversation_collection_job) n
        WHERE m.source_kind='extraction' AND m.source_caller IS NULL
          AND m.source_upload_id='extract:' || replace(d.job_id::text, '-', '')
          AND n.name ~ '^[!-~]+$' AND n.name !~ '["&''+<>\\`]'
          AND d.owner_hash=encode(sha256(convert_to('"caller:' || n.name || '"', 'UTF8')), 'hex');
        """;

    // Forward-only like v24: a downgrade attempt fails before it changes anything. The recovered callers are
    // ordinary derived values, so a manual rollback only removes the history row.
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'extraction_caller_recovery_irreversible'; END $$;";
}
