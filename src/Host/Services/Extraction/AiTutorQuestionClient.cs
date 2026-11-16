using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Emenda.Service.Intake;
using Microsoft.Extensions.Options;

namespace Emenda.Host.Services.Extraction;

public sealed class AiTutorQuestionClient(HttpClient client, IOptions<ExtractionOptions> options) : IQuestionAnalysisClient
{
    private const int RequestLimit = 8 * 1024 * 1024;
    private const int ResponseLimit = 2 * 1024 * 1024;
    // Serialized bytes are sent only as HTTP JSON, never embedded in HTML.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Use this handler when registering the typed client; credentials must never follow redirects.
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    public async Task<QuestionAnalysisResult> AnalyzeAsync(QuestionAnalysisRequest request, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.IsConfigured) throw new ExtractionException("analysis_unavailable", 503);
        ValidateRequest(request);
        var body = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (body.Length > RequestLimit) throw new ExtractionException("analysis_request_too_large", 413);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.OperationTimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.TutorBaseUrl), "/api/v1/problems/analyze-images"));
        message.Headers.Add("X-Emenda-Analysis-Key", settings.TutorApiKey);
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new("application/json");
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ExtractionException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "analysis_credentials_rejected",
                    HttpStatusCode.TooManyRequests => "analysis_rate_limited",
                    _ => "analysis_provider_unavailable"
                }, 503);
            if (response.RequestMessage?.RequestUri != message.RequestUri)
                throw new ExtractionException("analysis_redirect_rejected", 503);
            var contentType = response.Content.Headers.ContentType;
            var mediaType = contentType?.MediaType;
            var charset = contentType?.CharSet?.Trim('"');
            if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                    || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                    && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                || charset is not null && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Count != 0)
                throw new ExtractionException("invalid_analysis_response", 502);
            if (response.Content.Headers.ContentLength > ResponseLimit)
                throw new ExtractionException("invalid_analysis_response", 502);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (bytes.Length + count > ResponseLimit) throw new ExtractionException("invalid_analysis_response", 502);
                bytes.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("model", out _)
                || !root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            foreach (var question in questions.EnumerateArray())
            {
                if (question.ValueKind != JsonValueKind.Object) throw new JsonException();
                foreach (var name in new[] { "subject", "statement", "studentAnswer", "referenceAnswer", "explanation", "outcome", "evidence", "regions" })
                    if (!question.TryGetProperty(name, out _)) throw new JsonException();
            }
            var result = root.Deserialize<QuestionAnalysisResult>(Json);
            ValidateResult(result, request.Images.Select(i => i.SourceId).ToHashSet());
            return result!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new ExtractionException("analysis_timeout", 504); }
        catch (HttpRequestException) { throw new ExtractionException("analysis_provider_unavailable", 503); }
        catch (IOException) { throw new ExtractionException("analysis_provider_unavailable", 503); }
        catch (JsonException) { throw new ExtractionException("invalid_analysis_response", 502); }
    }

    private static bool ValidRegion(ExtractionRegion? r, HashSet<Guid> sources) => r is not null
        && sources.Contains(r.SourceId) && double.IsFinite(r.X0) && double.IsFinite(r.Y0)
        && double.IsFinite(r.X1) && double.IsFinite(r.Y1)
        && r.X0 >= 0 && r.Y0 >= 0 && r.X1 <= 1 && r.Y1 <= 1 && r.X0 < r.X1 && r.Y0 < r.Y1;

    private static void ValidateRequest(QuestionAnalysisRequest request)
    {
        if (request is null || request.Grade is < 1 or > 12 || request.Images is null || request.Images.Count is < 1 or > 9)
            throw new ExtractionException("invalid_analysis_request");
        var sources = request.Images.Where(i => i is not null).Select(i => i.SourceId).ToHashSet();
        if (sources.Contains(Guid.Empty) || sources.Count != request.Images.Count || request.Images.Any(i => i is null
            || i.Width is < 1 or > 8192 || i.Height is < 1 or > 8192 || (long)i.Width * i.Height > 16000000
            || i.DataUrl is null || !(i.DataUrl.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) || i.DataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            || i.Regions is null || i.Regions.Count > 256 || i.Regions.Any(r => !ValidRegion(r, [i.SourceId]))))
            throw new ExtractionException("invalid_analysis_request");
        foreach (var image in request.Images)
        {
            var data = image.DataUrl.AsSpan(image.DataUrl.IndexOf(',') + 1);
            if (data.Length == 0 || data.Length > RequestLimit) throw new ExtractionException("invalid_analysis_request");
            var decoded = new byte[data.Length / 4 * 3 + 3];
            if (!Convert.TryFromBase64Chars(data, decoded, out _)) throw new ExtractionException("invalid_analysis_request");
        }
    }

    private static void ValidateResult(QuestionAnalysisResult? result, HashSet<Guid> sources)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Model) || result.Model.Length > 256
            || result.Questions is null || result.Questions.Count > 32)
            throw new ExtractionException("invalid_analysis_response", 502);
        foreach (var q in result.Questions)
            if (q is null || q.Subject is < 1 or > 9 || string.IsNullOrWhiteSpace(q.Statement) || q.Statement.Length > 8000
                || q.StudentAnswer?.Length > 8000 || q.ReferenceAnswer is null || q.ReferenceAnswer.Length > 16000
                || q.Explanation is null || q.Explanation.Length > 16000 || q.Evidence is null || q.Evidence.Length > 16000
                || q.Outcome is not ("correct" or "incorrect" or "unknown")
                || q.Outcome != "unknown" && (string.IsNullOrWhiteSpace(q.StudentAnswer) || string.IsNullOrWhiteSpace(q.ReferenceAnswer) || string.IsNullOrWhiteSpace(q.Evidence))
                || q.Regions is null || q.Regions.Count is < 1 or > 32 || q.Regions.Any(r => !ValidRegion(r, sources)))
                throw new ExtractionException("invalid_analysis_response", 502);
    }
}
