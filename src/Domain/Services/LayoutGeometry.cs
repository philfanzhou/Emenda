using Emenda.Domain.Models;

namespace Emenda.Domain.Services;

/// <summary>
/// Pure geometry for layout-detection results (IKQA2N). All mapping is closed-form arithmetic
/// on normalized coordinates; no image decoding, no I/O, no clock. The module defines the
/// geometry contract every later consumer (persistence, crops, editing) must share:
///
/// - Boxes are normalized in [0, 1] against the stored pixel frame
///   (<see cref="CoordinateFrame"/>) and map to continuous pixel coordinates and back without
///   intermediate rounding.
/// - The EXIF orientation 1-8 of the submitted image describes how the stored pixels must be
///   transformed for upright display. <see cref="ToDisplayFrame"/> applies exactly that
///   transform once; <see cref="FromDisplayFrame"/> is its exact inverse, so mapping to the
///   display frame and back never applies a rotation twice.
/// - The snapshot's independent <c>imageRotations</c> list is source metadata only. Student's
///   rotation operation rewrites image pixels and leaves the list unchanged, so it is not an
///   unapplied rotation and is never composed into the display transform;
///   <see cref="PreserveSnapshotRotations"/> exists to carry it with its original order and
///   length, and no transform in this module reads it.
/// </summary>
public static class LayoutGeometry
{
    /// <summary>
    /// The one coordinate frame layout results use: origin at the top-left corner of the
    /// stored pixel frame, x rightward, y downward, coordinates normalized to [0, 1].
    /// </summary>
    public const string CoordinateFrame = "top-left-origin-normalized";

    /// <summary>
    /// Maps a normalized box to continuous coordinates in the stored pixel frame. A pixel cell
    /// occupies [i, i+1); the rectangle edges keep their fractional positions so the mapping
    /// round-trips exactly. Width and height must be positive pixel counts.
    /// </summary>
    public static LayoutPixelRectangle ToStoredPixels(NormalizedLayoutBox box, int width, int height)
    {
        ValidateDimensions(width, height);
        return new LayoutPixelRectangle(box.X0 * width, box.Y0 * height, box.X1 * width, box.Y1 * height);
    }

    /// <summary>
    /// Maps a rectangle of continuous stored-pixel coordinates back to the normalized frame.
    /// Inverse of <see cref="ToStoredPixels"/> for any valid dimensions. Width and height must
    /// be positive pixel counts.
    /// </summary>
    public static NormalizedLayoutBox FromStoredPixels(LayoutPixelRectangle rectangle, int width, int height)
    {
        ValidateDimensions(width, height);
        return new NormalizedLayoutBox(
            rectangle.Left / width, rectangle.Top / height, rectangle.Right / width, rectangle.Bottom / height);
    }

    /// <summary>
    /// The dimensions the image presents once its EXIF orientation is applied. The transposing
    /// values 5-8 swap width and height; the others keep them. A null orientation (no tag) is
    /// the identity.
    /// </summary>
    public static (int Width, int Height) DisplayDimensions(int storedWidth, int storedHeight, int? orientation)
    {
        ValidateDimensions(storedWidth, storedHeight);
        if (orientation is null) return (storedWidth, storedHeight);
        ValidateOrientation(orientation.Value);
        return orientation is >= 5 and <= 8 ? (storedHeight, storedWidth) : (storedWidth, storedHeight);
    }

    /// <summary>
    /// Maps one point from stored-frame normalized coordinates (u rightward, v downward) to
    /// display-frame normalized coordinates, applying the EXIF orientation exactly as an image
    /// renderer would apply the tag itself. A null orientation is the identity.
    /// </summary>
    public static (double U, double V) StoredToDisplayPoint(int? orientation, double u, double v)
    {
        if (orientation is null) return (u, v);
        return orientation.Value switch
        {
            1 => (u, v),               // Normal.
            2 => (1 - u, v),           // Mirror horizontal.
            3 => (1 - u, 1 - v),       // Rotate 180.
            4 => (u, 1 - v),           // Mirror vertical.
            5 => (v, u),               // Transpose (mirror across the main diagonal).
            6 => (1 - v, u),           // Rotate 90 clockwise.
            7 => (1 - v, 1 - u),       // Transverse (mirror across the anti-diagonal).
            8 => (v, 1 - u),           // Rotate 90 counter-clockwise.
            _ => throw new ArgumentOutOfRangeException(
                nameof(orientation), "The EXIF orientation must be a value from 1 to 8."),
        };
    }

    /// <summary>
    /// Maps one point from display-frame normalized coordinates back to stored-frame
    /// normalized coordinates. Exact inverse of <see cref="StoredToDisplayPoint"/>: mapping a
    /// stored box to the display frame and back yields the original box, so display-side edits
    /// can always be traced back to the stored pixels without double rotation.
    /// </summary>
    public static (double U, double V) DisplayToStoredPoint(int? orientation, double u, double v)
    {
        if (orientation is null) return (u, v);
        return orientation.Value switch
        {
            1 => (u, v),
            2 => (1 - u, v),
            3 => (1 - u, 1 - v),
            4 => (u, 1 - v),
            5 => (v, u),
            6 => (v, 1 - u),
            7 => (1 - v, 1 - u),
            8 => (1 - v, u),
            _ => throw new ArgumentOutOfRangeException(
                nameof(orientation), "The EXIF orientation must be a value from 1 to 8."),
        };
    }

    /// <summary>
    /// Maps a normalized box from the stored pixel frame to the EXIF-adjusted display frame.
    /// The point transform can rotate a corner past another, so the result is the axis-aligned
    /// bounding box of the two transformed corners. Callers that already hold pixels an
    /// orientation baked into (orientation 1 or no tag) get the box back unchanged.
    /// </summary>
    public static NormalizedLayoutBox ToDisplayFrame(NormalizedLayoutBox box, int? orientation)
    {
        if (orientation is null or 1) return box;
        var (x0, y0) = StoredToDisplayPoint(orientation, box.X0, box.Y0);
        var (x1, y1) = StoredToDisplayPoint(orientation, box.X1, box.Y1);
        return new NormalizedLayoutBox(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>
    /// Maps a normalized box from the EXIF-adjusted display frame back to the stored pixel
    /// frame. Exact inverse of <see cref="ToDisplayFrame"/>: the stored-frame box recovered
    /// from a display-frame box never carries an extra rotation.
    /// </summary>
    public static NormalizedLayoutBox FromDisplayFrame(NormalizedLayoutBox box, int? orientation)
    {
        if (orientation is null or 1) return box;
        var (x0, y0) = DisplayToStoredPoint(orientation, box.X0, box.Y0);
        var (x1, y1) = DisplayToStoredPoint(orientation, box.X1, box.Y1);
        return new NormalizedLayoutBox(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>
    /// Carries the snapshot's independent rotation history forward as metadata. The values are
    /// the degrees (0/90/180/270) Student recorded for its own pixel-rewriting operations; the
    /// list is returned with its original order and length and is never composed into any
    /// transform of this module, because those operations already rewrote the stored pixels.
    /// </summary>
    public static IReadOnlyList<int> PreserveSnapshotRotations(IReadOnlyList<int> imageRotations)
    {
        foreach (var rotation in imageRotations)
        {
            if (rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentOutOfRangeException(
                    nameof(imageRotations), "Snapshot rotation values must be 0, 90, 180 or 270 degrees.");
        }
        return imageRotations.ToArray();
    }

    /// <summary>
    /// Validates one parsed layout answer against the geometry contract. Returns the empty
    /// list for a valid answer — including the legitimate explicitly empty region list — and
    /// one message per violation otherwise: request id present, positive dimensions, orientation
    /// 1-8 or absent, finite in-bounds ordered box coordinates, non-empty labels, unique
    /// response-local region ids. Invalid or incompatible output is a stage error, never an
    /// empty successful layout.
    /// </summary>
    public static IReadOnlyList<string> ValidateLayoutResult(LayoutDetectionResult result)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(result.RequestId))
            errors.Add("The response carries no request id.");
        if (result.Width <= 0 || result.Height <= 0)
            errors.Add("The reported image dimensions must be positive pixel counts.");

        if (result.Orientation is < 1 or > 8)
            errors.Add("The reported orientation must be an EXIF value from 1 to 8.");

        if (result.Engine is not null && string.IsNullOrWhiteSpace(result.Engine))
            errors.Add("The reported engine must be non-empty when present.");
        if (result.Model is not null && string.IsNullOrWhiteSpace(result.Model))
            errors.Add("The reported model must be non-empty when present.");
        if (result.Version is not null && string.IsNullOrWhiteSpace(result.Version))
            errors.Add("The reported version must be non-empty when present.");

        var regionIds = new HashSet<int>();
        for (var i = 0; i < result.Regions.Count; i++)
        {
            var region = result.Regions[i];
            var prefix = $"Region {i}";
            if (!regionIds.Add(region.Id))
                errors.Add($"{prefix} repeats region id {region.Id}; ids are response-local and must be unique.");
            if (string.IsNullOrWhiteSpace(region.Label))
                errors.Add($"{prefix} carries no label.");
            var box = region.Box;
            if (!double.IsFinite(box.X0) || !double.IsFinite(box.Y0) ||
                !double.IsFinite(box.X1) || !double.IsFinite(box.Y1))
                errors.Add($"{prefix} carries a non-finite coordinate.");
            else
            {
                if (box.X0 < 0 || box.X0 > 1 || box.X1 < 0 || box.X1 > 1 ||
                    box.Y0 < 0 || box.Y0 > 1 || box.Y1 < 0 || box.Y1 > 1)
                    errors.Add($"{prefix} carries a coordinate outside [0, 1].");
                if (box.X0 > box.X1 || box.Y0 > box.Y1)
                    errors.Add($"{prefix} carries unordered coordinates; (x0, y0) must be the top-left corner.");
            }
        }
        return errors;
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive pixel counts.");
    }

    private static void ValidateOrientation(int orientation)
    {
        if (orientation is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(orientation), "The EXIF orientation must be a value from 1 to 8.");
    }
}
