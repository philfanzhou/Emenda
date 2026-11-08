using ImageMagick;

namespace Ruoyu.Study.Common.Oss;

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
