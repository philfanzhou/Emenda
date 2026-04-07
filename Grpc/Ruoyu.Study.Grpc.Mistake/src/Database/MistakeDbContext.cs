using Microsoft.EntityFrameworkCore;
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
