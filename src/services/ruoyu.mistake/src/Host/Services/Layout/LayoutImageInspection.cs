using System.Buffers.Binary;

namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>
/// Bounded header inspection of one submitted layout image (IKQA2N), mirroring the upstream
/// StructaDoc v0.1.13 request contract: only JPEG and PNG signatures are accepted, the stored
/// dimensions are read from the headers (PNG IHDR / the first JPEG SOF marker), and the byte
/// and pixel bounds match the endpoint's own limits. The inspection never decodes pixels; it
/// is the consumer-side pre-send defence so an image the endpoint would refuse is rejected
/// locally with zero requests.
/// </summary>
public static class LayoutImageInspection
{
    public const int MaximumImageBytes = 32 * 1024 * 1024;

    public const int MaximumDimensionPixels = 32_000;

    public const long MaximumTotalPixels = 100_000_000;

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Inspects one image. Returns the detected media type in the wire spelling and the stored
    /// (non-oriented) pixel dimensions; every rejection carries a fixed message naming the
    /// violated bound. Structural header damage is a rejection, not a best-effort pass,
    /// because dimensions the consumer cannot determine cannot be pre-checked against the
    /// endpoint's pixel bounds.
    /// </summary>
    public static bool TryInspect(ReadOnlySpan<byte> bytes, out string mediaType, out int width, out int height, out string? rejection)
    {
        mediaType = "";
        width = 0;
        height = 0;
        rejection = null;
        if (bytes.Length == 0)
        {
            rejection = "The image is empty.";
            return false;
        }
        if (bytes.Length > MaximumImageBytes)
        {
            rejection = $"The image exceeds the {MaximumImageBytes}-byte layout detection limit.";
            return false;
        }
        if (bytes.StartsWith(PngSignature))
        {
            if (!TryReadPngDimensions(bytes, out width, out height))
            {
                rejection = "The PNG image carries no readable IHDR header.";
                return false;
            }
            mediaType = "image/png";
        }
        else if (bytes.StartsWith(JpegSignature))
        {
            if (!TryReadJpegDimensions(bytes, out width, out height))
            {
                rejection = "The JPEG image carries no readable frame header.";
                return false;
            }
            mediaType = "image/jpeg";
        }
        else
        {
            rejection = "The submitted image must be a JPEG or a PNG.";
            return false;
        }
        if (width > MaximumDimensionPixels || height > MaximumDimensionPixels ||
            (long)width * height > MaximumTotalPixels)
        {
            rejection = $"The image exceeds the pixel bounds of {MaximumDimensionPixels} per dimension and {MaximumTotalPixels} in total.";
            return false;
        }
        return true;
    }

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        // Signature (8) + length (4) + "IHDR" (4) precede the big-endian width and height.
        if (bytes.Length < 24) return false;
        if (BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) != 13) return false;
        if (bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R') return false;
        width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        return width > 0 && height > 0;
    }

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        // Walk the marker segments after SOI. Every marker except standalone RST/SOI/EOI and
        // SOS carries a big-endian length; the first SOF marker (C0-CF except C4/C8/CC) holds
        // the frame dimensions. Dimensions must precede the scan data, so SOS ends the walk.
        var position = 2;
        while (position + 1 < bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                position++;
                continue;
            }
            var marker = bytes[position + 1];
            if (marker is 0xFF)
            {
                position++;
                continue;
            }
            position += 2;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xD9) continue;
            if (marker == 0xDA) return false; // Scan data reached without a frame header.
            if (position + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (length < 2) return false;
            var isStartOfFrame = marker is (>= 0xC0 and <= 0xCF) and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                // Length (2), sample precision (1), then height (2) and width (2).
                if (position + 7 > bytes.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]);
                return width > 0 && height > 0;
            }
            position += length;
        }
        return false;
    }
}
