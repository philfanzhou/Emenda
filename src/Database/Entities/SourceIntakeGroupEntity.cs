using System.ComponentModel.DataAnnotations.Schema;
namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("source_intake_group")]
public sealed class SourceIntakeGroupEntity
{
    [Column("item_id")]
    public Guid ItemId { get; set; }
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("group_hash")]
    public string GroupHash { get; set; } = "";
    [Column("canonical")]
    public string Canonical { get; set; } = "";
    [Column("atoms")]
    public string[] Atoms { get; set; } = [];
    [Column("regions", TypeName = "jsonb")]
    public string Regions { get; set; } = "";
    [Column("payload")]
    public string Payload { get; set; } = "";
    [Column("result")]
    public string Result { get; set; } = "";
    [Column("created_log_id")]
    public Guid CreatedLogId { get; set; }
}
