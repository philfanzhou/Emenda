using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService(
    DbContextOptions<MistakeDbContext> databaseOptions, IOssService oss,
    IQuestionAnalysisClient analysis, Layout.ILayoutDetectionClient layout,
    IOptions<ExtractionOptions> options, ILogger<ExtractionService> logger) : IExtractionService
{
    private const string AdministratorOwner = "admin:";
    private static string Owner(ExtractionAccess access) => access.Administrator ? AdministratorOwner : "caller:" + access.Caller;

    private MistakeDbContext Open() => new(databaseOptions);
    private static Task LockAsync(MistakeDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ExtractionRules.LockKey(key)})", ct);
    private static Task<DateTimeOffset> NowAsync(MistakeDbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    private static List<ExtractionSource> Sources(ExtractionJobEntity job) =>
        ExtractionRules.Read<List<ExtractionSource>>(job.SourcesJson);

    private static async Task<ExtractionJobEntity> FindAsync(MistakeDbContext db, ExtractionAccess access,
        Guid id, CancellationToken ct)
    {
        var job = await db.ExtractionJobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || !access.Administrator && job.Caller != Owner(access))
            throw new ExtractionException("not_found", 404);
        return job;
    }

    public async Task<ExtractionJobView> SubmitAsync(ExtractionAccess access, ExtractionUpload upload, CancellationToken ct = default)
    {
        ExtractionRules.ValidateUpload(upload);
        if (string.IsNullOrWhiteSpace(access.Caller)) throw new ExtractionException("caller_required", 401);
        var owner = Owner(access);
        var identity = upload.Images.Select(i => new { i.ContentType, Hash = ExtractionRules.Hash(i.Content) }).ToList();
        var hash = ExtractionRules.Hash(new { upload.StudentId, upload.Grade, Images = identity });
        Guid jobId;
        await using (var db = Open())
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(db, $"extraction-source:{owner}:{upload.SourceKey}", ct);
            var existing = await db.ExtractionJobs.SingleOrDefaultAsync(j => j.Caller == owner && j.SourceKey == upload.SourceKey, ct);
            if (existing is not null)
            {
                if (existing.PayloadHash != hash) throw new ExtractionException("source_conflict", 409);
                jobId = existing.Id;
                if (existing.Status != "Staging") return await ViewAsync(db, existing, null, ct);
            }
            else
            {
                if (!options.Value.Enabled) throw new ExtractionException("extraction_disabled", 503);
                jobId = Guid.NewGuid();
                var originals = new List<ExtractionSource>();
                for (var i = 0; i < upload.Images.Count; i++)
                {
                    if (originals.Any(s => s.Sha256 == identity[i].Hash && s.ContentType == identity[i].ContentType)) continue;
                    QuestionImage.Metadata metadata;
                    try { metadata = QuestionImage.Inspect(upload.Images[i].Content, upload.Images[i].ContentType); }
                    catch (Exception error) when (error is ArgumentException or ImageMagick.MagickException or InvalidDataException)
                    { throw new ExtractionException("invalid_image"); }
                    var sourceId = Guid.NewGuid();
                    var extension = upload.Images[i].ContentType == "image/png" ? "png" : "jpg";
                    originals.Add(new(sourceId, $"mistakes/extractions/{jobId:N}/originals/{sourceId:N}.{extension}",
                        upload.Images[i].ContentType, upload.Images[i].Content.Length, identity[i].Hash,
                        metadata.DisplayWidth, metadata.DisplayHeight, metadata.Orientation));
                }
                var now = await NowAsync(db, ct);
                db.ExtractionJobs.Add(new() { Id = jobId, Caller = owner, SourceKey = upload.SourceKey,
                    PayloadHash = hash, StudentId = upload.StudentId, Grade = upload.Grade,
                    SourcesJson = ExtractionRules.Serialize(originals), CreatedAt = now, UpdatedAt = now, Status = "Staging" });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        if (!options.Value.Enabled) throw new ExtractionException("extraction_disabled", 503);
        // The immutable payload binding survives storage failure or a lost upload response.
        await using var staged = Open();
        var job = await FindAsync(staged, access, jobId, ct);
        foreach (var source in Sources(job))
        {
            var index = identity.FindIndex(i => i.Hash == source.Sha256 && i.ContentType == source.ContentType);
            await StoreAsync(source.Path, upload.Images[index].Content, source.ContentType, ct);
        }
        await using var queue = Open();
        await using var queued = await queue.Database.BeginTransactionAsync(ct);
        await LockAsync(queue, jobId.ToString(), ct);
        var current = await FindAsync(queue, access, jobId, ct);
        if (current.Status == "Staging")
        {
            current.Status = "Queued";
            current.UpdatedAt = await NowAsync(queue, ct);
            await queue.SaveChangesAsync(ct);
        }
        await queued.CommitAsync(ct);
        return await ViewAsync(queue, current, null, ct);
    }

    public async Task<List<ExtractionJobView>> ListAsync(ExtractionAccess access, string? studentId, int page = 1, CancellationToken ct = default)
    {
        if (page is < 1 or > 100000 || studentId?.Length > 36) throw new ExtractionException("invalid_request");
        await using var db = Open();
        var owner = Owner(access);
        var query = db.ExtractionJobs.Where(j => access.Administrator || j.Caller == owner);
        if (!string.IsNullOrWhiteSpace(studentId)) query = query.Where(j => j.StudentId == studentId);
        var jobs = await query.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id).Skip((page - 1) * 20).Take(20).ToListAsync(ct);
        var views = new List<ExtractionJobView>();
        foreach (var job in jobs) views.Add(await ViewAsync(db, job, null, ct));
        return views;
    }

    public async Task<ExtractionJobView> GetAsync(ExtractionAccess access, Guid id, CancellationToken ct = default)
    {
        await using var db = Open();
        return await ViewAsync(db, await FindAsync(db, access, id, ct), null, ct);
    }

    public async Task<ExtractionSnapshot> GetVersionAsync(ExtractionAccess access, Guid id, int version, CancellationToken ct = default)
    {
        await using var db = Open();
        var job = await FindAsync(db, access, id, ct);
        if (version != job.Version) throw new ExtractionException("version_retired", 410);
        var row = await db.ExtractionRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.JobId == id && r.Version == version, ct)
            ?? throw new ExtractionException("version_not_found", 404);
        return await WithDeletionStateAsync(db, ExtractionRules.Read<ExtractionSnapshot>(row.SnapshotJson), ct);
    }

    private static async Task<ExtractionSnapshot> WithDeletionStateAsync(MistakeDbContext db, ExtractionSnapshot snapshot, CancellationToken ct,
        bool includeSupplements = false)
    {
        var ids = snapshot.Questions.Where(q => q.ItemId.HasValue).Select(q => q.ItemId!.Value).ToArray();
        var existing = (await db.MistakeItems.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
        var questionIds = snapshot.Questions.Select(q => q.Question.Id).ToArray();
        var pending = includeSupplements ? await db.ExtractionSupplements.AsNoTracking()
            .Where(s => questionIds.Contains(s.QuestionId) && s.Status == "Requested").ToListAsync(ct) : [];
        return new(snapshot.Questions.Select(q =>
        {
            var current = q.ItemId.HasValue && !existing.Contains(q.ItemId.Value) ? q with { ItemDeleted = true, Images = [] } : q;
            var supplement = pending.SingleOrDefault(s => s.QuestionId == q.Question.Id);
            return current with { Supplement = supplement is null ? null : SupplementView(supplement) };
        }).ToList());
    }

    private static async Task<ExtractionJobView> ViewAsync(MistakeDbContext db, ExtractionJobEntity job, int? version, CancellationToken ct)
    {
        var selected = version ?? job.Version;
        var snapshot = selected == 0 ? new ExtractionSnapshot([]) : ExtractionRules.Read<ExtractionSnapshot>(
            (await db.ExtractionRevisions.AsNoTracking().SingleAsync(r => r.JobId == job.Id && r.Version == selected, ct)).SnapshotJson);
        snapshot = selected == job.Version ? await WithDeletionStateAsync(db, snapshot, ct, includeSupplements: true) : new([]);
        var versions = await db.ExtractionRevisions.AsNoTracking().Where(r => r.JobId == job.Id).OrderByDescending(r => r.Version)
            .Select(r => new ExtractionVersionView(r.Version, r.RequestKey, r.Actor, r.Reason, r.Model, r.CreatedAt)).ToListAsync(ct);
        return new(job.Id, job.StudentId, job.Grade, job.Status, selected, job.Attempt, job.ErrorCode,
            job.CreatedAt, job.UpdatedAt, Sources(job), snapshot.Questions, versions) { Superseded = selected != job.Version };
    }

    public async Task<ExtractionJobView> RetryAsync(ExtractionAccess access, Guid id, ExtractionRetryRequest request, CancellationToken ct = default)
    {
        if (request.RequestKey == Guid.Empty) throw new ExtractionException("invalid_request");
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id.ToString(), ct);
        var job = await FindAsync(db, access, id, ct);
        var keys = ExtractionRules.Read<List<Guid>>(job.RetryKeysJson);
        if (!keys.Contains(request.RequestKey))
        {
            if (!options.Value.Enabled) throw new ExtractionException("extraction_disabled", 503);
            if (job.Status != "Failed" || job.Version != 0) throw new ExtractionException("retry_not_available", 409);
            if (job.Attempt >= ExtractionRules.MaxAttempts) throw new ExtractionException("attempts_exhausted", 409);
            keys.Add(request.RequestKey);
            job.RetryKeysJson = ExtractionRules.Serialize(keys);
            job.LastRetryKey = request.RequestKey;
            job.Status = "Queued";
            job.ErrorCode = null;
            job.UpdatedAt = await NowAsync(db, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await ViewAsync(db, job, null, ct);
    }
}
