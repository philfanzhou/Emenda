using System;
using SixLabors.ImageSharp;
using System.Collections.Generic;

namespace Ruoyu.Study.TestFixtures;

/// <summary>Self-contained, uncompressed 1x1 grayscale BigTIFF fixtures; no TIFF encoder is used.</summary>
internal static class BigTiffFixture
{
    public static byte[] Valid(bool littleEndian, int frames)
    {
        var bytes = Header(littleEndian, 16, 16 + frames * 176 + frames);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = 16 + frame * 176;
            Write(bytes, offset, 8, 8, littleEndian);
            var entries = new (int Tag, int Type, ulong Value)[]
            {
                (256, 4, 1), (257, 4, 1), (258, 3, 8), (259, 3, 1),
                (262, 3, 1), (273, 16, (ulong)(16 + frames * 176 + frame)), (278, 4, 1), (279, 16, 1)
            };
            for (var i = 0; i < entries.Length; i++)
            {
                var pos = offset + 8 + i * 20;
                Write(bytes, pos, 2, (ulong)entries[i].Tag, littleEndian);
                Write(bytes, pos + 2, 2, (ulong)entries[i].Type, littleEndian);
                Write(bytes, pos + 4, 8, 1, littleEndian);
                Write(bytes, pos + 12, entries[i].Type == 3 ? 2 : entries[i].Type == 4 ? 4 : 8, entries[i].Value, littleEndian);
            }
            Write(bytes, offset + 168, 8, frame + 1 < frames ? (ulong)(offset + 176) : 0, littleEndian);
            bytes[16 + frames * 176 + frame] = (byte)(64 + frame * 64);
        }
        return bytes;
    }

    public static IEnumerable<object[]> Pictures()
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(13, 7);
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++) image[x, y] = new((byte)(x * 17), (byte)(y * 31), 85);
        foreach (var encoder in new SixLabors.ImageSharp.Formats.IImageEncoder[]
        {
            new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder(),
            new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
            new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder
            { Compression = SixLabors.ImageSharp.Formats.Tiff.Constants.TiffCompression.None }
        })
        {
            using var stream = new System.IO.MemoryStream();
            image.Save(stream, encoder);
            yield return new object[] { stream.ToArray() };
        }
        foreach (var little in new[] { true, false })
            foreach (var frames in new[] { 1, 2 }) yield return new object[] { Valid(little, frames) };
    }

    public static byte[] Header(bool littleEndian, ulong firstOffset, int length = 24)
    {
        var bytes = new byte[length];
        bytes[0] = bytes[1] = littleEndian ? (byte)0x49 : (byte)0x4d;
        Write(bytes, 2, 2, 43, littleEndian);
        Write(bytes, 4, 2, 8, littleEndian);
        Write(bytes, 8, 8, firstOffset, littleEndian);
        return bytes;
    }

    public static IEnumerable<object[]> Malformed()
    {
        foreach (var little in new[] { true, false })
        {
            foreach (var count in new[] { 5000000000UL, ulong.MaxValue })
            {
                var bytes = Header(little, 16);
                Write(bytes, 16, 8, count, little);
                yield return new object[] { bytes };
            }
            var second = Valid(little, 2);
            Write(second, 192, 8, 5000000000UL, little);
            yield return new object[] { second };
        }
    }

    public static void Write(byte[] bytes, int offset, int width, ulong value, bool littleEndian)
    {
        for (var i = 0; i < width; i++) bytes[offset + (littleEndian ? i : width - 1 - i)] = (byte)(value >> (8 * i));
    }
}
