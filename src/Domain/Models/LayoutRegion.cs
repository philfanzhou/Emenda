namespace Emenda.Domain.Models;

/// <summary>
/// One detected region of a layout-detection answer: a generic type label plus its normalized
/// bounding box in the stored pixel frame of the submitted image.
///
/// <see cref="Id"/> is the response-local region number (the upstream array index). It identifies
/// a region inside exactly one response and is not a stable question ID, a persisted resource ID
/// or an ordering guarantee across responses. <see cref="Label"/> is the generic category the
/// runtime detected (such as text or table) — never recognized body content; unknown or generic
/// labels are preserved as-is because a region is a proposal, not a question or a correctness
/// judgment.
/// </summary>
public sealed record LayoutRegion(int Id, string Label, NormalizedLayoutBox Box);
