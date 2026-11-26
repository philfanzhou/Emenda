using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Host.Security;
using Emenda.Host.Services.Extraction;
using Emenda.Service;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host;

/// <summary>
/// One authenticated read for every picture a mistake shows, at any display size. Extraction crops are
/// read through the owning job (digest-verified), conversation evidence through its caller-scoped path
/// and legacy images directly; no storage URL is exposed.
/// </summary>
public static class MistakeImageEndpoints
{
    private const int MaxLegacyBytes = 15 * 1024 * 1024;

    public static WebApplication MapMistakeImageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/mistakes/{id:guid}/images/{index:int}", ReadImageAsync);
        return app;
    }

    private static async Task<IResult> ReadImageAsync(HttpContext http, Guid id, int index, string? size,
        MistakeDbContext db, IOssService oss, ExtractionService extraction, ILoggerFactory loggers, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        var caller = http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey] as string;
        if (string.IsNullOrWhiteSpace(caller)) return Error("caller_required", 401);
        if (ImageVariants.Normalize(size) is not { } display) return Error("invalid_size", 400);
        try
        {
            // The scoped context hides conversation items of other callers.
            var item = await db.MistakeItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
            if (item is null || index < 0 || index >= item.SourceRegions.Count) return Error("not_found", 404);
            var path = item.SourceRegions[index].SourceImagePath ?? "";
            if (item.SourceKind == MistakeSource.Extraction)
            {
                if (MistakeSource.ExtractionJobId(item) is not { } jobId) return Error("not_found", 404);
                var access = new ExtractionAccess(caller, caller);
                ExtractionJobView job;
                try { job = await extraction.GetAsync(access, jobId, ct); }
                catch (ExtractionException missing) when (missing.Status == 404)
                {
                    // A confirmed mistake outlives its deleted task; its crops are read from the retained archive.
                    return File(await extraction.ReadRetainedCropAsync(access, id, index, display, ct));
                }
                var question = job.Questions.SingleOrDefault(q => q.ItemId == id && !q.ItemDeleted);
                var crop = question?.Images.FindIndex(i => i.Path == path) ?? -1;
                if (question is null || crop < 0) return Error("not_found", 404);
                return File(await extraction.ReadCropAsync(access, jobId, question.Question.Id, job.Version, crop, display, ct));
            }
            if (!path.StartsWith("mistakes/", StringComparison.Ordinal) || path.StartsWith("mistakes/extractions/", StringComparison.Ordinal)
                || ThumbnailHelper.IsThumbnailPath(path))
                return Error("not_found", 404);
            if (path.StartsWith("mistakes/conversation/", StringComparison.Ordinal)
                && !MistakeHttpEndpoints.IsCanonicalConversationObjectPath(path, caller))
                return Error("conversation_image_forbidden", 403);
            if (!await oss.ObjectExistsAsync(path).WaitAsync(ct)) return Error("not_found", 404);
            Task<byte[]> Parent(CancellationToken token) => ReadAsync(oss, path, token);
            if (display == ImageVariants.Full) return File((await Parent(ct), MediaType(path)));
            return File((await ImageVariants.ReadOrCreateAsync(oss, path, display, Parent, ct), ImageVariants.ContentType));
        }
        catch (ExtractionException error) { return Error(error.Code, error.Status); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is ImageMagick.MagickException or InvalidDataException or IOException)
        {
            loggers.CreateLogger("MistakeImages").LogWarning("Mistake image unavailable {ErrorType}", error.GetType().Name);
            return Error("image_unavailable", 503);
        }
    }

    private static async Task<byte[]> ReadAsync(IOssService oss, string path, CancellationToken ct)
    {
        await using var stream = await oss.DownloadAsync(path).WaitAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + count > MaxLegacyBytes) throw new InvalidDataException("Image exceeds its size budget.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    // The item address is stable while a correction can replace its picture, so responses are not cached.
    private static IResult File((byte[] Bytes, string ContentType) image) => Results.File(image.Bytes, image.ContentType);

    // Same error envelope as the rest of the /api/mistakes namespace.
    private static IResult Error(string code, int status) => Results.Json(new { success = false, errorKind = code }, statusCode: status);
}
