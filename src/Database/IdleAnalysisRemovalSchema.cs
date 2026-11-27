namespace Emenda.Database;

/// <summary>
/// Schema v27: drops <c>mistake_analysis</c> and <c>reanalyze_job.analysis_input</c> (schema v17). The structured VL
/// analysis that used them was retired with the standalone product (schema v19), so they have been empty and unused
/// since. The upgrade refuses to run while either holds data, so nothing is lost.
/// </summary>
public static class IdleAnalysisRemovalSchema
{
    public const string Migration = "20261129000000_DropIdleMistakeAnalysis";

    public const string Up = """
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM mistake_analysis) OR EXISTS (SELECT 1 FROM reanalyze_job WHERE analysis_input IS NOT NULL) THEN
                RAISE EXCEPTION 'idle_mistake_analysis_present';
            END IF;
        END $$;
        DROP TABLE mistake_analysis;
        ALTER TABLE reanalyze_job DROP COLUMN analysis_input;
        """;

    // Forward-only like v20 and v22-v26. EF runs each migration in its own transaction, so a reversible step here
    // would let a downgrade attempt change the database before v26 refuses it; refusing first changes nothing.
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'idle_mistake_analysis_irreversible'; END $$;";
}
