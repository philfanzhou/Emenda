using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

[Table("product_intake_request")]
public sealed class ProductIntakeRequestEntity
{
    [Key, Column("request_id"), MaxLength(64)]
    public string RequestId { get; set; } = string.Empty;

    [Column("caller"), Required]
    public string Caller { get; set; } = string.Empty;

    [Column("student_id"), Required, MaxLength(36)]
    public string StudentId { get; set; } = string.Empty;

    [Column("payload_hash"), Required, MaxLength(64)]
    public string PayloadHash { get; set; } = string.Empty;

    [Column("response", TypeName = "jsonb")]
    public string? Response { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
