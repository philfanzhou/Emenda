using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
namespace Ruoyu.Study.Mistake.Database;

internal static class SourceIntakeModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceIntakeEntity>(e =>
        {
            e.HasKey(x => new { x.SourceUploadId, x.ContentRevision });
            e.Property(x => x.StudentId).HasMaxLength(36);
        });
        modelBuilder.Entity<SourceIntakeGroupEntity>(e =>
        {
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.GroupHash).HasMaxLength(64);
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision, x.GroupHash }).HasDatabaseName("IX_source_intake_group_source_hash");
            e.HasOne<SourceIntakeEntity>().WithMany().HasForeignKey(x => new { x.SourceUploadId, x.ContentRevision }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_intake_group_source_intake");
            e.HasOne<MistakeItemEntity>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_intake_group_item");
            e.HasOne<ProcessLogEntity>().WithMany().HasForeignKey(x => x.CreatedLogId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_intake_group_created_log");
            // The SQL migration deliberately uses only the source/hash lookup index; FKs need no EF-generated index.
            e.HasIndex(x => x.CreatedLogId).HasDatabaseName("IX_source_intake_group_created_log_id");
        });
        modelBuilder.Entity<SourceIntakeRequestEntity>(e =>
        {
            e.HasKey(x => new { x.Actor, x.Action, x.RequestKey });
            e.HasOne<SourceIntakeEntity>().WithMany().HasForeignKey(x => new { x.SourceUploadId, x.ContentRevision }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_intake_request_source_intake");
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision }).HasDatabaseName("IX_source_intake_request_source_revision");
        });
    }
}
