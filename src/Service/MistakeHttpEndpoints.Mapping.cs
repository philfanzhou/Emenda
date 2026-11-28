using System.Text.Json;
using Emenda.Domain.Models;
using Emenda.HttpContracts;
using Emenda.Service.Dtos;

namespace Emenda.Service;

public static partial class MistakeHttpEndpoints
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static string FormatCreatedAt(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue ? string.Empty : value.UtcDateTime.ToString(TimestampFormat);

    private static MistakeItemDto ToDto(MistakeItemModel item)
    {
        var dto = new MistakeItemDto
        {
            // A conversation's evidence snapshot is its source_detail, byte for byte.
            ConversationSource = item.SourceKind != MistakeSourceKinds.Conversation || item.SourceDetail == null ? null
                : JsonSerializer.Deserialize<ConversationQuestionSnapshot>(item.SourceDetail, JsonSerializerOptions.Web),
            ConversationActive = item.ConversationActive,
            Id = item.Id.ToString(),
            StudentId = item.StudentId ?? string.Empty,
            Subject = item.Subject,
            Grade = item.Grade,
            SourceUploadId = item.SourceUploadId ?? string.Empty,
            ReviewStatus = (ReviewStatus)item.ReviewStatus,
            ReviewerId = item.ReviewerId ?? string.Empty,
            ReviewedAt = item.ReviewedAt?.UtcDateTime.ToString(TimestampFormat) ?? string.Empty,
            RootCause = item.RootCause ?? string.Empty,
            QuestionId = item.QuestionId ?? string.Empty,
            CreatedAt = FormatCreatedAt(item.CreatedAt),
            UpdatedAt = item.UpdatedAt?.UtcDateTime.ToString(TimestampFormat) ?? string.Empty
        };

        foreach (var region in item.SourceRegions ?? new())
        {
            var regionDto = new SourceRegionDto
            {
                SourceImagePath = region.SourceImagePath ?? string.Empty
            };

            if (region.BoundingBox != null)
            {
                regionDto.BoundingBox = new BoundingBoxDto
                {
                    X1 = region.BoundingBox.X1,
                    Y1 = region.BoundingBox.Y1,
                    X2 = region.BoundingBox.X2,
                    Y2 = region.BoundingBox.Y2
                };
            }

            dto.SourceRegions.Add(regionDto);
        }

        dto.SourceKind = item.SourceKind;
        dto.Content = MapContent(item, dto.ReviewStatus);
        return dto;
    }

    // Text exposure (unchanged since mistake-http-v6): conversation text travels with conversationSource, while an
    // extraction mistake's text is shown only while it is pending review or confirmed.
    // Images are always listed; their paths are already public through sourceRegions.
    private static MistakeContentDto MapContent(MistakeItemModel item, ReviewStatus status)
    {
        if (string.IsNullOrEmpty(item.Content)) return MistakeContentDto.Empty;
        using var document = JsonDocument.Parse(item.Content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return MistakeContentDto.Empty;
        var showText = item.SourceKind != MistakeSourceKinds.Extraction
            || status is ReviewStatus.PendingReview or ReviewStatus.Confirmed;
        string? Text(string name) => showText && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var points = showText && root.TryGetProperty("knowledgePoints", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray()
            : [];
        var images = root.TryGetProperty("images", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(image =>
            {
                var path = image.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : string.Empty;
                var role = image.TryGetProperty("role", out var r) && r.GetString() == "source" ? "source" : "question";
                int[]? box = image.TryGetProperty("box", out var b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() == 4
                    && b.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _))
                    ? b.EnumerateArray().Select(v => v.GetInt32()).ToArray() : null;
                return new MistakeContentImageDto(path, role, box);
            }).ToArray()
            : [];
        return new MistakeContentDto(1, Text("statement"), Text("studentAnswer"), Text("referenceAnswer"),
            Text("explanation"), Text("evidence"), points, images);
    }

    private static ProcessLogDto ToDto(ProcessLogModel log)
    {
        return new ProcessLogDto
        {
            Id = log.Id.ToString(),
            MistakeItemId = log.MistakeItemId.ToString(),
            Action = (ProcessAction)log.Action,
            Details = log.Details ?? string.Empty,
            CreatedAt = FormatCreatedAt(log.CreatedAt),
            StudentId = log.StudentId ?? string.Empty
        };
    }
}
