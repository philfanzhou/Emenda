using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Ruoyu.Study.Mistake.Database.Entity;

namespace Ruoyu.Study.Mistake.Database;

public class MistakeDbContext : DbContext
{
    public MistakeDbContext(DbContextOptions<MistakeDbContext> options) : base(options)
    {
    }

    public DbSet<MistakeEntity> Mistakes { get; set; } = null!;
    public DbSet<MistakePracticeEntity> MistakePractices { get; set; } = null!;
    public DbSet<ProcessLogEntity> ProcessLogs { get; set; } = null!;

    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mistake 配置
        modelBuilder.Entity<MistakeEntity>(entity =>
        {
            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => new { e.StudentId, e.Subject, e.Grade, e.Status });
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Date);
            entity.HasIndex(e => e.CreatedAt);

            // EF Core 8.0 optimized JSON conversion for ImagePaths
            var imagePathsConverter = new ValueConverter<List<string>, string>(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : (JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>()));

            var imagePathsComparer = new ValueComparer<List<string>>(
                (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.ImagePaths)
                .HasConversion(imagePathsConverter)
                .Metadata.SetValueComparer(imagePathsComparer);
        });

        // MistakePractice 配置
        modelBuilder.Entity<MistakePracticeEntity>(entity =>
        {
            entity.HasIndex(e => e.MistakeId);
            entity.HasIndex(e => e.StudentId);
            entity.HasIndex(e => e.PracticeDate);

            entity.HasOne(e => e.Mistake)
                  .WithMany()
                  .HasForeignKey(e => e.MistakeId)
                  .OnDelete(DeleteBehavior.Cascade);

            // EF Core 8.0 optimized JSON conversion for ImagePaths
            var imagePathsConverter = new ValueConverter<List<string>, string>(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : (JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>()));

            var imagePathsComparer = new ValueComparer<List<string>>(
                (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.ImagePaths)
                .HasConversion(imagePathsConverter)
                .Metadata.SetValueComparer(imagePathsComparer);
        });

        // ProcessLog 配置
        modelBuilder.Entity<ProcessLogEntity>(entity =>
        {
            entity.HasIndex(e => new { e.MistakeId, e.CreatedAt });
            entity.HasIndex(e => e.CreatedAt);

            entity.HasOne(e => e.Mistake)
                  .WithMany()
                  .HasForeignKey(e => e.MistakeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
