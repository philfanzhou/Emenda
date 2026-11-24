using System.Globalization;
using Emenda.Database;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    // The caller holds SerializeJobAsync; active deletions register the listing in their metadata transaction.
    private async Task InventoryObjectsAsync(MistakeDbContext db, Guid jobId, CancellationToken ct) =>
        await RegisterJobObjectsAsync(db, jobId, await ListJobObjectPathsAsync(jobId, ct), ct);

    private async Task<string[]> ListJobObjectPathsAsync(Guid jobId, CancellationToken ct)
    {
        // A failed or partial listing must abort before metadata disappears or a receipt reports completion.
        var objects = await oss.ListExtractionObjectsAsync(jobId).WaitAsync(ct);
        return objects.Select(o => o.ObjectPath).Where(path => IsJobObjectPath(jobId, path))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static Task RegisterJobObjectsAsync(MistakeDbContext db, Guid jobId, string[] paths, CancellationToken ct)
    {
        if (paths.Length == 0) return Task.CompletedTask;
        // Do not reset pending fences or retry backoff for an object already being collected.
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_object(path,job_id,pending,legacy,attempts,next_attempt)
            SELECT path,{jobId},false,false,0,clock_timestamp() FROM unnest({paths}) path
            ON CONFLICT(path) DO NOTHING
            """, ct);
    }

    private static bool IsJobObjectPath(Guid jobId, string path)
    {
        var key = path.AsSpan();
        Span<Range> segments = stackalloc Range[8];
        var count = key.Split(segments, '/');
        if (count is not (5 or 6 or 7) || !key[segments[0]].SequenceEqual("mistakes") ||
            !key[segments[1]].SequenceEqual("extractions") || !IsStorageGuid(key[segments[2]]) ||
            !Guid.TryParseExact(key[segments[2]], "N", out var owner) || owner != jobId)
            return false;
        if (count == 5 && key[segments[3]].SequenceEqual("originals"))
            return IsSourceObjectName(key[segments[4]]);
        if (count == 7 && key[segments[3]].SequenceEqual("supplements"))
            return IsStorageGuid(key[segments[4]]) && key[segments[5]].SequenceEqual("originals") &&
                IsSourceObjectName(key[segments[6]]);
        if (!key[segments[3]].SequenceEqual("versions") || !IsStorageGuid(key[segments[4]]))
            return false;
        var file = key[segments[count - 1]];
        if (!file.EndsWith(".png", StringComparison.Ordinal)) return false;
        var stem = file[..^4];
        if (count == 6)
            return stem.Length == 129 && stem[64] == '-' && IsStorageHex(stem[..64]) && IsStorageHex(stem[65..]);
        // Before v20 crops were versions/{request:N}/{question:N}/{index}-{sha256[..16]}.png.
        if (count != 7 || !IsStorageGuid(key[segments[5]])) return false;
        var separator = stem.IndexOf('-');
        return separator > 0 && stem.Length - separator - 1 == 16 &&
            int.TryParse(stem[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            IsStorageHex(stem[(separator + 1)..]);
    }

    private static bool IsSourceObjectName(ReadOnlySpan<char> name) =>
        name.Length == 36 && (name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal)) &&
        IsStorageGuid(name[..^4]);

    private static bool IsStorageGuid(ReadOnlySpan<char> value) =>
        value.Length == 32 && IsStorageHex(value);

    private static bool IsStorageHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }
}
