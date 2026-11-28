using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private sealed record RetainedFile(string Path, string ContentType, long Length, string Sha256);

    private async Task RetainFilesAsync(MistakeDbContext db, ExtractionJobEntity job,
        List<MistakeItemEntity> retained, CancellationToken ct)
    {
        if (retained.Count == 0) return;
        // Keep exact object identities before the manifests disappear, including older crop names.
        // The manifest is kept in source_detail.retainedFiles, which no HTTP response exposes.
        var sources = Sources(job);
        foreach (var json in await db.ExtractionSupplements.Where(s => s.JobId == job.Id).Select(s => s.SourcesJson).ToListAsync(ct))
            sources.AddRange(Payload.Read<List<ExtractionSource>>(json));
        var files = sources.Select(s => new RetainedFile(s.Path, s.ContentType, s.Length, s.Sha256))
            .DistinctBy(f => f.Path, StringComparer.Ordinal).ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var json in await db.ExtractionRevisions.Where(r => r.JobId == job.Id).Select(r => r.SnapshotJson).ToListAsync(ct))
            foreach (var crop in Payload.Read<ExtractionSnapshot>(json).Questions.SelectMany(q => q.Images))
                files[crop.Path] = new(crop.Path, "image/png", crop.Length, crop.Sha256);
        foreach (var item in retained)
        {
            var paths = item.SourceRegions.Select(r => r.SourceImagePath)
                .Where(p => p.StartsWith(JobStoragePrefix(job.Id), StringComparison.Ordinal)).Distinct(StringComparer.Ordinal);
            var manifest = new List<RetainedFile>();
            foreach (var path in paths)
            {
                if (!files.TryGetValue(path, out var file))
                {
                    if (!IsJobObjectPath(job.Id, path)) throw new ExtractionException("retained_image_metadata_unavailable", 409);
                    var image = await ReadLegacyFileAsync(path, ct);
                    file = new(path, image.ContentType, image.Bytes.LongLength, Payload.HashBytes(image.Bytes));
                    files.Add(path, file);
                }
                manifest.Add(file);
            }
            MistakeSource.SetRetainedFiles(item, System.Text.Json.JsonSerializer.SerializeToNode(manifest, Payload.Json)!);
        }
    }
}
