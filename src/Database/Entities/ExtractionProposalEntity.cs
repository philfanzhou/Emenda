using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_proposal")]
public sealed class ExtractionProposalEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("job_id")] public Guid JobId { get; set; }
    [Column("request_key")] public Guid RequestKey { get; set; }
    [Column("payload_hash")] public string PayloadHash { get; set; } = "";
    [Column("expected_version")] public int ExpectedVersion { get; set; }
    [Column("status")] public string Status { get; set; } = "Generating";
    [Column("request_json", TypeName = "jsonb")] public string? RequestJson { get; set; }
    [Column("result_json", TypeName = "jsonb")] public string? ResultJson { get; set; }
    [Column("owner")] public Guid? Owner { get; set; }
    [Column("lease_until")] public DateTimeOffset? LeaseUntil { get; set; }
    [Column("attempts")] public int Attempts { get; set; }
    [Column("error_code")] public string? ErrorCode { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
