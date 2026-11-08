using System.ComponentModel.DataAnnotations.Schema;
namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("legacy_return_attempt")]
public sealed class LegacyReturnAttemptEntity
{
    [Column("attempt_id")]
    public Guid AttemptId { get; set; }
    [Column("item_id")]
    public Guid ItemId { get; set; }
    [Column("position")]
    public int Position { get; set; }
    [Column("object_key")]
    public string ObjectKey { get; set; } = "";
    [Column("owner_id")]
    public Guid OwnerId { get; set; }
    [Column("fence")]
    public long Fence { get; set; }
    [Column("phase")]
    public string Phase { get; set; } = "Reserved";
    [Column("copy_settled_at")]
    public DateTimeOffset? CopySettledAt { get; set; }
    [Column("verified_length")]
    public long? VerifiedLength { get; set; }
    [Column("verified_sha256")]
    public string? VerifiedSha256 { get; set; }
}
