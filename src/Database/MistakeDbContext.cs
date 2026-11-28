using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Security.Cryptography;
using System.Text.Json;
using Emenda.Database.Entities;

namespace Emenda.Database;

public class MistakeDbContext : DbContext
{
    public MistakeDbContext(DbContextOptions<MistakeDbContext> options) : base(options)
    {
    }

    // Null is reserved for trusted background jobs; HTTP requests set an explicit caller scope.
    private string? _conversationCallerScope;
    public string? ConversationCallerScope
    {
        get => _conversationCallerScope;
        set
        {
            _conversationCallerScope = value;
            ExtractionCallerOwnerHash = value == null ? null : Convert.ToHexStringLower(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes("caller:" + value, JsonOptions)));
        }
    }

    private string? ExtractionCallerOwnerHash { get; set; }

    public DbSet<MistakeItemEntity> MistakeItems { get; set; } = null!;
    public DbSet<ProcessLogEntity> ProcessLogs { get; set; } = null!;
    public DbSet<ConversationCollectionEntity> ConversationCollections => Set<ConversationCollectionEntity>();
    public DbSet<ConversationMistakeReceiptEntity> ConversationMistakeReceipts => Set<ConversationMistakeReceiptEntity>();
    public DbSet<ExtractionJobEntity> ExtractionJobs => Set<ExtractionJobEntity>();
    public DbSet<ExtractionDeletionEntity> ExtractionDeletions => Set<ExtractionDeletionEntity>();
    public DbSet<ExtractionQuestionEntity> ExtractionQuestions => Set<ExtractionQuestionEntity>();
    public DbSet<ExtractionRevisionEntity> ExtractionRevisions => Set<ExtractionRevisionEntity>();
    public DbSet<ExtractionObjectEntity> ExtractionObjects => Set<ExtractionObjectEntity>();
    public DbSet<ExtractionProposalEntity> ExtractionProposals => Set<ExtractionProposalEntity>();
    public DbSet<ExtractionSupplementEntity> ExtractionSupplements => Set<ExtractionSupplementEntity>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Every EF write of a mistake goes through MistakeMaterializer, which derives the unified source columns.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        MistakeMaterializer.Materialize(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await MistakeMaterializer.MaterializeAsync(this, cancellationToken).ConfigureAwait(false);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConversationCollectionModel.Configure(modelBuilder);
        modelBuilder.Entity<MistakeItemEntity>().Property(e => e.ConversationActive).HasDefaultValue(false);
        // Legacy rows belong to no caller. Every other row is owned by its source_caller; an extraction whose task was
        // deleted before its caller could be resolved (source_caller NULL) still matches its deletion receipt.
        modelBuilder.Entity<MistakeItemEntity>().HasQueryFilter(e => ConversationCallerScope == null
            || e.SourceKind == MistakeSource.Legacy
            || e.SourceCaller == ConversationCallerScope
            || e.SourceKind == MistakeSource.Extraction && e.SourceCaller == null
                && ExtractionDeletions.Any(deletion => e.SourceUploadId == "extract:" + deletion.JobId.ToString().Replace("-", "")
                    && deletion.OwnerHash == ExtractionCallerOwnerHash));
        modelBuilder.Entity<ProcessLogEntity>().HasQueryFilter(e => ConversationCallerScope == null
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
    }
}
