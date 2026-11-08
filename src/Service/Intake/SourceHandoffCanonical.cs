using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace Emenda.Service.Intake;

public static class SourceHandoffCanonical
{
    public const string Version = "source-handoff-v1";
    public static string Encode(params string[] values)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer, Encoding.UTF8);
        foreach (var value in values) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        return Convert.ToBase64String(buffer.ToArray());
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Payload(Guid source, Guid revision, Guid snapshot, string student, string groups,
        IEnumerable<SourceHandoffTarget> targets) => Encode(Version, $"mistake-handoff:{snapshot:D}", source.ToString("D"),
        revision.ToString("D"), snapshot.ToString("D"), student, groups, Encode(targets.OrderBy(t => t.SourcePath, StringComparer.Ordinal)
        .Select(t => Encode(t.SourcePath,t.TargetKey,t.Length.ToString(CultureInfo.InvariantCulture),t.Sha256)).ToArray()));
}
