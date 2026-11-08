using System.Text.Json.Serialization;
namespace Emenda.Service.Intake;

public sealed class SourceManifest
{
    [JsonRequired] public Guid SnapshotId { get; init; }
    [JsonRequired] public Guid SourceUploadId { get; init; }
    [JsonRequired] public string StudentId { get; init; } = "";
    [JsonRequired] public Guid ContentRevision { get; init; }
    [JsonRequired] public DateTimeOffset AcquiredAt { get; init; }
    [JsonRequired] public string AcquiredBy { get; init; } = "";
    [JsonRequired] public List<ManifestImage> Images { get; init; } = [];
    [JsonRequired] public List<int> ImageRotations { get; init; } = [];
}
