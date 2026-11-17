using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Database;

internal static class ExtractionModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtractionJobEntity>(b =>
        {
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Version).HasDefaultValue(0);
            b.Property(e => e.Fence).HasDefaultValue(0L);
            b.Property(e => e.Attempt).HasDefaultValue(0);
            b.Property(e => e.SourcesJson).HasDefaultValueSql("'[]'::jsonb");
            b.Property(e => e.LayoutJson).HasDefaultValueSql("'[]'::jsonb");
            b.Property(e => e.RetryKeysJson).HasDefaultValueSql("'[]'::jsonb");
            b.HasIndex(e => new { e.Caller, e.SourceKey }).IsUnique().HasDatabaseName("IX_extraction_job_caller_source_key");
        });
        modelBuilder.Entity<ExtractionQuestionEntity>(b =>
        {
            b.Property(e => e.Id).ValueGeneratedNever();
            b.HasIndex(e => e.JobId).HasDatabaseName("IX_extraction_question_job_id");
            b.HasOne<ExtractionJobEntity>().WithMany().HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_extraction_question_job");
        });
        modelBuilder.Entity<ExtractionRevisionEntity>(b =>
        {
            b.HasKey(e => new { e.JobId, e.Version });
            b.HasIndex(e => new { e.JobId, e.RequestKey }).IsUnique().HasDatabaseName("IX_extraction_revision_job_request_key");
            b.HasOne<ExtractionJobEntity>().WithMany().HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_extraction_revision_job");
        });
    }
}
