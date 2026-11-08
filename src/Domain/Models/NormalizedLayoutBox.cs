namespace Ruoyu.Study.Mistake.Domain.Models;

/// <summary>
/// A normalized layout box in the <see cref="Services.LayoutGeometry.CoordinateFrame"/>: the
/// origin is the top-left corner of the reported pixel frame, x grows rightward, y grows
/// downward, and every coordinate is a finite value in [0, 1]. (x0, y0) is the top-left corner
/// and (x1, y1) the bottom-right corner, so x0 &lt;= x1 and y0 &lt;= y1; degenerate zero-area
/// boxes are tolerated because the upstream runtime can emit them. Validation lives in
/// <see cref="Services.LayoutGeometry"/>; this record is the inert value carrier.
/// </summary>
public readonly record struct NormalizedLayoutBox(double X0, double Y0, double X1, double Y1);
