namespace Ruoyu.Study.Mistake.Domain.Models;

/// <summary>
/// A rectangle as continuous coordinates in a pixel frame. A pixel cell occupies [i, i+1), so
/// an edge at coordinate c borders pixel column/row (int)c. The values are not clamped to
/// integers: normalized boxes map to exact fractional positions and back without loss.
/// </summary>
public readonly record struct LayoutPixelRectangle(double Left, double Top, double Right, double Bottom);
