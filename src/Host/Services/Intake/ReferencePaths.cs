using System.Text;
using System.Text.Json;

namespace Emenda.Host.Services.Intake;

/// <summary>
/// Extracts the object keys a Mistake row references, failing closed on any shape it does not
/// expect: a missing property, a non-string or empty key, or a key that is not valid UTF-8.
/// (These rules came from the retired StorageReferences v1 coverage query, ADR-0030.)
/// </summary>
public static class ReferencePaths
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <param name="format">
    /// <c>paths</c> (array of keys), <c>regions</c> (array of <c>sourceImagePath</c>),
    /// <c>original</c> (<c>sourceRegions</c> array of <c>sourceImagePath</c>), <c>manifest</c>
    /// (<c>images</c> array of <c>path</c>), <c>returned</c> (<c>imageEntries</c> array of <c>path</c>)
    /// or <c>targets</c> (array of <c>sourcePath</c> and <c>targetKey</c>).
    /// </param>
    public static string[] Parse(string format, string value)
    {
        using var json = JsonDocument.Parse(value);
        var root = json.RootElement;
        if (format == "manifest") root = Required(root, "images");
        if (format == "original") root = Required(root, "sourceRegions");
        if (format == "returned") root = Required(root, "imageEntries");
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Reference array required.");
        var paths = new List<string>();
        foreach (var entry in root.EnumerateArray())
        {
            switch (format)
            {
                case "paths": paths.Add(Text(entry)); break;
                case "regions": case "original": paths.Add(Text(Required(entry, "sourceImagePath"))); break;
                case "manifest": case "returned": paths.Add(Text(Required(entry, "path"))); break;
                case "targets":
                    paths.Add(Text(Required(entry, "sourcePath"))); paths.Add(Text(Required(entry, "targetKey"))); break;
                default: throw new JsonException("Unknown reference representation.");
            }
        }
        foreach (var path in paths) ValidateKey(path);
        return [.. paths];
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Invalid reference key.");
        _ = Utf8.GetByteCount(key);
    }

    private static JsonElement Required(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value))
            throw new JsonException("Required reference property missing.");
        return value;
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? throw new JsonException("Null reference key.")
        : throw new JsonException("String reference key required.");
}
