using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private sealed record JobLease(Guid Id, Guid Owner, long Fence);
    private sealed record AcceptedLayout(Guid SourceId, LayoutDetectionResult Result);

    public async Task RunQueuedAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        JobLease lease;
        await using (var db = Open())
        await using (var tx = await db.Database.BeginTransactionAsync(stoppingToken))
        {
            var now = await NowAsync(db, stoppingToken);
            var candidate = await db.ExtractionJobs.Where(j => j.Status == "Queued" ||
                j.Status == "Running" && j.LeaseUntil < now).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(stoppingToken);
            if (candidate is null) return;
            await LockAsync(db, candidate.Id.ToString(), stoppingToken);
            await db.Entry(candidate).ReloadAsync(stoppingToken);
            if (db.Entry(candidate).State == EntityState.Detached) return;
            now = await NowAsync(db, stoppingToken);
            if (candidate.Status != "Queued" && !(candidate.Status == "Running" && candidate.LeaseUntil < now)) return;
            if (candidate.Attempt >= ExtractionRules.MaxAttempts)
            {
                candidate.Status = "Failed";
                candidate.ErrorCode = "attempts_exhausted";
                candidate.LeaseUntil = null;
                await db.SaveChangesAsync(stoppingToken);
                await tx.CommitAsync(stoppingToken);
                return;
            }
            candidate.Status = "Running";
            candidate.Attempt++;
            candidate.OwnerId = Guid.NewGuid();
            candidate.Fence++;
            candidate.LeaseUntil = now.AddSeconds(90);
            candidate.UpdatedAt = now;
            lease = new(candidate.Id, candidate.OwnerId.Value, candidate.Fence);
            await db.SaveChangesAsync(stoppingToken);
            await tx.CommitAsync(stoppingToken);
        }
        using var heartbeatStop = new CancellationTokenSource();
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseAsync(lease, processing, heartbeatStop.Token);
        try { await ProcessJobAsync(lease, processing.Token); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { await FailJobAsync(lease, "lease_lost", stoppingToken); }
        catch (ExtractionException error) { await FailJobAsync(lease, error.Code, stoppingToken); }
        catch (Layout.LayoutDetectionException error)
        { await FailJobAsync(lease, "layout_" + error.Failure.ToString().ToLowerInvariant(), stoppingToken); }
        catch (Exception error)
        {
            logger.LogWarning("Extraction failed {JobId} {ErrorType}", lease.Id, error.GetType().Name);
            await FailJobAsync(lease, "processing_failed", stoppingToken);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
        }
    }

    private async Task RenewLeaseAsync(JobLease lease, CancellationTokenSource processing, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(stop))
            {
                await using var db = Open();
                var count = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_job SET lease_until=clock_timestamp()+interval '90 seconds' WHERE id={lease.Id} AND owner_id={lease.Owner} AND fence={lease.Fence} AND status='Running' AND lease_until>clock_timestamp()", stop);
                if (count != 1) { processing.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { processing.Cancel(); }
    }

    private static async Task<ExtractionJobEntity> GetLeasedJobAsync(MistakeDbContext db, JobLease lease, CancellationToken ct)
    {
        await LockAsync(db, lease.Id.ToString(), ct);
        var job = await db.ExtractionJobs.SingleOrDefaultAsync(j => j.Id == lease.Id, ct);
        var now = await NowAsync(db, ct);
        if (job is null || job.Status != "Running" || job.OwnerId != lease.Owner || job.Fence != lease.Fence || job.LeaseUntil <= now)
            throw new ExtractionException("lease_lost", 409);
        return job;
    }

    private async Task CheckpointAsync(JobLease lease, Action<ExtractionJobEntity> change, CancellationToken ct)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await GetLeasedJobAsync(db, lease, ct);
        change(job);
        job.UpdatedAt = await NowAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ProcessJobAsync(JobLease lease, CancellationToken ct)
    {
        ExtractionJobEntity job;
        await using (var db = Open()) job = await db.ExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == lease.Id, ct)
            ?? throw new ExtractionException("lease_lost", 409);
        var sources = Sources(job);
        QuestionAnalysisResult analyzed;
        if (job.CandidateJson is not null) analyzed = ExtractionRules.Read<QuestionAnalysisResult>(job.CandidateJson);
        else
        {
            var accepted = ExtractionRules.Read<List<AcceptedLayout>>(job.LayoutJson);
            var inputs = new List<QuestionAnalysisImage>();
            // Allow 64 KiB per page for up to 256 layout boxes and JSON overhead.
            var metadataBudget = Math.Max(1024 * 1024, sources.Count * 64 * 1024);
            var previewBudget = (AiTutorQuestionClient.RequestLimit - metadataBudget) / sources.Count * 3 / 4;
            foreach (var source in sources)
            {
                var bytes = await VerifiedSourceAsync(source, ct);
                var retained = accepted.SingleOrDefault(a => a.SourceId == source.Id);
                if (retained is null)
                {
                    var image = new Layout.LayoutDetectionImage(bytes, source.ContentType);
                    LayoutDetectionResult result;
                    try
                    {
                        result = await layout.DetectLayoutAsync(image, ct);
                    }
                    catch (Layout.LayoutDetectionException error) when (error is
                        { Failure: Layout.LayoutDetectionFailure.UpstreamTimeout, HttpStatusCode: 504, ProblemCode: "layout-timeout" })
                    {
                        logger.LogWarning("Retrying layout detection once for extraction {JobId}, source {SourceId}", lease.Id, source.Id);
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        result = await layout.DetectLayoutAsync(image, ct);
                    }
                    var swaps = source.Orientation is >= 5 and <= 8;
                    if (result.Width != (swaps ? source.Height : source.Width) || result.Height != (swaps ? source.Width : source.Height) ||
                        result.Orientation.HasValue && result.Orientation != (source.Orientation ?? 1))
                        throw new ExtractionException("layout_geometry_conflict", 502);
                    retained = new(source.Id, result);
                    accepted.Add(retained);
                    await CheckpointAsync(lease, j => j.LayoutJson = ExtractionRules.Serialize(accepted), ct);
                }
                var regions = retained.Result.Regions.Where(r => r.Box.X0 < r.Box.X1 && r.Box.Y0 < r.Box.Y1)
                    .Select(r => ToDisplayRegion(source, r)).ToList();

                var preview = QuestionImage.Preview(bytes, previewBudget);
                inputs.Add(new(source.Id, "data:image/jpeg;base64," + Convert.ToBase64String(preview), source.Width, source.Height, regions));
            }
            analyzed = await analysis.AnalyzeAsync(new(job.Grade, inputs), ct);
            if (string.IsNullOrWhiteSpace(analyzed.Model) || analyzed.Model.Length > 128)
                throw new ExtractionException("invalid_analysis_response", 502);
            analyzed = analyzed with { Questions = analyzed.Questions.Select((q, index) =>
                q with { Id = ExtractionRules.Identity($"{job.Id:N}:question:{index}") }).ToList() };
            ExtractionRules.ValidateQuestions(analyzed.Questions, sources, automatic: true);
            await CheckpointAsync(lease, j => j.CandidateJson = ExtractionRules.Serialize(analyzed), ct);
        }
        ExtractionRules.ValidateQuestions(analyzed.Questions, sources, automatic: true);
        var requestKey = ExtractionRules.Identity($"{job.Id:N}:automatic");
        await using var jobLock = await SerializeJobAsync(job.Id, ct);
        await using (var check = Open())
        await using (var checkTransaction = await check.Database.BeginTransactionAsync(ct))
        {
            await GetLeasedJobAsync(check, lease, ct);
            await checkTransaction.CommitAsync(ct);
        }
        var snapshot = await BuildSnapshotAsync(job.Id, requestKey, analyzed.Questions, sources, null, ct);
        await using var publish = Open();
        await using var transaction = await publish.Database.BeginTransactionAsync(ct);
        var current = await GetLeasedJobAsync(publish, lease, ct);
        if (current.Version != 0) throw new ExtractionException("version_conflict", 409);
        await PublishRevisionAsync(publish, current, snapshot, null, requestKey,
            ExtractionRules.Hash(analyzed), "automatic", "initial_extraction", analyzed.Model, ct);
        await transaction.CommitAsync(ct);
    }

    private static ExtractionRegion ToDisplayRegion(ExtractionSource source, LayoutRegion region)
    {
        var box = region.Box;
        var points = new[]
        {
            LayoutGeometry.StoredToDisplayPoint(source.Orientation, box.X0, box.Y0),
            LayoutGeometry.StoredToDisplayPoint(source.Orientation, box.X1, box.Y0),
            LayoutGeometry.StoredToDisplayPoint(source.Orientation, box.X0, box.Y1),
            LayoutGeometry.StoredToDisplayPoint(source.Orientation, box.X1, box.Y1)
        };
        return new(source.Id, points.Min(p => p.U), points.Min(p => p.V), points.Max(p => p.U), points.Max(p => p.V));
    }

    private async Task FailJobAsync(JobLease lease, string code, CancellationToken ct)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        ExtractionJobEntity job;
        try { job = await GetLeasedJobAsync(db, lease, ct); }
        catch (ExtractionException error) when (error.Code == "lease_lost") { return; }
        job.Status = "Failed";
        job.ErrorCode = code;
        job.OwnerId = null;
        job.LeaseUntil = null;
        job.UpdatedAt = await NowAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

public sealed class ExtractionHostedService(IServiceProvider services, ILogger<ExtractionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ExtractionService>();
                await service.RunQueuedAsync(stoppingToken);
                await service.RunCleanupAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogError(error, "Extraction worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
