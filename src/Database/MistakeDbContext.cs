using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;
using Emenda.Database.Entities;

namespace Emenda.Database;

public class MistakeDbContext : DbContext
{
    public MistakeDbContext(DbContextOptions<MistakeDbContext> options) : base(options)
    {
    }

    // Null is reserved for trusted background jobs; HTTP requests set an explicit caller scope.
    public string? ConversationCallerScope { get; set; }

    public DbSet<MistakeItemEntity> MistakeItems { get; set; } = null!;
    public DbSet<MistakePracticeEntity> MistakePractices { get; set; } = null!;
    public DbSet<ProcessLogEntity> ProcessLogs { get; set; } = null!;
    public DbSet<ConversationCollectionEntity> ConversationCollections => Set<ConversationCollectionEntity>();
    public DbSet<ConversationMistakeReceiptEntity> ConversationMistakeReceipts => Set<ConversationMistakeReceiptEntity>();
    public DbSet<MistakeAnalysisEntity> MistakeAnalyses => Set<MistakeAnalysisEntity>();
    public DbSet<ReanalyzeJobEntity> ReanalyzeJobs { get; set; } = null!;
    public DbSet<ProductIntakeRequestEntity> ProductIntakeRequests => Set<ProductIntakeRequestEntity>();
    public DbSet<ExtractionJobEntity> ExtractionJobs => Set<ExtractionJobEntity>();
    public DbSet<ExtractionQuestionEntity> ExtractionQuestions => Set<ExtractionQuestionEntity>();
    public DbSet<ExtractionRevisionEntity> ExtractionRevisions => Set<ExtractionRevisionEntity>();
    public DbSet<ExtractionObjectEntity> ExtractionObjects => Set<ExtractionObjectEntity>();
    public DbSet<ExtractionProposalEntity> ExtractionProposals => Set<ExtractionProposalEntity>();
    public DbSet<ExtractionSupplementEntity> ExtractionSupplements => Set<ExtractionSupplementEntity>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ProductIntakeSchema.Configure(modelBuilder);
        MistakeAnalysisSchema.Configure(modelBuilder);
        ConversationCollectionModel.Configure(modelBuilder);
        modelBuilder.Entity<MistakeItemEntity>().Property(e => e.ConversationActive).HasDefaultValue(false);
        modelBuilder.Entity<MistakeItemEntity>().HasQueryFilter(e => ConversationCallerScope == null
            || e.ConversationSource == null || e.ConversationCaller == ConversationCallerScope);
        modelBuilder.Entity<MistakePracticeEntity>().HasQueryFilter(e => ConversationCallerScope == null
            || e.MistakeItem.ConversationSource == null || e.MistakeItem.ConversationCaller == ConversationCallerScope);
        modelBuilder.Entity<ProcessLogEntity>().HasQueryFilter(e => ConversationCallerScope == null
            || e.MistakeItem.ConversationSource == null || e.MistakeItem.ConversationCaller == ConversationCallerScope);
        modelBuilder.Entity<MistakeAnalysisEntity>().HasQueryFilter(e => ConversationCallerScope == null
            || MistakeItems.Any(item => item.Id == e.MistakeItemId));
        ExtractionModel.Configure(modelBuilder);

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
