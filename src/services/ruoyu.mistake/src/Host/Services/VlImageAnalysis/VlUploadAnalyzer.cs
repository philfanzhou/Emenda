using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Ai;
using Ruoyu.Study.Common.Constants;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Service.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;

public class VlUploadAnalyzer : IVlAnalyzer
{
    private readonly IOssService _ossService;
    private readonly OpenAiCompatibleClient _client;
    private readonly VlAnalyzerOptions _options;
    private readonly ILogger<VlUploadAnalyzer> _logger;

    private const int MaxImageLongSide = 768;

    public VlUploadAnalyzer(
        IOssService ossService,
        OpenAiCompatibleClient client,
        IOptions<VlAnalyzerOptions> options,
        ILogger<VlUploadAnalyzer> logger)
    {
        _ossService = ossService;
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VlAnalysisResult> AnalyzeAsync(string studentId, List<string> imagePaths, CancellationToken ct = default)
    {
        if (imagePaths == null || imagePaths.Count == 0)
        {
            _logger.LogWarning("No image paths provided for analysis");
            return new VlAnalysisResult { Skipped = true };
        }

        var imageDataList = await LoadImagesAsBase64Async(imagePaths, ct);

        var prompt = BuildPrompt(imagePaths.Count);
        var requestBody = BuildRequestBody(prompt, imageDataList);

        var responseContent = await _client.CallAsync(requestBody, ct);

        return ParseResponse(responseContent, imagePaths.Count);
    }

    public async Task<VlAnalysisResult> AnalyzeVerifiedAsync(string studentId, List<string> imagePaths,
        IReadOnlyList<byte[]> imageBytes, CancellationToken ct = default)
    {
        if (imagePaths.Count == 0 || imagePaths.Count != imageBytes.Count)
            throw new InvalidOperationException("Invalid verified image set.");
        var data = imageBytes.Select(bytes => "data:image/jpeg;base64," + Convert.ToBase64String(CompressImage(bytes))).ToList();
        var response = await _client.CallAsync(BuildRequestBody(BuildPrompt(data.Count), data), ct);
        try
        {
            var api = JsonSerializer.Deserialize<OpenAiApiResponse>(response,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            var content = api?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content)) throw new JsonException();
            var start = content.IndexOf('['); var end = content.LastIndexOf(']');
            if (start < 0 || end < start) throw new JsonException();
            var raw = JsonSerializer.Deserialize<List<VlGroupDto>>(content[start..(end+1)]) ?? throw new JsonException();
            return new VlAnalysisResult { Groups = raw.Select(g => new VlImageGroup
            { ImageIndices = g.ImageIndices ?? [], Subject = g.Subject, Grade = g.Grade, RootCause = g.RootCause ?? "" }).ToList() };
        }
        catch (JsonException) { throw new InvalidOperationException("Invalid intake analysis plan."); }
    }

    public async Task<VlReanalysisResult> ReAnalyzeAsync(
        string studentId,
        List<string> imagePaths,
        string existingRootCause,
        string? teacherDescription,
        CancellationToken ct = default)
    {
        if (imagePaths == null || imagePaths.Count == 0)
        {
            _logger.LogWarning("ReAnalyzeAsync: No image paths provided for item belonging to studentId={StudentId}", studentId);
            return new VlReanalysisResult { Skipped = true };
        }

        var imageDataList = await LoadImagesAsBase64Async(imagePaths, ct);

        var prompt = BuildReanalyzePrompt(imagePaths.Count, existingRootCause, teacherDescription);
        var requestBody = BuildReanalyzeRequestBody(prompt, imageDataList);

        var responseContent = await _client.CallAsync(requestBody, ct);

        return ParseReanalyzeResponse(responseContent);
    }

    private async Task<List<string>> LoadImagesAsBase64Async(List<string> imagePaths, CancellationToken ct)
    {
        var results = new List<string>();
        for (int i = 0; i < imagePaths.Count; i++)
        {
            var path = imagePaths[i];
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = await _ossService.DownloadAsync(path);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                var compressedBytes = CompressImage(bytes);
                var base64 = Convert.ToBase64String(compressedBytes);
                var mimeType = "image/jpeg";
                results.Add($"data:{mimeType};base64,{base64}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Failed to load image {i} from OSS: {path}", ex);
            }
        }
        return results;
    }

    private static byte[] CompressImage(byte[] imageData)
    {
        using var image = Image.Load(imageData);

        if (image.Width > MaxImageLongSide || image.Height > MaxImageLongSide)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(MaxImageLongSide, MaxImageLongSide),
                Mode = ResizeMode.Max
            }));
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
        return ms.ToArray();
    }

    private static string BuildReanalyzePrompt(int totalImageCount, string existingRootCause, string? teacherDescription)
    {
        // 安全截断教师描述，防止异常长度污染 prompt
        var safeExisting = string.IsNullOrWhiteSpace(existingRootCause) ? "(none)" : existingRootCause;
        var hasTeacherNote = !string.IsNullOrWhiteSpace(teacherDescription);

        var teacherNoteSection = hasTeacherNote
            ? $@"

[Teacher's Additional Context]
The reviewing teacher has provided the following note about this mistake:
""{teacherDescription}""

Take this teacher note into account as supplementary context, but the images remain the primary source of truth.
"
            : "\n(No additional teacher note was provided.)\n";

        return $@"[Task Description]
You are re-analyzing a previously-analyzed student mistake. The original analysis concluded:
""{safeExisting}""

You will see {totalImageCount} images for this mistake. Produce a refined one-sentence analysis of the error root cause in Chinese (no more than 50 characters). Focus on WHY the student made the mistake, not just WHAT the mistake is.
{teacherNoteSection}
[Output Requirements]
Return ONLY a JSON object with no other text:
{{ ""rootCause"": ""..."" }}
";
    }

    private object BuildReanalyzeRequestBody(string prompt, List<string> imageDataList)
    {
        var contentParts = new List<object>();

        contentParts.Add(new { type = "text", text = prompt });

        foreach (var dataUri in imageDataList)
        {
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = dataUri }
            });
        }

        // max_tokens is intentionally omitted — see VlAnalyzerOptions comment.
        return new
        {
            model = _options.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = contentParts
                }
            },
            temperature = _options.Temperature,
            stream = false
        };
    }

    private VlReanalysisResult ParseReanalyzeResponse(string responseContent)
    {
        var result = new VlReanalysisResult { Skipped = false };

        try
        {
            var apiResponse = JsonSerializer.Deserialize<OpenAiApiResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            if (apiResponse?.Choices == null || apiResponse.Choices.Count == 0)
            {
                _logger.LogWarning("ReAnalyzeAsync: VL API returned empty choices. Raw (first 500): {Raw}",
                    Truncate(responseContent, 500));
                return result;
            }

            var choice = apiResponse.Choices[0];
            var finishReason = choice.FinishReason ?? "unknown";
            var content = choice.Message?.Content;

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning(
                    "ReAnalyzeAsync: VL API returned empty content. finish_reason={FinishReason}, raw (first 500): {Raw}",
                    finishReason, Truncate(responseContent, 500));
                result.RawResponse = responseContent;
                return result;
            }

            result.RawResponse = content;

            var rootCause = TryParseReanalysisRootCause(content);
            if (rootCause == null)
            {
                _logger.LogWarning("ReAnalyzeAsync: Failed to extract rootCause from response. Content (first 500): {Content}",
                    Truncate(content, 500));
                return result;
            }

            result.RootCause = rootCause;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ReAnalyzeAsync: Failed to parse VL API response. Raw (first 500): {Raw}",
                Truncate(responseContent, 500));
        }

        return result;
    }

    private static string? TryParseReanalysisRootCause(string content)
    {
        // Try direct JSON object parse first
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rootCause", out var prop))
            {
                var rc = prop.GetString();
                if (!string.IsNullOrWhiteSpace(rc)) return rc.Trim();
            }
        }
        catch (JsonException)
        {
            // Fall through to regex fallback
        }

        // Fallback: scan for "rootCause" key (handles responses wrapped in code blocks or with extra text)
        var keyIndex = content.IndexOf("\"rootCause\"", StringComparison.Ordinal);
        if (keyIndex < 0) return null;

        var colonIndex = content.IndexOf(':', keyIndex);
        if (colonIndex < 0) return null;

        var quoteStart = content.IndexOf('"', colonIndex + 1);
        if (quoteStart < 0) return null;

        var quoteEnd = content.IndexOf('"', quoteStart + 1);
        if (quoteEnd < 0) return null;

        var value = content.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildPrompt(int totalImageCount)
    {
        var subjectList = string.Join("\n", SubjectConstants.DisplayNames.Select(kv => $"{kv.Key}={kv.Value}"));
        var gradeList = string.Join("\n", GradeConstants.DisplayNames.Select(kv => $"{kv.Key}={kv.Value}"));

        return $@"[Task Description]
You will see {totalImageCount} images uploaded by a student. Analyze these images, determine which mistake items they belong to, and split the images into groups by ""same mistake item"".

[Splitting Rules]
- Multiple images belonging to the same mistake item (question + solution, continuous steps, related diagrams, etc.) must be in the same group.
- Images of different subjects/different questions must be in different groups.
- The union of all splits must cover all {totalImageCount} images (indices starting from 0), with no omissions or duplicates.

[Available Subjects]
{subjectList}

[Available Grades]
{gradeList}

[Output Requirements]
You must and may only return a JSON array, without any other text explanation.
JSON format example (assuming 3 images, where the first 2 belong to the same mistake):
[
  {{ ""imageIndices"": [0, 1], ""subject"": 1, ""grade"": 7, ""rootCause"": ""语文现代文阅读理解答题思路不清"" }},
  {{ ""imageIndices"": [2],    ""subject"": 3, ""grade"": 7, ""rootCause"": ""英语完形填空上下文逻辑理解错误"" }}
]

[Field Rules]
1. imageIndices: integer array, representing the original indices of images in this group (0-based). The union of all groups' imageIndices = [0, {totalImageCount - 1}].
2. subject: subject number (1-9).
3. grade: grade number (1-12).
4. rootCause: one-sentence analysis of the error root cause in Chinese (no more than 50 characters). Focus on WHY the student made the mistake, not just WHAT the mistake is.

[Important Notes]
- Only valid subject numbers 1-9 and grade numbers 1-12 are allowed.
- If an image cannot determine subject/grade, choose the most likely option, do not return 0.
- The returned JSON must be in standard array format, ensuring it can be parsed correctly.";
    }

    private object BuildRequestBody(string prompt, List<string> imageDataList)
    {
        var contentParts = new List<object>();

        contentParts.Add(new { type = "text", text = prompt });

        foreach (var dataUri in imageDataList)
        {
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = dataUri }
            });
        }

        // max_tokens is intentionally omitted — see VlAnalyzerOptions comment.
        return new
        {
            model = _options.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = contentParts
                }
            },
            temperature = _options.Temperature,
            stream = false
        };
    }

    private VlAnalysisResult ParseResponse(string responseContent, int totalImageCount)
    {
        var result = new VlAnalysisResult { Skipped = false };

        try
        {
            var apiResponse = JsonSerializer.Deserialize<OpenAiApiResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            if (apiResponse?.Choices == null || apiResponse.Choices.Count == 0)
            {
                _logger.LogWarning("VL API returned empty choices. Raw response (first 500 chars): {RawResponse}",
                    Truncate(responseContent, 500));
                return result;
            }

            var choice = apiResponse.Choices[0];
            var finishReason = choice.FinishReason ?? "unknown";
            var content = choice.Message?.Content;

            _logger.LogDebug("VL API finish_reason: {FinishReason}, content length: {ContentLength}",
                finishReason, content?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(content))
            {
                var contentType = DetectContentType(responseContent);
                _logger.LogWarning(
                    "VL API returned empty message content. finish_reason={FinishReason}, " +
                    "content JSON type={ContentType}, raw response (first 1000 chars): {RawResponse}",
                    finishReason, contentType, Truncate(responseContent, 1000));
                result.RawResponse = responseContent;
                return result;
            }

            result.RawResponse = content;

            var groups = TryParseGroups(content, totalImageCount);
            if (groups == null)
            {
                _logger.LogWarning("Failed to extract groups JSON from VL response. Content (first 500 chars): {Content}",
                    Truncate(content, 500));
                return result;
            }

            result.Groups = groups;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse VL API response. Raw response (first 500 chars): {RawResponse}",
                Truncate(responseContent, 500));
        }

        return result;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
    }

    private static string DetectContentType(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.ValueKind switch
                    {
                        JsonValueKind.String => "string",
                        JsonValueKind.Array => $"array[{content.GetArrayLength()} elements]",
                        JsonValueKind.Null => "null",
                        JsonValueKind.Object => "object",
                        _ => content.ValueKind.ToString()
                    };
                }
            }
        }
        catch
        {
            // Ignore parse errors in detection
        }
        return "unknown";
    }

    private List<VlImageGroup>? TryParseGroups(string content, int totalImageCount)
    {
        _logger.LogDebug("TryParseGroups: totalImageCount={TotalImageCount}, contentLen={ContentLen}",
            totalImageCount, content.Length);

        var startIdx = content.IndexOf('[');
        if (startIdx < 0)
        {
            _logger.LogWarning("TryParseGroups: No '[' found in content");
            return null;
        }

        var endIdx = content.LastIndexOf(']');
        if (endIdx < startIdx)
        {
            _logger.LogWarning("TryParseGroups: No ']' found in content or invalid position");
            return null;
        }

        var jsonStr = content.Substring(startIdx, endIdx - startIdx + 1);

        var rawGroups = JsonSerializer.Deserialize<List<VlGroupDto>>(jsonStr, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        if (rawGroups == null)
        {
            _logger.LogWarning("TryParseGroups: Deserialize returned null");
            return null;
        }

        _logger.LogDebug("TryParseGroups: Parsed {RawGroupCount} raw groups from JSON", rawGroups.Count);

        var groups = new List<VlImageGroup>();
        var usedIndices = new HashSet<int>();
        foreach (var raw in rawGroups)
        {
            var groupIdx = groups.Count;
            if (raw.ImageIndices == null || raw.ImageIndices.Count == 0)
            {
                _logger.LogWarning("TryParseGroups: Group {GroupIdx} skipped (no imageIndices)", groupIdx);
                continue;
            }

            var validIndices = raw.ImageIndices
                .Where(i => i >= 0 && i < totalImageCount)
                .Distinct()
                .ToList();

            if (validIndices.Count == 0)
            {
                _logger.LogWarning("TryParseGroups: Group {GroupIdx} skipped (no valid indices)", groupIdx);
                continue;
            }

            var overlappingIndices = validIndices.Where(i => usedIndices.Contains(i)).ToList();
            if (overlappingIndices.Count > 0)
            {
                validIndices = validIndices.Except(overlappingIndices).ToList();
                if (validIndices.Count == 0)
                {
                    _logger.LogWarning("TryParseGroups: Group {GroupIdx} skipped (all indices overlap)", groupIdx);
                    continue;
                }
                _logger.LogInformation("TryParseGroups: Group {GroupIdx} removed overlapping indices, remaining={Remaining}",
                    groupIdx, string.Join(",", validIndices));
            }

            foreach (var i in validIndices) usedIndices.Add(i);

            var subject = raw.Subject > 0 && raw.Subject <= 9 ? raw.Subject : 1;
            var grade = raw.Grade > 0 && raw.Grade <= 12 ? raw.Grade : 7;

            groups.Add(new VlImageGroup
            {
                ImageIndices = validIndices,
                Subject = subject,
                Grade = grade,
                RootCause = raw.RootCause ?? string.Empty
            });

            _logger.LogDebug("TryParseGroups: Added group {GroupIdx} (indices={ValidIndices}, subject={Subject}, grade={Grade})",
                groupIdx, string.Join(",", validIndices), subject, grade);
        }

        if (groups.Count == 0) return null;

        return groups;
    }

    private class VlGroupDto
    {
        [JsonPropertyName("imageIndices")]
        public List<int>? ImageIndices { get; set; }

        [JsonPropertyName("subject")]
        public int Subject { get; set; }

        [JsonPropertyName("grade")]
        public int Grade { get; set; }

        [JsonPropertyName("rootCause")]
        public string? RootCause { get; set; }
    }
}
