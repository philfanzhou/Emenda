using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    public async Task<byte[]> ReadRetainedCropAsync(ExtractionAccess access, Guid itemId, int index, CancellationToken ct)
    {
        if (index < 0) throw new ExtractionException("not_found", 404);
        await using var db = Open();
        var (item, jobId) = await RetainedItemAsync(db, access, itemId, ct);
        if (index >= item.SourceRegions.Count) throw new ExtractionException("not_found", 404);
        var image = await ReadRetainedFileAsync(item, jobId, item.SourceRegions[index].SourceImagePath, ct);
        return image.Bytes;
    }

    /// <summary>
    /// A retained confirmed crop at a display size. The crop stays referenced after its task is deleted,
    /// so cleanup keeps it and its variants together; a missing variant is rendered from the verified file.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)> ReadRetainedCropAsync(ExtractionAccess access, Guid itemId, int index,
        string? size, CancellationToken ct)
    {
        var display = DisplaySize(size);
        if (index < 0) throw new ExtractionException("not_found", 404);
        await using var db = Open();
        var (item, jobId) = await RetainedItemAsync(db, access, itemId, ct);
        if (index >= item.SourceRegions.Count) throw new ExtractionException("not_found", 404);
        var path = item.SourceRegions[index].SourceImagePath;
        if (display == Emenda.Common.Oss.ImageVariants.Full) return await ReadRetainedFileAsync(item, jobId, path, ct);
        return (await VariantAsync(path, display, async token => (await ReadRetainedFileAsync(item, jobId, path, token)).Bytes, ct),
            Emenda.Common.Oss.ImageVariants.ContentType);
    }

    private static async Task<(MistakeItemEntity Item, Guid JobId)> RetainedItemAsync(MistakeDbContext db,
        ExtractionAccess access, Guid itemId, CancellationToken ct)
    {
        var item = await db.MistakeItems.AsNoTracking().SingleOrDefaultAsync(i =>
            i.Id == itemId && i.ReviewStatus == (int)ReviewStatus.Confirmed, ct);
        if (item is null || MistakeSource.ExtractionJobId(item) is not { } jobId)
            throw new ExtractionException("not_found", 404);
        var ownerHash = ExtractionRules.Hash(Owner(access));
        if (!await db.ExtractionDeletions.AnyAsync(d => d.JobId == jobId &&
            (access.Administrator || d.OwnerHash == ownerHash), ct))
            throw new ExtractionException("not_found", 404);
        return (item!, jobId);
    }

    private async Task<(byte[] Bytes, string ContentType)> ReadRetainedFileAsync(MistakeItemEntity item,
        Guid jobId, string path, CancellationToken ct)
    {
        if (!IsJobObjectPath(jobId, path)) throw new ExtractionException("not_found", 404);
        if (MistakeSource.RetainedFiles(item) is { } manifest)
        {
            var file = JsonSerializer.Deserialize<List<RetainedFile>>(manifest, ExtractionRules.Json)?.SingleOrDefault(f => f.Path == path)
                ?? throw new ExtractionException("not_found", 404);
            if (file.ContentType is not ("image/png" or "image/jpeg") || file.Length <= 0 ||
                file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit))
                throw new ExtractionException("invalid_retained_state", 503);
            var bytes = await ReadObjectAsync(path, ExtractionRules.MaxCropBytes, ct);
            if (bytes.LongLength != file.Length || ExtractionRules.Hash(bytes) != file.Sha256)
                throw new ExtractionException("retained_image_bytes_conflict", 503);
            return (bytes, file.ContentType);
        }

        return await ReadLegacyFileAsync(path, ct);
    }

    private async Task<(byte[] Bytes, string ContentType)> ReadLegacyFileAsync(string path, CancellationToken ct)
    {
        // rc.12 receipts have no file manifest. Exact owner-bound references remain readable;
        // crop names carry a full digest (new) or a 16-hex digest (historical).
        var contentType = path.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";
        var limit = path.Contains("/versions/", StringComparison.Ordinal) ? ExtractionRules.MaxCropBytes : ExtractionRules.MaxImageBytes;
        var retained = await ReadObjectAsync(path, limit, ct);
        if (path.Contains("/versions/", StringComparison.Ordinal))
        {
            var hash = path[(path.LastIndexOf('-') + 1)..^4];
            if (!ExtractionRules.Hash(retained).AsSpan(0, hash.Length).SequenceEqual(hash))
                throw new ExtractionException("crop_bytes_conflict", 503);
        }
        return (retained, contentType);
    }
}
