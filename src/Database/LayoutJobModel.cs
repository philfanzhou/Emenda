using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database.Entities;

namespace Ruoyu.Study.Mistake.Database;

internal static class LayoutJobModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<LayoutJobEntity>(e =>
        {
            e.HasKey(x => x.JobId);
            e.Property(x => x.JobId).ValueGeneratedNever();
            e.Property(x => x.ImageRotations).HasColumnType("jsonb");
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision, x.ResultRevision }).IsUnique().HasDatabaseName("IX_layout_job_revision");
            e.HasIndex(x => x.Status).HasDatabaseName("IX_layout_job_status");
            e.HasOne<SourceIntakeEntity>().WithMany().HasForeignKey(x => new { x.SourceUploadId, x.ContentRevision })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_layout_job_source_intake");
        });
        model.Entity<LayoutJobImageEntity>(e =>
        {
            e.HasKey(x => new { x.JobId, x.ImagePath });
            e.HasIndex(x => new { x.JobId, x.Position }).HasDatabaseName("IX_layout_job_image_position");
            e.HasOne<LayoutJobEntity>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_layout_job_image_layout_job");
        });
        model.Entity<LayoutResultEntity>(e =>
        {
            e.HasKey(x => x.ResultId);
            e.Property(x => x.ResultId).ValueGeneratedNever();
            e.Property(x => x.Diagnostics).HasColumnType("jsonb");
            e.HasIndex(x => new { x.JobId, x.ImagePath }).IsUnique().HasDatabaseName("IX_layout_result_job_image");
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision, x.ResultRevision, x.ImagePath }).IsUnique()
                .HasDatabaseName("IX_layout_result_revision_image");
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision, x.ResultRevision }).HasDatabaseName("IX_layout_result_revision");
            e.HasOne<LayoutJobEntity>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_layout_result_layout_job");
        });
        model.Entity<LayoutResultRegionEntity>(e =>
        {
            e.HasKey(x => new { x.ResultId, x.RegionId });
            e.HasOne<LayoutResultEntity>().WithMany().HasForeignKey(x => x.ResultId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_layout_result_region_layout_result");
        });
        model.Entity<LayoutRequestEntity>(e =>
        {
            e.HasKey(x => new { x.Actor, x.Action, x.RequestKey });
            e.HasIndex(x => new { x.SourceUploadId, x.ContentRevision }).HasDatabaseName("IX_layout_request_source_revision");
            e.HasIndex(x => x.JobId).HasDatabaseName("IX_layout_request_job");
            e.HasOne<LayoutJobEntity>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_layout_request_layout_job");
        });
    }
}
