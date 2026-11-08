using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
namespace Ruoyu.Study.Mistake.Database;

internal static class LegacyReturnModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<LegacyReturnEntity>(e =>
        {
            e.HasKey(x => x.ItemId); e.Property(x => x.ItemId).ValueGeneratedNever();
            e.HasIndex(x => x.OperationId).IsUnique().HasDatabaseName("IX_legacy_return_operation");
            e.HasIndex(x => x.FinalLogId).HasDatabaseName("IX_legacy_return_FinalLogId");
            e.HasIndex(x => x.Phase).HasDatabaseName("IX_legacy_return_phase");
            e.HasOne<MistakeItemEntity>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_legacy_return_item");
            e.HasOne<ProcessLogEntity>().WithMany().HasForeignKey(x => x.FinalLogId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_legacy_return_log");
        });
        model.Entity<LegacyReturnObjectEntity>(e =>
        {
            e.HasKey(x => new { x.ItemId, x.Position });
            e.HasIndex(x => x.SelectedAttemptId).HasDatabaseName("IX_legacy_return_object_SelectedAttemptId");
            e.HasOne<LegacyReturnEntity>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_legacy_return_object_return");
            e.HasOne<LegacyReturnAttemptEntity>().WithMany().HasForeignKey(x => x.SelectedAttemptId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_legacy_return_object_selected");
        });
        model.Entity<LegacyReturnAttemptEntity>(e =>
        {
            e.HasKey(x => x.AttemptId); e.Property(x => x.AttemptId).ValueGeneratedNever();
            e.HasIndex(x => x.ObjectKey).IsUnique().HasDatabaseName("IX_legacy_return_attempt_key");
            e.HasIndex(x => new { x.ItemId, x.Position }).HasDatabaseName("IX_legacy_return_attempt_object");
            e.HasOne<LegacyReturnObjectEntity>().WithMany().HasForeignKey(x => new { x.ItemId, x.Position }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_legacy_return_attempt_object");
        });
    }
}
