using System.Security.Cryptography;
using Emenda.Common.Oss;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private async Task<byte[]> ReadObjectAsync(string path, int limit, CancellationToken ct)
    {
        var pending = oss.DownloadAsync(path);
        Stream stream;
        try { stream = await pending.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); }, TaskScheduler.Default);
            throw;
        }
        await using (stream)
        {
            if (stream.CanSeek && stream.Length > limit) throw new ExtractionException("image_too_large", 503);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, ct);
                if (count == 0) return memory.ToArray();
                if (memory.Length + count > limit) throw new ExtractionException("image_too_large", 503);
                await memory.WriteAsync(buffer.AsMemory(0, count), ct);
            }
        }
    }

    private async Task<byte[]> VerifiedSourceAsync(ExtractionSource source, CancellationToken ct)
    {
        var bytes = await ReadObjectAsync(source.Path, ExtractionRules.MaxImageBytes, ct);
        if (bytes.LongLength != source.Length || ExtractionRules.Hash(bytes) != source.Sha256)
            throw new ExtractionException("source_bytes_conflict", 503);
        return bytes;
    }

    private async Task StoreAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        if (!await oss.ObjectExistsAsync(path).WaitAsync(ct))
        {
            var split = path.LastIndexOf('/');
            var folder = path["mistakes/".Length..split];
            var actual = await oss.UploadAsync(bytes, path[(split + 1)..], contentType, OssBucket.Mistakes, folder).WaitAsync(ct);
            if (actual != path) throw new ExtractionException("storage_path_conflict", 503);
        }
        var stored = await ReadObjectAsync(path, ExtractionRules.MaxCropBytes, ct);
        if (stored.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stored), SHA256.HashData(bytes)))
            throw new ExtractionException("storage_bytes_conflict", 503);
    }

    public async Task<(byte[] Bytes, string ContentType)> ReadSourceAsync(ExtractionAccess access, Guid id,
        Guid sourceId, CancellationToken ct = default)
    {
        await using var db = Open();
        var job = await FindAsync(db, access, id, ct);
        var source = Sources(job).SingleOrDefault(s => s.Id == sourceId) ?? throw new ExtractionException("not_found", 404);
        return (await VerifiedSourceAsync(source, ct), source.ContentType);
    }

    public async Task<(byte[] Bytes, string ContentType)> ReadCropAsync(ExtractionAccess access, Guid id,
        Guid questionId, int version, int index, CancellationToken ct = default)
    {
        var snapshot = await GetVersionAsync(access, id, version, ct);
        var question = snapshot.Questions.SingleOrDefault(q => q.Question.Id == questionId)
            ?? throw new ExtractionException("not_found", 404);
        if (index < 0 || index >= question.Images.Count) throw new ExtractionException("not_found", 404);
        var crop = question.Images[index];
        var bytes = await ReadObjectAsync(crop.Path, ExtractionRules.MaxCropBytes, ct);
        if (bytes.LongLength != crop.Length || ExtractionRules.Hash(bytes) != crop.Sha256)
            throw new ExtractionException("crop_bytes_conflict", 503);
        return (bytes, "image/png");
    }

    private async Task<ExtractionSnapshot> BuildSnapshotAsync(Guid jobId, Guid requestKey,
        List<ExtractionQuestionDraft> drafts, List<ExtractionSource> sources, ExtractionSnapshot? prior, CancellationToken ct)
    {
        var result = new List<ExtractionQuestionView>();
        foreach (var draft in drafts)
        {
            var old = prior?.Questions.SingleOrDefault(q => q.Question.Id == draft.Id);
            if (old is not null && old.Question.Regions.SequenceEqual(draft.Regions))
            {
                result.Add(old with { Question = draft });
                continue;
            }
            var crops = new List<ExtractionCrop>();
            foreach (var region in draft.Regions)
            {
                var original = sources.Single(s => s.Id == region.SourceId);
                var bytes = await VerifiedSourceAsync(original, ct);
                var crop = QuestionImage.Crop(bytes, region.X0, region.Y0, region.X1, region.Y1);
                if (crop.Bytes.Length > ExtractionRules.MaxCropBytes) throw new ExtractionException("crop_too_large", 413);
                var hash = ExtractionRules.Hash(crop.Bytes);
                var key = $"mistakes/extractions/{jobId:N}/versions/{requestKey:N}/{draft.Id:N}/{crops.Count}-{hash[..16]}.png";
                await StoreAsync(key, crop.Bytes, "image/png", ct);
                crops.Add(new(key, crop.Width, crop.Height, region.SourceId, region, crop.Bytes.LongLength, hash));
            }
            result.Add(new(draft, old?.ItemId, old?.ItemDeleted ?? false, crops));
        }
        return new(result);
    }
}
