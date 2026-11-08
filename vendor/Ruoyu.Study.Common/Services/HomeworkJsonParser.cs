using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ruoyu.Study.Common.Services;

/// <summary>
/// Shared safe JSON parser and normalizer for the User and Teacher Portal Homework BFFs.
///
/// Security model:
/// - The top-level JSON value must be an object; otherwise parsing is invalid.
/// - Logical fields are collected case-insensitively and conflicting case variants are rejected.
/// - Untrusted nodes are read only through non-throwing TryGetValue checks.
/// - A new outbound object is built with one camelCase property per logical field.
/// - Present fields must match their declared type; JSON null is not implicitly accepted.
///
/// Parsing flow:
/// 1. Validate the top-level object.
/// 2. Collect logical fields case-insensitively.
/// 3. Reject conflicting duplicate variants.
/// 4. Validate the selected value type.
/// 5. Build a canonical outbound object.
/// 6. Let the caller authorize and forward the canonical request.
/// </summary>
public static class HomeworkJsonParser
{
    /// <summary>
    /// Parse result. A false IsValid value means the caller should return HOMEWORK_INVALID_REQUEST.
    /// </summary>
    public sealed class ParseResult
    {
        public bool IsValid { get; set; }
        public JsonObject? Normalized { get; set; }
        public string? StudentId { get; set; }
    }

    /// <summary>
    /// Supported field type constraints.
    /// </summary>
    public enum FieldType
    {
        String,
        Int,
        Decimal,
        StringArray,
    }

    /// <summary>
    /// Defines a canonical camelCase field name and its required type.
    /// </summary>
    public sealed class FieldDefinition
    {
        public string CanonicalName { get; }
        public FieldType Type { get; }

        public FieldDefinition(string canonicalName, FieldType type)
        {
            CanonicalName = canonicalName;
            Type = type;
        }
    }

    /// <summary>
    /// Parses and normalizes a Homework request body.
    /// </summary>
    /// <param name="body">Raw request body.</param>
    /// <param name="fieldDefinitions">Fields to extract and validate.</param>
    /// <param name="extractStudentId">Whether to expose studentId for authorization.</param>
    /// <returns>The parse result; IsValid=false indicates an invalid request.</returns>
    public static ParseResult Parse(
        string? body,
        IReadOnlyList<FieldDefinition> fieldDefinitions,
        bool extractStudentId = true)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Invalid();

        JsonObject raw;
        try
        {
            var node = JsonNode.Parse(body);
            // Arrays, strings, numbers, and null are invalid top-level values.
            if (node is not JsonObject obj)
                return Invalid();
            raw = obj;
        }
        catch (JsonException)
        {
            return Invalid();
        }

        return ParseRaw(raw, fieldDefinitions, extractStudentId);
    }

    /// <summary>
    /// Normalizes an already parsed object. The caller is responsible for the top-level check.
    /// </summary>
    public static ParseResult ParseRaw(
        JsonObject raw,
        IReadOnlyList<FieldDefinition> fieldDefinitions,
        bool extractStudentId = true)
    {
        var normalized = new JsonObject();
        string? studentId = null;

        // Index all raw keys by their lowercase form to detect case-duplicate logical fields.
        var byLower = new Dictionary<string, List<KeyValuePair<string, JsonNode?>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw)
        {
            var lower = kv.Key.ToLowerInvariant();
            if (!byLower.TryGetValue(lower, out var list))
            {
                list = new List<KeyValuePair<string, JsonNode?>>();
                byLower[lower] = list;
            }
            list.Add(kv);
        }

        foreach (var field in fieldDefinitions)
        {
            if (!byLower.TryGetValue(field.CanonicalName.ToLowerInvariant(), out var variants))
                continue;

            // Detect case-duplicate conflicts: same logical field under multiple casings with conflicting values.
            if (HasConflict(variants, field.Type))
                return Invalid();

            // Prefer the exact-case canonical key, even when its JSON value is null.
            var exactIndex = -1;
            for (var index = 0; index < variants.Count; index++)
            {
                if (string.Equals(variants[index].Key, field.CanonicalName, StringComparison.Ordinal))
                {
                    exactIndex = index;
                    break;
                }
            }

            var node = exactIndex >= 0 ? variants[exactIndex].Value : variants[0].Value;

            // A present field must match its declared type; JSON null is not a typed value.
            if (node == null)
                return Invalid();

            switch (field.Type)
            {
                case FieldType.String:
                    if (!TryGetString(node, out var strVal))
                        return Invalid();
                    normalized[field.CanonicalName] = strVal;
                    break;

                case FieldType.Int:
                    if (!TryGetInt(node, out var intVal))
                        return Invalid();
                    normalized[field.CanonicalName] = intVal;
                    break;

                case FieldType.Decimal:
                    if (!TryGetDecimal(node, out var decimalVal))
                        return Invalid();
                    normalized[field.CanonicalName] = decimalVal;
                    break;

                case FieldType.StringArray:
                    if (!TryGetStringArray(node, out var arrVal))
                        return Invalid();
                    normalized[field.CanonicalName] = new JsonArray(arrVal.Select(v => JsonValue.Create(v)!).ToArray());
                    break;
            }

            if (extractStudentId && string.Equals(field.CanonicalName, "studentId", StringComparison.Ordinal))
            {
                studentId = TryGetString(node, out var parsedStudentId)
                    ? parsedStudentId
                    : null;
            }
        }

        return new ParseResult { IsValid = true, Normalized = normalized, StudentId = studentId };
    }

    /// <summary>
    /// Detects conflicting case variants of the same logical field.
    /// </summary>
    private static bool HasConflict(IReadOnlyList<KeyValuePair<string, JsonNode?>> variants, FieldType type)
    {
        if (variants.Count <= 1)
            return false;

        var first = variants[0].Value;
        for (var index = 1; index < variants.Count; index++)
        {
            if (!AreEquivalent(first, variants[index].Value, type))
                return true;
        }

        return false;
    }

    private static bool AreEquivalent(JsonNode? left, JsonNode? right, FieldType type)
    {
        if (left == null || right == null)
            return left == null && right == null;

        return type switch
        {
            FieldType.String =>
                TryGetString(left, out var leftString)
                && TryGetString(right, out var rightString)
                && string.Equals(leftString, rightString, StringComparison.Ordinal),
            FieldType.Int =>
                TryGetInt(left, out var leftInt)
                && TryGetInt(right, out var rightInt)
                && leftInt == rightInt,
            FieldType.Decimal =>
                TryGetDecimal(left, out var leftDecimal)
                && TryGetDecimal(right, out var rightDecimal)
                && leftDecimal == rightDecimal,
            FieldType.StringArray =>
                TryGetStringArray(left, out var leftArray)
                && TryGetStringArray(right, out var rightArray)
                && leftArray.SequenceEqual(rightArray, StringComparer.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Safely reads a string through <c>JsonValue.TryGetValue&lt;T&gt;()</c>.
    /// Untrusted values are never converted through a throwing GetValue call.
    /// </summary>
    private static bool TryGetString(JsonNode node, out string value)
    {
        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var stringValue)
            && stringValue is not null)
        {
            value = stringValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Safely reads an integer through <c>JsonValue.TryGetValue&lt;T&gt;()</c>.
    /// Non-integer JSON values return false.
    /// </summary>
    private static bool TryGetInt(JsonNode node, out int value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetDecimal(JsonNode node, out decimal value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<decimal>(out var decimalValue))
        {
            value = decimalValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads a string array only when every element is a JSON string.
    /// </summary>
    private static bool TryGetStringArray(JsonNode node, out List<string> value)
    {
        if (node is JsonArray arr)
        {
            var result = new List<string>();
            foreach (var item in arr)
            {
                // Every element must be a string; numbers, objects, nulls → invalid.
                if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var stringValue))
                {
                    result.Add(stringValue);
                }
                else
                {
                    value = new List<string>();
                    return false;
                }
            }
            value = result;
            return true;
        }
        value = new List<string>();
        return false;
    }

    private static ParseResult Invalid() => new() { IsValid = false };

    /// <summary>
    /// Serializes a normalized object as UTF-8 JSON content.
    /// </summary>
    public static StringContent ToJsonContent(JsonObject json)
    {
        var content = new StringContent(json.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }
}
