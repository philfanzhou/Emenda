using System.Text.Json.Serialization;
namespace Emenda.Service.Intake;

public sealed class ManifestImage
{
    [JsonRequired] public string Path { get; init; } = "";
    [JsonRequired] public string Type { get; init; } = "";
    [JsonRequired] public long Length { get; init; }
    [JsonRequired] public string Sha256 { get; init; } = "";
}
