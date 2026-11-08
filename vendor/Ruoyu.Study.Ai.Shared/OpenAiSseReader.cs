using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Common.Ai;

/// <summary>
/// Reads an OpenAI-compatible SSE stream and accumulates delta.content into a single string.
/// Each event line has the form "data: {json}". The stream ends with "data: [DONE]".
///
/// Uses a per-read idle timeout: every ReadLineAsync gets its own linked token that
/// cancels if no SSE event arrives within idleTimeoutSeconds. As long as the provider
/// keeps sending tokens, the overall call can take much longer than a hard total timeout.
/// </summary>
public class OpenAiSseReader
{
    private readonly ILogger _logger;
    private readonly int _idleTimeoutSeconds;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiSseReader(ILogger logger, int idleTimeoutSeconds)
    {
        _logger = logger;
        _idleTimeoutSeconds = idleTimeoutSeconds > 0 ? idleTimeoutSeconds : 60;
    }

    /// <summary>
    /// Read the entire SSE stream from an HttpContent and accumulate delta.content values.
    /// Caller is responsible for disposing the HttpContent.
    /// </summary>
    public async Task<string> ReadAllAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var contentBuilder = new StringBuilder();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var idleTimeout = TimeSpan.FromSeconds(_idleTimeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line;
            using (var lineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lineCts.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(lineCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"No SSE data received for {_idleTimeoutSeconds}s (idle timeout).");
                }
            }

            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            OpenAiStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(data, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Skip malformed chunks (e.g. keep-alive comments) but log for diagnostics
                _logger.LogDebug(ex, "Skipping malformed SSE chunk: {Data}", data);
                continue;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                contentBuilder.Append(delta);
            }
        }

        return contentBuilder.ToString();
    }
}
