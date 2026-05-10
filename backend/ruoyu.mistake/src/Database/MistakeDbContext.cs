using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;
using Ruoyu.Study.Mistake.Database.Entities;

namespace Ruoyu.Study.Mistake.Database;

public class MistakeDbContext : DbContext
{
    public MistakeDbContext(DbContextOptions<MistakeDbContext> options) : base(options)
    {
    }

    public DbSet<MistakeItemEntity> MistakeItems { get; set; } = null!;
    public DbSet<MistakePracticeEntity> MistakePractices { get; set; } = null!;
    public DbSet<ProcessLogEntity> ProcessLogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MistakeItemEntity>(entity =>
        {
            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => new { e.StudentId, e.Subject, e.Grade, e.ReviewStatus });
            entity.HasIndex(e => e.SourceUploadId);
            entity.HasIndex(e => e.ReviewStatus);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<MistakePracticeEntity>(entity =>
        {
            entity.HasIndex(e => e.MistakeItemId);
            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => e.PracticeDate);

            entity.HasOne(e => e.MistakeItem)
                  .WithMany()
                  .HasForeignKey(e => e.MistakeItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            var imagePathsComparer = new ValueComparer<List<string>>(
                (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.ImagePaths)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .Metadata.SetValueComparer(imagePathsComparer);
        });

        modelBuilder.Entity<ProcessLogEntity>(entity =>
        {
            entity.HasIndex(e => new { e.MistakeItemId, e.CreatedAt });
            entity.HasIndex(e => e.CreatedAt);

            entity.HasOne(e => e.MistakeItem)
                  .WithMany()
                  .HasForeignKey(e => e.MistakeItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
