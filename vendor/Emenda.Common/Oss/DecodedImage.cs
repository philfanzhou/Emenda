using ImageMagick;

namespace Emenda.Common.Oss;

/// <summary>Owned decoded raster frames. Native objects never leave the Common boundary.</summary>
public sealed class DecodedImage : IDisposable
{
    private readonly MagickImageCollection _frames;
    private readonly uint _jpegQuality;

    internal DecodedImage(byte[] bytes)
    {
        ImageDecoderPolicy.EnsureInitialized();
        _frames = new MagickImageCollection();
        try
        {
            // TGA has no required magic signature. Match its raster header, then supply
            // the coder explicitly; every other format retains automatic byte detection.
            var settings = new MagickReadSettings();
            // Preserve embedded PNG ICC bytes instead of replacing recognized sRGB profiles.
            settings.SetDefine("png:preserve-iCCP", "true");
            if (IsTga(bytes)) settings.Format = MagickFormat.Tga;
            _frames.Read(bytes, settings);
            var first = _frames[0];
            _jpegQuality = first.Format == MagickFormat.Jpeg && first.Quality is >= 1 and <= 100 ? first.Quality : 75;
        }
        catch
        {
            _frames.Dispose();
            throw;
        }
    }

    private static bool IsTga(byte[] bytes) => bytes.Length >= 18
        && bytes[1] <= 1 && bytes[2] is 1 or 2 or 3 or 9 or 10 or 11
        && (bytes[12] != 0 || bytes[13] != 0) && (bytes[14] != 0 || bytes[15] != 0)
        && bytes[16] is 8 or 15 or 16 or 24 or 32;

    public int Width => checked((int)_frames[0].Width);
    public int Height => checked((int)_frames[0].Height);
    public int FrameCount => _frames.Count;

    public void ResizeMax(int maxPixel, bool enlarge = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixel);
        if (!enlarge && Width <= maxPixel && Height <= maxPixel) return;
        foreach (var frame in _frames)
        {
            frame.FilterType = FilterType.Catrom;
            frame.Resize((uint)maxPixel, (uint)maxPixel);
        }
    }

    public void Rotate(int degrees)
    {
        if (degrees is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(degrees));
        if (degrees == 0) return;
        foreach (var frame in _frames)
        {
            frame.Rotate(degrees);
            // Manual rotation historically removes the orientation tag, without
            // applying it to pixels or discarding the remaining EXIF/ICC metadata.
            if (frame.GetExifProfile() is { } exif)
            {
                exif.RemoveValue(ExifTag.Orientation);
                frame.SetProfile(exif);
            }
            frame.Orientation = Orientation.Undefined;
        }
    }

    public byte[] ToJpeg(int? quality = null) => FrameToJpeg(0, quality);

    /// <summary>Crop a question in displayed orientation, in normalized 0–1000 coordinates.</summary>
    public (byte[] Data, int Width, int Height) CropQuestion(int x1, int y1, int x2, int y2)
    {
        if (x1 < 0 || y1 < 0 || x2 > 1000 || y2 > 1000 || x1 >= x2 || y1 >= y2)
            throw new ArgumentException("Invalid question region.");
        using var image = _frames[0].Clone();
        image.AutoOrient();
        var left = (int)((long)x1 * image.Width / 1000);
        var top = (int)((long)y1 * image.Height / 1000);
        var right = (int)(((long)x2 * image.Width + 999) / 1000);
        var bottom = (int)(((long)y2 * image.Height + 999) / 1000);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0) throw new ArgumentException("Question region is empty.");
        if (left != 0 || top != 0 || right != image.Width || bottom != image.Height)
        {
            image.Crop(new MagickGeometry(left, top, (uint)width, (uint)height));
            image.ResetPage();
        }
        image.Quality = 85;
        return (image.ToByteArray(MagickFormat.Jpeg), width, height);
    }

    internal byte[] FrameToJpeg(int frame, int? quality = null)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        using var image = _frames[frame].Clone();
        image.Quality = quality.HasValue ? (uint)quality.Value : _jpegQuality;
        // The previous encoder discarded the alpha channel, without automatic orientation
        // or color-profile conversion. Preserve original pixel coordinates and profiles.
        image.BackgroundColor = new MagickColor(0, 0, 0);
        image.Alpha(AlphaAction.Deactivate);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    public void Dispose() => _frames.Dispose();
}
