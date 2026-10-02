using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
namespace Ruoyu.Study.Mistake.Database;

internal static class SourceReturnModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SourceReturnEntity>(e =>
        {
            e.HasKey(x => x.ItemId); e.Property(x => x.ItemId).ValueGeneratedNever();
            e.HasIndex(x => x.OperationId).IsUnique().HasDatabaseName("IX_source_return_operation");
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision }).HasDatabaseName("IX_source_return_SourceUploadId_ContentRevision");
            e.HasIndex(x => x.FinalLogId).HasDatabaseName("IX_source_return_FinalLogId");
            e.HasIndex(x => x.Phase).HasDatabaseName("IX_source_return_phase");
            e.HasOne<SourceIntakeGroupEntity>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_return_group");
            e.HasOne<SourceIntakeEntity>().WithMany().HasForeignKey(x => new { x.SourceUploadId, x.ContentRevision }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_return_intake");
            e.HasOne<ProcessLogEntity>().WithMany().HasForeignKey(x => x.FinalLogId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_return_log");
        });
        model.Entity<SourceReturnObjectEntity>(e =>
        {
            e.HasKey(x => new { x.ItemId, x.Position });
            e.HasIndex(x => x.SelectedAttemptId).HasDatabaseName("IX_source_return_object_SelectedAttemptId");
            e.HasOne<SourceReturnEntity>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_return_object_return");
            e.HasOne<SourceReturnAttemptEntity>().WithMany().HasForeignKey(x => x.SelectedAttemptId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_return_object_selected");
        });
        model.Entity<SourceReturnAttemptEntity>(e =>
        {
            e.HasKey(x => x.AttemptId); e.Property(x => x.AttemptId).ValueGeneratedNever();
            e.HasIndex(x => x.ObjectKey).IsUnique().HasDatabaseName("IX_source_return_attempt_key");
            e.HasIndex(x => new { x.ItemId, x.Position }).HasDatabaseName("IX_source_return_attempt_object");
            e.HasOne<SourceReturnObjectEntity>().WithMany().HasForeignKey(x => new { x.ItemId, x.Position }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_return_attempt_object");
        });
    }
}
