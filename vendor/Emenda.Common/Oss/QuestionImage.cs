using ImageMagick;

namespace Emenda.Common.Oss;

/// <summary>Bounded, upright question images; retained originals are never rewritten.</summary>
public static class QuestionImage
{
    public sealed record Metadata(int Width, int Height, int DisplayWidth, int DisplayHeight, int? Orientation);

    public static Metadata Inspect(byte[] bytes, string contentType)
    {
        if (bytes.Length is 0 or > 15 * 1024 * 1024) throw new ArgumentException("Invalid image size.");
        var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 });
        if (!(contentType == "image/png" && png || contentType == "image/jpeg" && jpeg))
            throw new ArgumentException("Image signature does not match its media type.");
        ImageDecoderPolicy.EnsureInitialized();
        using var images = new MagickImageCollection();
        images.Ping(bytes, new MagickReadSettings { FrameCount = 2 });
        if (images.Count != 1) throw new ArgumentException("Exactly one image frame is required.");
        var image = images[0];
        if (image.Width is 0 or > 8192 || image.Height is 0 or > 8192 || (long)image.Width * image.Height > 16_000_000)
            throw new ArgumentException("Image exceeds pixel bounds.");
        var orientation = image.Orientation == Orientation.Undefined ? (int?)null : (int)image.Orientation;
        if (orientation is < 1 or > 8) throw new ArgumentException("Invalid image orientation.");
        var w = checked((int)image.Width);
        var h = checked((int)image.Height);
        return new(w, h, orientation is >= 5 and <= 8 ? h : w, orientation is >= 5 and <= 8 ? w : h, orientation);
    }

    public static byte[] Preview(byte[] bytes, int maxBytes)
    {
        if (maxBytes < 64 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ImageDecoderPolicy.EnsureInitialized();
        using var image = new MagickImage(bytes);
        image.AutoOrient();
        if (image.Width > 1600 || image.Height > 1600) image.Resize(1600, 1600);
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
        image.Strip();
        image.Quality = 85;
        for (var attempt = 0; attempt < 7; attempt++)
        {
            var encoded = image.ToByteArray(MagickFormat.Jpeg);
            if (encoded.Length <= maxBytes) return encoded;
            image.Quality = 70;
            image.Resize(Math.Max(256u, image.Width * 4 / 5), Math.Max(256u, image.Height * 4 / 5));
        }
        throw new InvalidDataException("Image preview cannot fit the request budget.");
    }

    public static (byte[] Bytes, int Width, int Height) Crop(byte[] bytes, double x0, double y0, double x1, double y1)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1) ||
            x0 < 0 || y0 < 0 || x1 > 1 || y1 > 1 || x0 >= x1 || y0 >= y1)
            throw new ArgumentException("Invalid image region.");
        ImageDecoderPolicy.EnsureInitialized();
        using var image = new MagickImage(bytes);
        image.AutoOrient();
        var left = (int)Math.Floor(x0 * image.Width);
        var top = (int)Math.Floor(y0 * image.Height);
        var right = Math.Min((int)image.Width, (int)Math.Ceiling(x1 * image.Width));
        var bottom = Math.Min((int)image.Height, (int)Math.Ceiling(y1 * image.Height));
        var width = right - left;
        var height = bottom - top;
        image.Crop(new MagickGeometry(left, top, (uint)width, (uint)height));
        image.ResetPage();
        image.Strip();
        return (image.ToByteArray(MagickFormat.Png), width, height);
    }
}
