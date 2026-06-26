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
    public DbSet<ReanalyzeJobEntity> ReanalyzeJobs { get; set; } = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var sourceRegionComparer = new ValueComparer<List<SourceRegionEntry>>(
            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.SourceImagePath.GetHashCode(), v.BoundingBox != null ? HashCode.Combine(v.BoundingBox.X1, v.BoundingBox.Y1, v.BoundingBox.X2, v.BoundingBox.Y2) : 0)),
            c => JsonSerializer.Deserialize<List<SourceRegionEntry>>(JsonSerializer.Serialize(c, JsonOptions), JsonOptions) ?? new());

        modelBuilder.Entity<MistakeItemEntity>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => new { e.StudentId, e.Subject, e.Grade, e.ReviewStatus });
            entity.HasIndex(e => e.SourceUploadId);
            entity.HasIndex(e => e.ReviewStatus);
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.SourceRegions)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<SourceRegionEntry>>(v, JsonOptions) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(sourceRegionComparer);
        });

        var imagePathsComparer = new ValueComparer<List<string>>(
            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList());

        modelBuilder.Entity<MistakePracticeEntity>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasIndex(e => e.MistakeItemId);
            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => e.PracticeDate);

            entity.HasOne(e => e.MistakeItem)
                  .WithMany()
                  .HasForeignKey(e => e.MistakeItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.ImagePaths)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(imagePathsComparer);
        });

        modelBuilder.Entity<ProcessLogEntity>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasIndex(e => new { e.MistakeItemId, e.CreatedAt });
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.StudentId);

            entity.HasOne(e => e.MistakeItem)
                  .WithMany()
                  .HasForeignKey(e => e.MistakeItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReanalyzeJobEntity>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ReviewerId, e.Status });
            entity.HasIndex(e => e.CompletedAt);
        });
    }
}
