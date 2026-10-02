using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
namespace Ruoyu.Study.Mistake.Database;

internal static class SourceTargetModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SourceTargetObjectEntity>(e =>
        {
            e.HasKey(x => new {x.SourceUploadId,x.ContentRevision,x.SourcePath});
            e.Property(x => x.StudentId).HasMaxLength(36);
            e.Property(x => x.ExpectedSha256).HasMaxLength(64);
            e.HasOne<SourceIntakeEntity>().WithMany().HasForeignKey(x => new {x.SourceUploadId,x.ContentRevision}).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_target_object_intake");
            e.HasOne<SourceTargetAttemptEntity>().WithMany().HasForeignKey(x => x.SelectedAttemptId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_target_object_selected");
        });
        model.Entity<SourceTargetAttemptEntity>(e =>
        {
            e.HasKey(x => x.AttemptId);e.Property(x => x.AttemptId).ValueGeneratedNever();
            e.Property(x => x.VerifiedSha256).HasMaxLength(64);
            e.HasIndex(x=>new {x.SourceUploadId,x.ContentRevision,x.SourcePath}).HasDatabaseName("IX_source_target_attempt_object");
            e.HasIndex(x => x.TargetKey).IsUnique().HasDatabaseName("IX_source_target_attempt_key");
            e.HasOne<SourceTargetObjectEntity>().WithMany().HasForeignKey(x => new {x.SourceUploadId,x.ContentRevision,x.SourcePath}).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_target_attempt_object");
        });
    }
}
