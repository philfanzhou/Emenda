using System.ComponentModel.DataAnnotations.Schema;
namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("source_return_object")]
public sealed class SourceReturnObjectEntity
{
    [Column("item_id")]
    public Guid ItemId { get; set; }
    [Column("position")]
    public int Position { get; set; }
    [Column("source_path")]
    public string SourcePath { get; set; } = "";
    [Column("target_key")]
    public string TargetKey { get; set; } = "";
    [Column("expected_length")]
    public long ExpectedLength { get; set; }
    [Column("expected_sha256")]
    public string ExpectedSha256 { get; set; } = "";
    [Column("selected_attempt_id")]
    public Guid? SelectedAttemptId { get; set; }
}
