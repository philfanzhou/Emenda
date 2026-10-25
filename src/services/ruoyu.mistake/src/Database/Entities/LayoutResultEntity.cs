using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

/// <summary>One accepted layout result for one image of one job generation (IKQA2O).
/// Insert-only: the first accepted answer per job image wins and a late duplicate is
/// discarded. Each row is self-contained — bound to source upload, content revision,
/// snapshot, exact image byte identity, relevant source rotation, producing job attempt
/// and the captured non-secret processing configuration. Rows and their regions are
/// immutable; database triggers refuse UPDATE and DELETE.</summary>
[Table("layout_result")]
public sealed class LayoutResultEntity
{
    [Column("result_id")]
    public Guid ResultId { get; set; }
    [Column("job_id")]
    public Guid JobId { get; set; }
    [Column("source_upload_id")]
    public Guid SourceUploadId { get; set; }
    [Column("content_revision")]
    public Guid ContentRevision { get; set; }
    [Column("snapshot_id")]
    public Guid SnapshotId { get; set; }
    [Column("result_revision")]
    public int ResultRevision { get; set; }
    [Column("image_path")]
    public string ImagePath { get; set; } = "";
    [Column("image_position")]
    public int ImagePosition { get; set; }
    [Column("bytes_length")]
    public long BytesLength { get; set; }
    [Column("bytes_sha256")]
    public string BytesSha256 { get; set; } = "";
    [Column("source_rotation")]
    public int? SourceRotation { get; set; }
    [Column("attempt")]
    public int Attempt { get; set; }
    [Column("upstream_request_id")]
    public string UpstreamRequestId { get; set; } = "";
    [Column("contract_id")]
    public string ContractId { get; set; } = "";
    [Column("coordinate_frame")]
    public string CoordinateFrame { get; set; } = "";
    [Column("width")]
    public int Width { get; set; }
    [Column("height")]
    public int Height { get; set; }
    [Column("orientation")]
    public int? Orientation { get; set; }
    [Column("engine")]
    public string? Engine { get; set; }
    [Column("model")]
    public string? Model { get; set; }
    [Column("version")]
    public string? Version { get; set; }
    [Column("diagnostics")]
    public string Diagnostics { get; set; } = "[]";
    [Column("config")]
    public string Config { get; set; } = "";
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
