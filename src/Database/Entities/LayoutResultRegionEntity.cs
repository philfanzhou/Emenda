using System.ComponentModel.DataAnnotations.Schema;

namespace Emenda.Database.Entities;

/// <summary>One validated region of an accepted layout result (IKQA2O): the
/// response-local region id (which is also the preserved upstream detection sequence),
/// the generic type label and the normalized box in the stored pixel frame. Immutable
/// with its result.</summary>
[Table("layout_result_region")]
public sealed class LayoutResultRegionEntity
{
    [Column("result_id")]
    public Guid ResultId { get; set; }
    [Column("region_id")]
    public int RegionId { get; set; }
    [Column("label")]
    public string Label { get; set; } = "";
    [Column("x0")]
    public double X0 { get; set; }
    [Column("y0")]
    public double Y0 { get; set; }
    [Column("x1")]
    public double X1 { get; set; }
    [Column("y1")]
    public double Y1 { get; set; }
}
