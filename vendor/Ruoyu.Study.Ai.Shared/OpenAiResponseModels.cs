using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruoyu.Study.Common.Ai;

/// <summary>
/// SSE stream chunk. Each "data: {...}" line deserializes to this shape.
/// Only delta.content is accumulated; other fields (finish_reason, usage) are ignored.
/// Used by streaming clients (SSE). The only streaming consumer (DocLibrary LLM document
/// analysis) was extracted to the external Doctheca repo (ADR-0012); models retained for the
/// streaming capability.
/// </summary>
public record OpenAiStreamChunk
{
    [JsonPropertyName("choices")]
    public List<OpenAiStreamChoice>? Choices { get; init; }
}

public record OpenAiStreamChoice
{
    [JsonPropertyName("delta")]
    public OpenAiStreamDelta? Delta { get; init; }
}

public record OpenAiStreamDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

/// <summary>
/// Non-streaming API response. Used by non-streaming clients (e.g. mistake VL image analysis).
/// MessageContentConverter handles both string and array formats for the content field,
/// since some VL models return content as a plain string, others as an array of content parts.
/// </summary>
public class OpenAiApiResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChoice>? Choices { get; set; }
}

public class OpenAiChoice
{
    [JsonPropertyName("message")]
    public OpenAiMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class OpenAiMessage
{
    [JsonPropertyName("content")]
    [JsonConverter(typeof(MessageContentConverter))]
    public string? Content { get; set; }
}

/// <summary>
/// Handles both string and array formats for the message content field.
/// Some VL models return content as a plain string, others as an array of content parts.
/// </summary>
public class MessageContentConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var doc = JsonDocument.ParseValue(ref reader);

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                // TryGetProperty throws InvalidOperationException for non-Object elements.
                // Skip plain strings here; they are handled by the third pass below.
                if (element.ValueKind != JsonValueKind.Object) continue;

                if (element.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() == "text")
                {
                    if (element.TryGetProperty("text", out var textProp))
                    {
                        var text = textProp.GetString();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                    if (element.TryGetProperty("content", out var contentProp))
                    {
                        var text = contentProp.GetString();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                if (element.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String)
                {
                    var text = textProp.GetString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }

            var texts = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var text = element.GetString();
                    if (!string.IsNullOrEmpty(text)) texts.Add(text);
                }
            }

            if (texts.Count > 0) return string.Join("\n", texts);

            var rawArray = JsonSerializer.Serialize(doc.RootElement);
            return rawArray.Length > 0 ? rawArray : null;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
