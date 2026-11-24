using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_job")]
public sealed class ExtractionJobEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("caller"), Required] public string Caller { get; set; } = "";
    [Column("source_key"), Required, MaxLength(64)] public string SourceKey { get; set; } = "";
    [Column("payload_hash"), Required, MaxLength(64)] public string PayloadHash { get; set; } = "";
    [Column("student_id"), Required, MaxLength(36)] public string StudentId { get; set; } = "";
    [Column("grade")] public int? Grade { get; set; }
    [Column("content_hash"), MaxLength(64)] public string? ContentHash { get; set; }
    [Column("status"), Required] public string Status { get; set; } = "Staging";
    [Column("version")] public int Version { get; set; }
    [Column("sources_json", TypeName = "jsonb"), Required] public string SourcesJson { get; set; } = "[]";
    [Column("layout_json", TypeName = "jsonb"), Required] public string LayoutJson { get; set; } = "[]";
    [Column("candidate_json", TypeName = "jsonb")] public string? CandidateJson { get; set; }
    [Column("error_code")] public string? ErrorCode { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [Column("owner_id")] public Guid? OwnerId { get; set; }
    [Column("fence")] public long Fence { get; set; }
    [Column("lease_until")] public DateTimeOffset? LeaseUntil { get; set; }
    [Column("attempt")] public int Attempt { get; set; }
    [Column("last_retry_key")] public Guid? LastRetryKey { get; set; }
    [Column("retry_keys_json", TypeName = "jsonb"), Required] public string RetryKeysJson { get; set; } = "[]";
}
