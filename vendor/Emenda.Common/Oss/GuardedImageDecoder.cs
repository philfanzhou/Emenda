using System.Buffers.Binary;
using ImageMagick;

namespace Emenda.Common.Oss;

/// <summary>
/// Checks physical BigTIFF directory bounds before automatic image decoding.
/// Callers must not modify the input concurrently. The caller retains stream ownership.
/// </summary>
public static class GuardedImageDecoder
{
    /// <summary>Inspect bounded question-image headers before allocating decoded pixels.</summary>
    public static DecodedImage LoadQuestion(byte[] bytes)
    {
        if (bytes.Length is 0 or > 4_500_000) throw new ArgumentException("Question image exceeds the byte budget.");
        var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 });
        var webp = bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        if (!png && !jpeg && !webp) throw new ArgumentException("Only JPEG, PNG and WebP question images are supported.");
        ImageDecoderPolicy.EnsureInitialized();
        using var headers = new MagickImageCollection();
        headers.Ping(bytes, new MagickReadSettings { FrameCount = 2 });
        if (headers.Count != 1) throw new ArgumentException("Question images must contain one frame.");
        var frame = headers[0];
        if (frame.Width > 8192 || frame.Height > 8192 || (long)frame.Width * frame.Height > 16_000_000)
            throw new ArgumentException("Question image exceeds the pixel bounds.");
        return Load(bytes);
    }

    public static DecodedImage Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ValidateBigTiff(stream);
        return new DecodedImage(bytes);
    }

    public static async Task<DecodedImage> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Normalize all inputs into one immutable byte buffer. Validation and native
        // decoding must see exactly the same bytes, including nonzero TIFF origins.
        var start = stream.CanSeek ? stream.Position : (long?)null;
        using var buffer = new MemoryStream();
        var completed = false;
        try
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            ValidateBigTiff(buffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var image = new DecodedImage(buffer.ToArray());
            completed = true;
            return image;
        }
        finally
        {
            if (start.HasValue && (start.Value != 0 || !completed)) stream.Position = start.Value;
        }
    }

    internal static void ValidateBigTiff(Stream stream, CancellationToken cancellationToken = default)
    {
        var start = stream.Position;
        try
        {
            var length = (ulong)(stream.Length - start);
            if (length < 4) return;
            Span<byte> header = stackalloc byte[16];
            ReadExactly(stream, header[..4]);
            var littleEndian = header[0] == 0x49 && header[1] == 0x49;
            if (!littleEndian && !(header[0] == 0x4d && header[1] == 0x4d)) return;
            if (ReadUInt16(header[2..4], littleEndian) != 43) return;
            if (length < 16) throw InvalidDirectory();
            ReadExactly(stream, header[4..]);
            if (ReadUInt16(header[4..6], littleEndian) != 8 || ReadUInt16(header[6..8], littleEndian) != 0)
                throw InvalidDirectory();

            var offset = ReadUInt64(header[8..], littleEndian);
            var directories = 0;
            Span<byte> value = stackalloc byte[8];
            while (offset != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Check by subtraction/division before computing count * 20 or adding offsets.
                if (offset > length || length - offset < 16) throw InvalidDirectory();
                stream.Position = start + (long)offset;
                ReadExactly(stream, value);
                var count = ReadUInt64(value, littleEndian);
                var remaining = length - offset - 8;
                if (count > (remaining - 8) / 20) throw InvalidDirectory();
                stream.Position = start + (long)(offset + 8 + count * 20);
                ReadExactly(stream, value);
                var next = ReadUInt64(value, littleEndian);
                if (next != 0 && next <= offset) throw InvalidDirectory();
                offset = next;
                // Match the decoder's existing forward-only main IFD chain limit.
                if (++directories >= 65534) throw InvalidDirectory();
            }
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);

    private static void ReadExactly(Stream stream, Span<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var count = stream.Read(bytes);
            if (count == 0) throw InvalidDirectory();
            bytes = bytes[count..];
        }
    }

    private static InvalidDataException InvalidDirectory() => new("Invalid BigTIFF directory structure.");
}
