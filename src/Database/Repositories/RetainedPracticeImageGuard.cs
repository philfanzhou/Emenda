using System.Text.Json;
using Emenda.Database.Entities;
using Emenda.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Database.Repositories;

internal static class RetainedPracticeImageGuard
{
    internal static async Task ValidateAsync(MistakeDbContext db, MistakePracticeEntity[] practices, CancellationToken ct)
    {
        foreach (var group in practices.GroupBy(p => p.MistakeItemId).OrderBy(g => g.Key))
        {
            // Hold the parent through SaveChanges: a deletion must either observe this practice,
            // or finish first so that this writer validates the committed archival manifest.
            var query = db.Database.IsNpgsql()
                ? db.MistakeItems.FromSqlInterpolated($"SELECT * FROM public.mistake_item WHERE id={group.Key} FOR KEY SHARE")
                : db.MistakeItems.Where(i => i.Id == group.Key);
            var item = await query.AsNoTracking().SingleOrDefaultAsync(ct)
                ?? throw new MistakeConflictException("source_managed");
            if (!item.SourceUploadId.StartsWith("extract:", StringComparison.Ordinal) ||
                !Guid.TryParseExact(item.SourceUploadId.AsSpan(8), "N", out var jobId))
                continue;
            if (!await db.ExtractionDeletions.AnyAsync(d => d.JobId == jobId, ct)) continue;

            using var analysis = JsonDocument.Parse(item.AnalysisSnapshot ?? "{}");
            HashSet<string> allowed;
            if (analysis.RootElement.TryGetProperty("retainedFiles", out var files))
                allowed = files.EnumerateArray().Select(f => f.GetProperty("path").GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
            else
            {
                // rc.12 has no file manifest: only reuse this item's existing references.
                allowed = item.SourceRegions.Select(r => r.SourceImagePath).ToHashSet(StringComparer.Ordinal);
                foreach (var paths in await db.MistakePractices.Where(p => p.MistakeItemId == item.Id)
                    .Select(p => p.ImagePaths).ToListAsync(ct))
                    allowed.UnionWith(paths);
            }
            foreach (var path in group.SelectMany(p => p.ImagePaths))
                if (path.StartsWith("mistakes/extractions/", StringComparison.Ordinal) && !allowed.Contains(path))
                    throw new MistakeConflictException("retained_image_not_recorded");
        }
    }
}
