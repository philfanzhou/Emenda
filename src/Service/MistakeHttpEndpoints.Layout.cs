using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Emenda.Service.Intake;

namespace Emenda.Service;

public static partial class MistakeHttpEndpoints
{
    // Administrative layout operations (IKQA2P) over an existing managed source
    // revision. They follow the intake family conventions: an always-authenticated
    // Admin subject with no anonymous compatibility window, managed error bodies,
    // and raw JSON views. Start delegates to the enqueue command (request-key
    // replay, conflict rejection, separate result revisions); the status read
    // stays available and returns retained results with the capability off.
    private static async Task<IResult> StartLayoutJobAsync(Guid sourceId, Guid revision,
        [FromBody] Dtos.StartLayoutJobRequest request, ClaimsPrincipal user,
        IMistakeApiResourceAuthorizationService authorization, ILayoutJobAdminService jobs, CancellationToken ct)
    {
        var error = IntakeRole(user, authorization, MistakeResourceAccessGate.AdminOnly);
        if (error != null) return error;
        if (sourceId == Guid.Empty || revision == Guid.Empty) return IntakeError("invalid_request", 400);
        if (request.RequestKey is not { } requestKey || requestKey == Guid.Empty)
            return IntakeError("invalid_request", 400);
        try
        {
            var job = await jobs.EnqueueAsync(sourceId, revision, requestKey,
                authorization.GetSubjectId(user)!, ct);
            return Results.Ok(MapLayoutJob(job));
        }
        catch (LayoutAdminException ex) { return IntakeError(ex.ErrorKind, ex.StatusCode); }
    }

    private static async Task<IResult> GetLayoutJobsAsync(Guid sourceId, Guid revision,
        ClaimsPrincipal user, IMistakeApiResourceAuthorizationService authorization,
        ILayoutJobAdminService jobs, CancellationToken ct)
    {
        var error = IntakeRole(user, authorization, MistakeResourceAccessGate.AdminOnly);
        if (error != null) return error;
        if (sourceId == Guid.Empty || revision == Guid.Empty) return IntakeError("invalid_request", 400);
        var views = await jobs.GetJobsAsync(sourceId, revision, ct);
        return Results.Ok(new { jobs = views.Select(MapLayoutJob).ToList() });
    }

    /// <summary>Projects one durable layout job onto the administrative wire view:
    /// durable per-image status, accepted results with validated regions, and the
    /// captured non-secret processing configuration. Never includes the credential,
    /// a credential value or any signed URL.</summary>
    private static object MapLayoutJob(LayoutAdminJob job) => new
    {
        jobId = job.JobId.ToString(),
        sourceUploadId = job.SourceUploadId.ToString(),
        contentRevision = job.ContentRevision.ToString(),
        snapshotId = job.SnapshotId.ToString(),
        studentId = job.StudentId,
        actor = job.Actor,
        resultRevision = job.ResultRevision,
        status = job.Status,
        config = job.Config,
        createdAt = LayoutTimestamp(job.CreatedAt),
        completedAt = job.CompletedAt is { } completed ? LayoutTimestamp(completed) : null,
        errorKind = job.ErrorKind,
        images = job.Images.Select(image => new
        {
            imagePath = image.ImagePath,
            position = image.Position,
            expectedLength = image.ExpectedLength,
            expectedSha256 = image.ExpectedSha256,
            status = image.Status,
            attempts = image.Attempts,
            lastError = image.LastError,
            retryNotBefore = image.RetryNotBefore is { } retry ? LayoutTimestamp(retry) : null
        }).ToList(),
        results = job.Results.Select(result => new
        {
            resultId = result.ResultId.ToString(),
            imagePath = result.ImagePath,
            resultRevision = result.ResultRevision,
            attempt = result.Attempt,
            upstreamRequestId = result.UpstreamRequestId,
            contractId = result.ContractId,
            coordinateFrame = result.CoordinateFrame,
            width = result.Width,
            height = result.Height,
            orientation = result.Orientation,
            engine = result.Engine,
            model = result.Model,
            version = result.Version,
            sourceRotation = result.SourceRotation,
            bytesLength = result.BytesLength,
            bytesSha256 = result.BytesSha256,
            createdAt = LayoutTimestamp(result.CreatedAt),
            diagnostics = result.Diagnostics,
            regions = result.Regions.Select(region => new
            {
                regionId = region.RegionId,
                label = region.Label,
                x0 = region.X0,
                y0 = region.Y0,
                x1 = region.X1,
                y1 = region.Y1
            }).ToList()
        }).ToList()
    };

    private static string LayoutTimestamp(DateTimeOffset value) => value == DateTimeOffset.MinValue
        ? string.Empty
        : value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
