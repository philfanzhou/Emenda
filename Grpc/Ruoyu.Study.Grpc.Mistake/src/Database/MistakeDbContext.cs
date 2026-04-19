using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Ruoyu.Study.Mistake.Database.Entities;

namespace Ruoyu.Study.Mistake.Database;

public class MistakeDbContext : DbContext
{
    public MistakeDbContext(DbContextOptions<MistakeDbContext> options) : base(options)
    {
    }

    public DbSet<MistakeEntity> Mistakes { get; set; } = null!;
    public DbSet<MistakeDetailEntity> MistakeDetails { get; set; } = null!;
    public DbSet<MistakePracticeEntity> MistakePractices { get; set; } = null!;
    public DbSet<ProcessLogEntity> ProcessLogs { get; set; } = null!;

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

            entity.HasOne<MistakeDetailEntity>()
                  .WithOne(d => d.Mistake)
                  .HasForeignKey<MistakeDetailEntity>(d => d.MistakeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // MistakeDetail 配置
        modelBuilder.Entity<MistakeDetailEntity>(entity =>
        {
            entity.HasIndex(e => e.MistakeId);

            var imagePathsComparer = new ValueComparer<List<string>>(
                (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.OriginalImages)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
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

            // Configure ImagePaths as JSON string for cross-database compatibility
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
