using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("conversation_collection_job")]
public sealed class ConversationCollectionEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("caller_name")] public string CallerName { get; set; } = "";
    [Column("actor_id")] public string ActorId { get; set; } = "";
    [Column("student_id")] public string StudentId { get; set; } = "";
    [Column("owner_id")] public string OwnerId { get; set; } = "";
    [Column("grade")] public int Grade { get; set; }
    [Column("status")] public string Status { get; set; } = "pending";
    [Column("attempt")] public int Attempt { get; set; }
    [Column("lease_id")] public Guid? LeaseId { get; set; }
    [Column("lease_until")] public DateTimeOffset? LeaseUntil { get; set; }
    [Column("next_attempt_at")] public DateTimeOffset NextAttemptAt { get; set; }
    [Column("staged_regions", TypeName = "jsonb")] public string StagedRegions { get; set; } = "[]";
    [Column("error_code")] public string? ErrorCode { get; set; }
    [Column("result", TypeName = "jsonb")] public string? Result { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [Column("updated_at"), ConcurrencyCheck] public DateTimeOffset UpdatedAt { get; set; }
}

[Table("conversation_mistake_receipt")]
public sealed class ConversationMistakeReceiptEntity
{
    [Key, Column("event_id")] public string EventId { get; set; } = "";
    [Column("source_key")] public string SourceKey { get; set; } = "";
    [Column("student_id")] public string StudentId { get; set; } = "";
    [Column("item_id")] public Guid ItemId { get; set; }
    [Column("position")] public int Position { get; set; }
    [Column("content_hash")] public string ContentHash { get; set; } = "";
    [Column("observation", TypeName = "jsonb")] public string Observation { get; set; } = "{}";
    [Column("status")] public string Status { get; set; } = "";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
