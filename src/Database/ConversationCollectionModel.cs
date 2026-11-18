using Microsoft.EntityFrameworkCore;
using Emenda.Database.Entities;

namespace Emenda.Database;

internal static class ConversationCollectionModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<ConversationCollectionEntity>(b =>
        {
            b.ToTable("conversation_collection_job"); b.HasKey(e => e.Id).HasName("PK_conversation_collection_job");
            b.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(e => e.CallerName).HasColumnName("caller_name").HasColumnType("text").IsRequired();
            b.Property(e => e.ActorId).HasColumnName("actor_id").HasColumnType("text").IsRequired();
            b.Property(e => e.StudentId).HasColumnName("student_id").HasColumnType("text").IsRequired();
            b.Property(e => e.OwnerId).HasColumnName("owner_id").HasColumnType("text").IsRequired();
            b.Property(e => e.Grade).HasColumnName("grade").HasColumnType("integer");
            b.Property(e => e.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(e => e.Attempt).HasColumnName("attempt").HasColumnType("integer");
            b.Property(e => e.LeaseId).HasColumnName("lease_id").HasColumnType("uuid");
            b.Property(e => e.LeaseUntil).HasColumnName("lease_until").HasColumnType("timestamp with time zone");
            b.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
            b.Property(e => e.StagedRegions).HasColumnName("staged_regions").HasColumnType("jsonb").IsRequired();
            b.Property(e => e.ErrorCode).HasColumnName("error_code").HasColumnType("text");
            b.Property(e => e.Result).HasColumnName("result").HasColumnType("jsonb");
            b.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsConcurrencyToken();
            b.HasIndex(e => new { e.Status, e.NextAttemptAt }).HasDatabaseName("IX_conversation_collection_status");
        });
        model.Entity<ConversationMistakeReceiptEntity>(b =>
        {
            b.ToTable("conversation_mistake_receipt"); b.HasKey(e => e.EventId).HasName("PK_conversation_mistake_receipt");
            b.Property(e => e.EventId).HasColumnName("event_id").HasColumnType("text").IsRequired();
            b.Property(e => e.SourceKey).HasColumnName("source_key").HasColumnType("text").IsRequired();
            b.Property(e => e.StudentId).HasColumnName("student_id").HasColumnType("text").IsRequired();
            b.Property(e => e.ItemId).HasColumnName("item_id").HasColumnType("uuid");
            b.Property(e => e.Position).HasColumnName("position").HasColumnType("integer");
            b.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("text").IsRequired();
            b.Property(e => e.Observation).HasColumnName("observation").HasColumnType("jsonb").IsRequired();
            b.Property(e => e.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            b.HasIndex(e => e.SourceKey).HasDatabaseName("IX_conversation_mistake_source");
        });
    }
}
