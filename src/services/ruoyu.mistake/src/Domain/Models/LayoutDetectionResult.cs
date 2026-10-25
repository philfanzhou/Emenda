namespace Ruoyu.Study.Mistake.Domain.Models;

/// <summary>
/// One validated, non-archiving layout-detection answer for a single submitted image.
///
/// <see cref="Width"/> and <see cref="Height"/> describe the submitted image's stored pixel
/// frame — the frame the pixels occupy as stored, before any EXIF orientation is applied for
/// display. Every region box normalizes against these dimensions. Use
/// <see cref="Services.LayoutGeometry"/> to map boxes to stored pixel coordinates or to the
/// EXIF-adjusted display frame.
///
/// Ordering semantics: <see cref="Regions"/> preserves the upstream detection sequence. That
/// sequence is a detection pass over the page, not a guaranteed reading or question order. An
/// explicitly empty <see cref="Regions"/> list is a legitimate accepted answer — the runtime
/// looked and found nothing — and carries no conclusion about whether the photo contains
/// questions.
///
/// <see cref="Orientation"/> is the EXIF orientation value (1-8) the upstream service derived
/// from the submitted image, or null when the image carried no orientation tag. The optional
/// engine/model/version metadata describe the inference runtime that produced the answer;
/// they are observations, not reproducibility promises. <see cref="Diagnostics"/> carries
/// bounded recoverable notes from the execution.
/// </summary>
public sealed record LayoutDetectionResult
{
    public required string RequestId { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int? Orientation { get; init; }

    public string? Engine { get; init; }

    public string? Model { get; init; }

    public string? Version { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public required IReadOnlyList<LayoutRegion> Regions { get; init; }
}
