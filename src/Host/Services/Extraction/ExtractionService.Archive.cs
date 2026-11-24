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
        var item = await db.MistakeItems.AsNoTracking().SingleOrDefaultAsync(i =>
            i.Id == itemId && i.ReviewStatus == (int)ReviewStatus.Confirmed, ct);
        var source = item?.SourceUploadId;
        if (source is null || source.Length != 40 || !source.StartsWith("extract:", StringComparison.Ordinal) ||
            !Guid.TryParseExact(source.AsSpan(8), "N", out var jobId) || index >= item!.SourceRegions.Count)
            throw new ExtractionException("not_found", 404);
        var ownerHash = ExtractionRules.Hash(Owner(access));
        if (!await db.ExtractionDeletions.AnyAsync(d => d.JobId == jobId &&
            (access.Administrator || d.OwnerHash == ownerHash), ct))
            throw new ExtractionException("not_found", 404);

        var path = item.SourceRegions[index].SourceImagePath;
        var prefix = $"mistakes/extractions/{jobId:N}/versions/";
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal))
            throw new ExtractionException("not_found", 404);
        var file = path[prefix.Length..];
        if (file.Length != 166 || file[32] != '/' || file[97] != '-' || !file.EndsWith(".png", StringComparison.Ordinal) ||
            !Guid.TryParseExact(file.AsSpan(0, 32), "N", out _) ||
            !file.Skip(33).Take(64).All(char.IsAsciiHexDigit) || !file.Skip(98).Take(64).All(char.IsAsciiHexDigit))
            throw new ExtractionException("not_found", 404);
        var bytes = await ReadObjectAsync(path, ExtractionRules.MaxCropBytes, ct);
        if (!file.AsSpan(98, 64).SequenceEqual(ExtractionRules.Hash(bytes)))
            throw new ExtractionException("crop_bytes_conflict", 503);
        return bytes;
    }
}
