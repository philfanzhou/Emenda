using ImageMagick;

namespace Emenda.TestFixtures;

/// <summary>Raw pixel test generation does not depend on pseudo-format coders or delegates.</summary>
internal static class RasterFixture
{
    public static void AssertJpeg(byte[] actualBytes, byte[] expectedBytes)
    {
        using var actual = new MagickImage(actualBytes);
        using var expected = new MagickImage(expectedBytes);
        Xunit.Assert.Equal(MagickFormat.Jpeg, actual.Format);
        Xunit.Assert.Equal(expected.Width, actual.Width);
        Xunit.Assert.Equal(expected.Height, actual.Height);
        var actualPixels = actual.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var expectedPixels = expected.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var mean = System.Linq.Enumerable.Average(System.Linq.Enumerable.Zip(actualPixels, expectedPixels,
            (a, e) => System.Math.Abs(a - e)));
        Xunit.Assert.True(mean <= 3.0, $"Unexpected JPEG pixel error: {mean}");
    }

    public static MagickImage Read(byte[] bytes)
    {
        if (bytes.Length >= 18 && bytes[1] <= 1 && bytes[2] is 1 or 2 or 3 or 9 or 10 or 11
            && (bytes[12] != 0 || bytes[13] != 0) && (bytes[14] != 0 || bytes[15] != 0)
            && bytes[16] is 8 or 15 or 16 or 24 or 32)
            return new MagickImage(bytes, MagickFormat.Tga);
        return new MagickImage(bytes);
    }

    public static MagickImage Create(int width, int height, byte red = 0, byte green = 0, byte blue = 85)
    {
        using var policy = Emenda.Common.Oss.GuardedImageDecoder.Load(System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC"));
        var bytes = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                bytes[offset] = (byte)(red + x * 17);
                bytes[offset + 1] = (byte)(green + y * 31);
                bytes[offset + 2] = blue;
            }
        var image = new MagickImage();
        image.ReadPixels(bytes, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"));
        image.Quality = 75;
        return image;
    }
}
