using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed class SourceSnapshotClient(HttpClient client, StudentServiceAuthenticationOptions identity)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<SourceManifest> AcquireAsync(Guid source, Guid revision, CancellationToken ct)
    {
        var path = $"/api/integrations/mistake/uploads/{source:D}/snapshots/{revision:D}";
        try
        {
            using var response = await client.PutAsync(path, null, ct);
            return await ReadAsync(response, source, revision, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // PUT may have committed. Only the same authenticated GET can resolve it.
            try
            {
                using var response = await client.GetAsync(path, ct);
                return await ReadAsync(response, source, revision, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            { throw new SourceIntakeException("source_dependency_unavailable", 503); }
        }
    }

    private async Task<SourceManifest> ReadAsync(HttpResponseMessage response, Guid source, Guid revision, CancellationToken ct)
    {
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Created)
        {
            var status = response.StatusCode;
            throw new SourceIntakeException(status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "source_credentials_rejected",
                HttpStatusCode.Conflict => "source_condition_conflict",
                HttpStatusCode.UnprocessableEntity => "source_unavailable",
                _ => "source_dependency_unavailable"
            }, status is HttpStatusCode.Conflict ? 409 : 503);
        }
        try
        {
            var manifest = await response.Content.ReadFromJsonAsync<SourceManifest>(Json, ct)
                ?? throw new JsonException();
            IntakeCanonical.ValidateManifest(manifest, source, revision, identity.Subject);
            return manifest;
        }
        catch (JsonException) { throw new SourceIntakeException("invalid_source_manifest", 503); }
    }
}
