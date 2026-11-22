using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("extraction_supplement")]
public sealed class ExtractionSupplementEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("job_id")] public Guid JobId { get; set; }
    [Column("question_id")] public Guid QuestionId { get; set; }
    [Column("item_id")] public Guid ItemId { get; set; }
    [Column("request_key")] public Guid RequestKey { get; set; }
    [Column("payload_hash")] public string PayloadHash { get; set; } = "";
    [Column("expected_version")] public int ExpectedVersion { get; set; }
    [Column("accepted_version")] public int? AcceptedVersion { get; set; }
    [Column("parent_id")] public Guid? ParentId { get; set; }
    [Column("status")] public string Status { get; set; } = "Requested";
    [Column("note")] public string Note { get; set; } = "";
    [Column("sources_json", TypeName = "jsonb")] public string SourcesJson { get; set; } = "[]";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
