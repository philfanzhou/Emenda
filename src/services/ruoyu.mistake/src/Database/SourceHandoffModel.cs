using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;
namespace Ruoyu.Study.Mistake.Database;

internal static class SourceHandoffModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SourceHandoffEntity>(e =>
        {
            e.HasKey(x => new { x.SourceUploadId,x.ContentRevision });
            e.HasIndex(x => x.SnapshotId).IsUnique().HasDatabaseName("IX_source_handoff_snapshot");
            e.HasIndex(x => x.OperationId).IsUnique().HasDatabaseName("IX_source_handoff_operation");
            e.HasOne<SourceIntakeEntity>().WithMany().HasForeignKey(x => new { x.SourceUploadId,x.ContentRevision })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_source_handoff_intake");
        });
    }
}
