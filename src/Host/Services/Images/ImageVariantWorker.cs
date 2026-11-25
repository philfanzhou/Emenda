using System.Security.Cryptography;
using Emenda.Common.Oss;
using Emenda.Database;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Images;

public sealed class ImageVariantOptions
{
    public const string SectionName = "Emenda:ImageVariants";
    /// <summary>Backfill and new-image variant generation; reads still render a missing variant on demand.</summary>
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 8;
}

/// <summary>
/// Drains <c>image_variant_task</c>: every listed stored image gets all display variants. Existing data
/// was seeded by the v23 upgrade; writers enqueue new originals, supplements, crops and conversation images.
/// </summary>
public sealed class ImageVariantBacklog(DbContextOptions<MistakeDbContext> databaseOptions, IOssService oss,
    ILogger<ImageVariantBacklog> logger)
{
    private const int MaxParentBytes = 64 * 1024 * 1024;
    private const int MaxAttempts = 8;

    private MistakeDbContext Open() => new(databaseOptions);

    private sealed class Claim
    {
        public string Path { get; set; } = "";
        public int Attempts { get; set; }
        public DateTimeOffset LeaseUntil { get; set; }
    }

    /// <summary>Processes one batch; returns how many tasks were claimed.</summary>
    public async Task<int> RunOnceAsync(int batchSize, CancellationToken ct)
    {
        List<Claim> claims;
        await using (var db = Open())
        {
            // The lease keeps another instance off these paths while they are processed.
            claims = await db.Database.SqlQuery<Claim>($"""
                UPDATE image_variant_task t SET attempts=t.attempts+1, next_attempt=clock_timestamp()+interval '10 minutes'
                WHERE t.path IN (SELECT path FROM image_variant_task WHERE next_attempt<=clock_timestamp()
                    ORDER BY next_attempt, path LIMIT {batchSize} FOR UPDATE SKIP LOCKED)
                RETURNING t.path AS "Path", t.attempts AS "Attempts", t.next_attempt AS "LeaseUntil"
                """).ToListAsync(ct);
        }
        foreach (var claim in claims)
        {
            bool done;
            try { done = await EnsureAsync(claim.Path, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("Image variant generation failed {ErrorType} attempt {Attempt}", error.GetType().Name, claim.Attempts);
                done = claim.Attempts >= MaxAttempts;
            }
            await using var db = Open();
            if (done) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM image_variant_task WHERE path={claim.Path} AND next_attempt={claim.LeaseUntil} AND attempts={claim.Attempts}", ct);
            else
            {
                var delay = TimeSpan.FromSeconds(Math.Min(3600, 15 * (1 << Math.Min(claim.Attempts, 8))));
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE image_variant_task SET next_attempt=clock_timestamp()+{delay} WHERE path={claim.Path} AND next_attempt={claim.LeaseUntil} AND attempts={claim.Attempts}", ct);
            }
        }
        return claims.Count;
    }

    /// <summary>True when the task is finished (variants exist, or the parent is gone/unusable).</summary>
    internal async Task<bool> EnsureAsync(string path, CancellationToken ct)
    {
        if (!path.StartsWith("mistakes/", StringComparison.Ordinal) || ThumbnailHelper.IsThumbnailPath(path)) return true;
        if (await ImageVariants.AllExistAsync(oss, path, ct)) return true;
        if (!await oss.ObjectExistsOrThrowAsync(path, ct)) return true;
        var expected = await ExpectedDigestAsync(path, ct);
        if (expected == "") return true;
        byte[] bytes;
        await using (var stream = await oss.DownloadAsync(path).WaitAsync(ct))
        using (var memory = new MemoryStream())
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + count > MaxParentBytes) { logger.LogWarning("Image variant parent exceeds its budget"); return true; }
                memory.Write(buffer, 0, count);
            }
            bytes = memory.ToArray();
        }
        if (expected is not null && !Convert.ToHexStringLower(SHA256.HashData(bytes)).StartsWith(expected, StringComparison.Ordinal))
        {
            // Never derive display images from bytes that do not match the recorded upload.
            logger.LogWarning("Image variant parent digest mismatch");
            return true;
        }
        try { await ImageVariants.StoreAsync(oss, path, bytes, ct); }
        catch (Exception error) when (error is ImageMagick.MagickException or InvalidDataException)
        {
            logger.LogWarning("Image variant parent cannot be decoded {ErrorType}", error.GetType().Name);
        }
        return true;
    }

    /// <summary>
    /// The recorded SHA-256 (or historical 16-hex prefix) for extraction objects, null when the image has no
    /// recorded digest; an empty string means the extraction object no longer belongs to a live job.
    /// </summary>
    private async Task<string?> ExpectedDigestAsync(string path, CancellationToken ct)
    {
        var parts = path.Split('/');
        if (parts.Length < 4 || parts[1] != "extractions") return null;
        if (parts.Contains("versions"))
        {
            // Crop keys end in "-{sha256}.png"; historical keys carry the first 16 hex digits.
            var name = Path.GetFileNameWithoutExtension(parts[^1]);
            var dash = name.LastIndexOf('-');
            var named = dash >= 0 ? name[(dash + 1)..] : "";
            return named.Length is 64 or 16 && named.All(char.IsAsciiHexDigitLower) ? named : "";
        }
        if (!Guid.TryParseExact(parts[2], "N", out var jobId)) return "";
        await using var db = Open();
        var digest = await db.Database.SqlQuery<string>($"""
            SELECT s->>'sha256' AS "Value" FROM extraction_job j CROSS JOIN LATERAL jsonb_array_elements(j.sources_json) s
            WHERE j.id={jobId} AND s->>'path'={path}
            UNION ALL
            SELECT s->>'sha256' FROM extraction_supplement x JOIN extraction_job j ON j.id=x.job_id
                CROSS JOIN LATERAL jsonb_array_elements(x.sources_json) s
            WHERE x.job_id={jobId} AND s->>'path'={path}
            """).FirstOrDefaultAsync(ct);
        return digest ?? "";
    }
}

public sealed class ImageVariantWorker(IServiceProvider services, ImageVariantOptions options,
    ILogger<ImageVariantWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using (var scope = services.CreateScope())
            if (!scope.ServiceProvider.GetRequiredService<MistakeDbContext>().Database.IsRelational()) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var backlog = scope.ServiceProvider.GetRequiredService<ImageVariantBacklog>();
                var claimed = await backlog.RunOnceAsync(Math.Clamp(options.BatchSize, 1, 64), stoppingToken);
                await Task.Delay(claimed == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(200), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogWarning("Image variant worker iteration failed {ErrorType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
