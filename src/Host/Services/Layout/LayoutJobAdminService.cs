using Emenda.Service.Intake;

namespace Emenda.Host.Services.Layout;

/// <summary>
/// Service-project administrative surface adapter (IKQA2P): delegates to the enqueue
/// command and durable read views of the layout job family (IKQA2O), projecting onto
/// the administrative wire views. The credential reference stays a configuration key
/// name inside the captured non-secret configuration; no credential value, image bytes
/// or signed URL ever cross this boundary.
/// </summary>
public sealed class LayoutJobAdminService(ILayoutJobService jobs) : ILayoutJobAdminService
{
    public async Task<LayoutAdminJob> EnqueueAsync(Guid sourceId, Guid revision, Guid requestKey, string actor,
        CancellationToken ct = default)
    {
        try
        {
            return Map(await jobs.EnqueueAsync(sourceId, revision, requestKey, actor, ct));
        }
        catch (LayoutJobException ex)
        {
            throw new LayoutAdminException(ex.ErrorKind, ex.StatusCode);
        }
    }

    public async Task<List<LayoutAdminJob>> GetJobsAsync(Guid sourceId, Guid revision, CancellationToken ct = default) =>
        (await jobs.GetJobsAsync(sourceId, revision, ct)).Select(Map).ToList();

    private static LayoutAdminJob Map(LayoutJobView job) => new(job.JobId, job.SourceUploadId, job.ContentRevision,
        job.SnapshotId, job.StudentId, job.Actor, job.ResultRevision, job.Status, job.Config, job.CreatedAt,
        job.CompletedAt, job.ErrorKind,
        job.Images.Select(image => new LayoutAdminImage(image.ImagePath, image.Position, image.ExpectedLength,
            image.ExpectedSha256, image.Status, image.Attempts, image.LastError, image.RetryNotBefore)).ToList(),
        job.Results.Select(result => new LayoutAdminResult(result.ResultId, result.ImagePath, result.ResultRevision,
            result.Attempt, result.UpstreamRequestId, result.ContractId, result.CoordinateFrame, result.Width,
            result.Height, result.Orientation, result.Engine, result.Model, result.Version, result.SourceRotation,
            result.BytesLength, result.BytesSha256, result.CreatedAt, result.Diagnostics,
            result.Regions.Select(region => new LayoutAdminRegion(region.RegionId, region.Label,
                region.X0, region.Y0, region.X1, region.Y1)).ToList())).ToList());
}
