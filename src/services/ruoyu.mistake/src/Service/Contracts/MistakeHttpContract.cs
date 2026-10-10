using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ruoyu.Study.Mistake.HttpContracts;

/// <summary>
/// Executes the JSON Schema vocabulary used by the pinned Mistake contract.
/// It validates wire values before mirror DTO defaults can hide absent fields.
/// This library has no reference to the provider's Domain or Database.
/// </summary>
public static class MistakeHttpContract
{
    public const string Version = "mistake-http-v2";
    private static readonly byte[] Bytes = ReadBytes();
    private static readonly JsonDocument Contract = JsonDocument.Parse(Bytes);
    public static string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    public static JsonElement Definition => Contract.RootElement;

    public static void ValidateSchema(JsonElement value, JsonElement schema) => Validate(value, schema, "$", 0);

    public static void ValidateResponse(string method, string path, int status, JsonElement value)
    {
        var operation = FindOperation(method, path);
        var responses = operation.GetProperty("responses");
        if (!responses.TryGetProperty(status.ToString(CultureInfo.InvariantCulture), out var response))
            response = responses.GetProperty("default");
        if (!response.TryGetProperty("content", out var content)
            || !content.TryGetProperty("application/json", out var json))
            throw new JsonException("The pinned operation does not define a JSON response for this status.");
        ValidateSchema(value, json.GetProperty("schema"));
        // A pending managed operation cannot be reclassified as completed by HTTP success.
        if (status == 202 && value.GetProperty("data").GetProperty("success").GetBoolean())
            throw new JsonException("A pending managed operation must retain inner success=false.");
    }

    public static void ValidateRequest(string method, string path, JsonElement body)
    {
        var operation = FindOperation(method, path);
        ValidateSchema(body, operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema"));
    }

    internal static async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response,
        JsonSerializerOptions options, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new JsonException("Successful Mistake responses require the pinned JSON content type.");
        var text = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(text);
        var request = response.RequestMessage ?? throw new JsonException("Missing consumer request identity.");
        ValidateResponse(request.Method.Method, request.RequestUri!.AbsolutePath, (int)response.StatusCode, document.RootElement);
        return document.RootElement.Deserialize<T>(options);
    }

    private static JsonElement FindOperation(string method, string path)
    {
        var paths = Definition.GetProperty("paths");
        if (paths.TryGetProperty(path, out var exact) && exact.TryGetProperty(method.ToLowerInvariant(), out var direct))
            return direct;
        foreach (var candidate in paths.EnumerateObject())
        {
            var pattern = "^" + string.Join("/", candidate.Name.Split('/').Select(segment =>
                segment.StartsWith('{') && segment.EndsWith('}') ? "[^/]+" : Regex.Escape(segment))) + "$";
            if (Regex.IsMatch(path, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                && candidate.Value.TryGetProperty(method.ToLowerInvariant(), out var operation))
                return operation;
        }
        throw new JsonException("The consumer operation is absent from the pinned Mistake contract.");
    }

    private static void Validate(JsonElement value, JsonElement schema, string path, int depth)
    {
        if (depth > 64) throw new JsonException("Contract nesting exceeds its supported depth.");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = Definition;
            foreach (var part in reference.GetString()![2..].Split('/'))
                target = target.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
            Validate(value, target, path, depth + 1);
            return;
        }
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            foreach (var alternative in alternatives.EnumerateArray())
            {
                try { Validate(value, alternative, path, depth + 1); return; }
                catch (JsonException) { }
            }
            throw new JsonException($"Contract alternatives did not match at {path}.");
        }
        if (schema.TryGetProperty("const", out var constant) && !SameScalar(value, constant))
            throw new JsonException($"Contract constant differs at {path}.");
        if (schema.TryGetProperty("enum", out var members)
            && !members.EnumerateArray().Any(member => SameScalar(member, value)))
            throw new JsonException($"Unknown contract enum value at {path}.");
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => throw new JsonException("Unsupported schema type in pinned contract.")
            };
            if (!matches) throw new JsonException($"Wrong contract type at {path}.");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate contract field at {path}.");
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _))
                        throw new JsonException($"Missing required contract field at {path}.{field.GetString()}.");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in value.EnumerateObject())
                    if (properties.TryGetProperty(property.Name, out var child))
                        Validate(property.Value, child, path + "." + property.Name, depth + 1);
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) Validate(item, items, path + "[]", depth + 1);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("pattern", out var pattern)
                && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new JsonException($"Wrong contract string format at {path}.");
            if (schema.TryGetProperty("format", out var format))
            {
                var valid = format.GetString() switch
                {
                    "uuid" => Guid.TryParse(text, out _),
                    "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    _ => true
                };
                if (!valid) throw new JsonException($"Wrong contract format at {path}.");
            }
        }
    }

    private static bool SameScalar(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDecimal(out var number) && right.TryGetDecimal(out var other) && number == other,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false
        };
    }

    private static byte[] ReadBytes()
    {
        using var source = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream("mistake-http-v2.json")
            ?? throw new InvalidOperationException("Missing pinned Mistake contract resource.");
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }
}
