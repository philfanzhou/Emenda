using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Database;

public static class MistakeAnalysisSchema
{
    public const string Migration = "20261117000000_AddMistakeAnalysis";
    public const string Up = """
        CREATE TABLE mistake_analysis (
            mistake_item_id uuid NOT NULL,
            revision bigint NOT NULL,
            draft_report jsonb,
            confirmed_report jsonb,
            teacher_feedback jsonb NOT NULL,
            draft_job_id uuid,
            confirmed_job_id uuid,
            input_digest text,
            generated_at timestamp with time zone,
            model text,
            prompt_version text,
            confirmed_by text,
            confirmed_at timestamp with time zone,
            CONSTRAINT "PK_mistake_analysis" PRIMARY KEY (mistake_item_id),
            CONSTRAINT "FK_mistake_analysis_mistake_item" FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE
        );
        ALTER TABLE reanalyze_job ADD COLUMN analysis_input jsonb;
        """;
    public const string Down = """
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM mistake_analysis) THEN
                RAISE EXCEPTION 'mistake_analysis_present';
            END IF;
        END $$;
        ALTER TABLE reanalyze_job DROP COLUMN analysis_input;
        DROP TABLE mistake_analysis;
        """;
    internal static readonly string[] Columns =
    [
        "mistake_analysis|mistake_item_id|uuid|N",
        "mistake_analysis|revision|bigint|N",
        "mistake_analysis|draft_report|jsonb|Y",
        "mistake_analysis|confirmed_report|jsonb|Y",
        "mistake_analysis|teacher_feedback|jsonb|N",
        "mistake_analysis|draft_job_id|uuid|Y",
        "mistake_analysis|confirmed_job_id|uuid|Y",
        "mistake_analysis|input_digest|text|Y",
        "mistake_analysis|generated_at|timestamp with time zone|Y",
        "mistake_analysis|model|text|Y",
        "mistake_analysis|prompt_version|text|Y",
        "mistake_analysis|confirmed_by|text|Y",
        "mistake_analysis|confirmed_at|timestamp with time zone|Y",
        "reanalyze_job|analysis_input|jsonb|Y"
    ];
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MistakeAnalysisEntity>(entity =>
        {
            entity.Property(e => e.MistakeItemId).ValueGeneratedNever();
            entity.HasOne<MistakeItemEntity>().WithOne().HasForeignKey<MistakeAnalysisEntity>(e => e.MistakeItemId)
                .HasConstraintName("FK_mistake_analysis_mistake_item").OnDelete(DeleteBehavior.Cascade);
        });
    }
}
