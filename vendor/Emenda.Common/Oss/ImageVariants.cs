using ImageMagick;

namespace Emenda.Common.Oss;

/// <summary>
/// The single definition of display variants for every stored mistake image: legacy uploads,
/// extraction originals/supplements/crops and conversation evidence all use the same sizes,
/// rendering and object names.
/// </summary>
/// <remarks>
/// A variant is a cache, never the source of truth. It lives next to its parent as
/// <c>{dir}/{stem}_{size}.jpg</c> (<see cref="ThumbnailHelper.GetThumbnailPath"/>), so every delete
/// that removes a parent also derives and removes its variants. Writers upload a variant and then
/// re-check that the parent still exists; deleters remove the parent before its variants. Either the
/// deleter's sweep sees the variant or the writer's re-check sees the missing parent, so a variant
/// cannot outlive its parent.
/// </remarks>
public static class ImageVariants
{
    public const string Full = "full";
    public const string ContentType = "image/jpeg";
    public const int MaxVariantBytes = 4 * 1024 * 1024;

    /// <summary>Variant sizes by long edge, largest first so one decode renders all of them.</summary>
    public static readonly IReadOnlyList<(string Size, int MaxPixel)> Sizes =
        [("medium", 800), ("small", 400), ("thumbnail", 200)];

    /// <summary>
    /// Normalizes a requested size: empty, <c>full</c> and the portal alias <c>large</c> mean the
    /// original bytes; a known variant name is returned in lower case; anything else is invalid (null).
    /// Unknown names are rejected rather than mapped so they can never create unmanaged objects.
    /// </summary>
    public static string? Normalize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return Full;
        var value = size.Trim().ToLowerInvariant();
        if (value is Full or "large" or "original") return Full;
        return Sizes.Any(s => s.Size == value) ? value : null;
    }

    public static string PathFor(string parentPath, string size) => ThumbnailHelper.GetThumbnailPath(parentPath, size);

    /// <summary>Renders every variant upright, on white, without metadata and never enlarged.</summary>
    public static Dictionary<string, byte[]> Render(byte[] parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ImageDecoderPolicy.EnsureInitialized();
        using var image = new MagickImage(parent, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
        image.AutoOrient();
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
        image.Strip();
        image.FilterType = FilterType.Catrom;
        var rendered = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (size, maxPixel) in Sizes)
        {
            if (image.Width > maxPixel || image.Height > maxPixel) image.Resize((uint)maxPixel, (uint)maxPixel);
            image.Quality = 82;
            var bytes = image.ToByteArray(MagickFormat.Jpeg);
            if (bytes.Length > MaxVariantBytes) throw new InvalidDataException("Image variant exceeds its size budget.");
            rendered[size] = bytes;
        }
        return rendered;
    }

    /// <summary>Reads a stored variant; a missing, oversized or non-JPEG object is treated as absent.</summary>
    public static async Task<byte[]?> TryReadAsync(IOssService oss, string parentPath, string size, CancellationToken ct = default)
    {
        var path = PathFor(parentPath, size);
        Stream stream;
        try
        {
            if (!await oss.ObjectExistsAsync(path).WaitAsync(ct)) return null;
            stream = await oss.DownloadAsync(path).WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        await using (stream)
        {
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            try
            {
                while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (memory.Length + count > MaxVariantBytes) return null;
                    memory.Write(buffer, 0, count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
            var bytes = memory.ToArray();
            return bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? bytes : null;
        }
    }

    /// <summary>True when every variant of the parent is already stored.</summary>
    public static async Task<bool> AllExistAsync(IOssService oss, string parentPath, CancellationToken ct = default)
    {
        foreach (var (size, _) in Sizes)
            if (!await oss.ObjectExistsAsync(PathFor(parentPath, size)).WaitAsync(ct)) return false;
        return true;
    }

    /// <summary>
    /// Renders and stores all variants of a parent whose bytes the caller has already verified.
    /// Returns false (and removes what it wrote) when the parent disappeared meanwhile.
    /// </summary>
    public static async Task<(bool Stored, Dictionary<string, byte[]> Variants)> StoreAsync(
        IOssService oss, string parentPath, byte[] parentBytes, CancellationToken ct = default)
    {
        var variants = Render(parentBytes);
        return (await StoreRenderedAsync(oss, parentPath, variants, ct), variants);
    }

    private static async Task<bool> StoreRenderedAsync(IOssService oss, string parentPath,
        Dictionary<string, byte[]> variants, CancellationToken ct)
    {
        if (!parentPath.StartsWith("mistakes/", StringComparison.Ordinal) || ThumbnailHelper.IsThumbnailPath(parentPath))
            throw new ArgumentException("Only stored mistake originals have variants.", nameof(parentPath));
        foreach (var (size, bytes) in variants)
        {
            ct.ThrowIfCancellationRequested();
            var path = PathFor(parentPath, size);
            var split = path.LastIndexOf('/');
            var actual = await oss.UploadAsync(bytes, path[(split + 1)..], ContentType, OssBucket.Mistakes,
                path["mistakes/".Length..split]);
            if (actual != path) throw new InvalidOperationException("Image variant was stored under an unexpected path.");
        }
        // Re-check after the writes: a concurrent delete removes the parent before its variants.
        if (await oss.ObjectExistsAsync(parentPath)) return true;
        await DiscardAsync(oss, parentPath);
        return false;
    }

    /// <summary>
    /// Returns the requested variant, rendering and storing all variants from the verified parent
    /// when the cache is missing. Storage failures do not fail the read: the rendered bytes are returned.
    /// </summary>
    public static async Task<byte[]> ReadOrCreateAsync(IOssService oss, string parentPath, string size,
        Func<CancellationToken, Task<byte[]>> verifiedParent, CancellationToken ct = default)
    {
        if (Normalize(size) is not { } normalized || normalized == Full)
            throw new ArgumentException("A variant size is required.", nameof(size));
        if (await TryReadAsync(oss, parentPath, normalized, ct) is { } cached) return cached;
        var variants = Render(await verifiedParent(ct));
        try { await StoreRenderedAsync(oss, parentPath, variants, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* The cache stays empty; the background backlog or the next read retries. */ }
        return variants[normalized];
    }

    /// <summary>Removes every variant of a parent (all content versions for extraction objects).</summary>
    public static async Task DiscardAsync(IOssService oss, string parentPath)
    {
        foreach (var (size, _) in Sizes)
        {
            var path = PathFor(parentPath, size);
            try
            {
                // Derived extraction objects purge every content version; other variants are plain objects.
                if (parentPath.StartsWith("mistakes/extractions/", StringComparison.Ordinal)
                    && (parentPath.Contains("/versions/", StringComparison.Ordinal) || parentPath.Contains("/supplements/", StringComparison.Ordinal)))
                    await oss.DeleteDerivedAsync(path);
                else await oss.DeleteAsync(path);
            }
            catch (Exception) { /* A later parent delete still derives and removes the variant. */ }
        }
    }
}
