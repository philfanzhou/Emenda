using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Common.Ai;

/// <summary>
/// Unified client for OpenAI-compatible chat/completions API.
/// Supports both non-streaming (CallAsync) and SSE streaming (CallStreamingAsync) modes.
/// Handles Bearer token auth, per-attempt timeout, retry with linear backoff, and 4xx non-429 short-circuit.
///
/// Each instance is bound to a specific AiClientOptions (one client per service configuration).
/// HttpClient is provided by IHttpClientFactory / AddHttpClient&lt;T&gt; for lifecycle management.
/// </summary>
public class OpenAiCompatibleClient
{
    private readonly HttpClient _httpClient;
    private readonly AiClientOptions _options;
    private readonly ILogger _logger;
    private readonly OpenAiSseReader? _sseReader;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Exposes the underlying HttpClient for callers that need to hit other endpoints
    /// on the same OpenAI-compatible API (e.g. GET /models/{id} for metadata queries).
    /// BaseAddress and Authorization are already configured by the constructor.
    /// </summary>
    public HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Constructor for non-streaming use (e.g. mistake VL image analysis).
    /// HttpClient.Timeout is set to options.TimeoutSeconds for hard timeout.
    /// </summary>
    public OpenAiCompatibleClient(HttpClient httpClient, AiClientOptions options, ILogger logger)
        : this(httpClient, options, logger, sseReader: null)
    {
    }

    /// <summary>
    /// Constructor for streaming use (SSE). The only streaming consumer (DocLibrary LLM
    /// document analysis) was extracted to the external Doctheca repo (ADR-0012); no in-repo
    /// service currently uses the streaming path, but the capability is retained.
    /// HttpClient.Timeout is set to InfiniteTimeSpan so streaming responses are not
    /// prematurely canceled; per-attempt timeout and idle timeout are enforced via
    /// CancellationTokenSource and OpenAiSseReader respectively.
    /// </summary>
    public OpenAiCompatibleClient(HttpClient httpClient, AiClientOptions options, ILogger logger, int streamIdleTimeoutSeconds)
        : this(httpClient, options, logger, new OpenAiSseReader(logger, streamIdleTimeoutSeconds))
    {
        // HttpClient.Timeout must be disabled for streaming, otherwise long-running
        // streams get canceled by the absolute timeout before tokens finish arriving.
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    private OpenAiCompatibleClient(HttpClient httpClient, AiClientOptions options, ILogger logger, OpenAiSseReader? sseReader)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _sseReader = sseReader;

        // Configure HttpClient - BaseUrl must end with / for relative path resolution
        var baseUrl = options.BaseUrl.TrimEnd('/');
        httpClient.BaseAddress = new Uri(baseUrl + "/");

        if (sseReader == null)
        {
            // Non-streaming mode: use finite HttpClient.Timeout as safety net.
            httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    /// <summary>
    /// Non-streaming call: returns the full response body as a string.
    /// Used for short-response scenarios (e.g. VL image analysis).
    /// </summary>
    public async Task<string> CallAsync(object requestBody, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                _logger.LogInformation("Retrying API call, attempt {Attempt}/{MaxRetries}", attempt, _options.MaxRetries);
                await Task.Delay(_options.RetryDelayMs * attempt, cancellationToken);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = JsonContent.Create(requestBody, options: JsonOptions),
                };
                using var response = await _httpClient.SendAsync(requestMessage, attemptCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogInformation("API call completed in {ElapsedMs}ms, response length={Length}",
                        sw.ElapsedMilliseconds, content.Length);
                    return content;
                }

                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("API call returned status {StatusCode}: {Error}", response.StatusCode, errorContent);

                // 4xx (non-429) = client error, retry won't help
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                    && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    throw new InvalidOperationException($"API client error ({response.StatusCode}): {errorContent}");
                }

                lastException = new InvalidOperationException($"API returned {response.StatusCode}: {errorContent}");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "API HTTP request failed, attempt {Attempt}", attempt + 1);
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("API request timed out, attempt {Attempt}", attempt + 1);
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("API call failed after retries");
    }

    /// <summary>
    /// SSE streaming call: returns accumulated delta.content as a string.
    /// Used for long-text generation scenarios (e.g. LLM document analysis).
    /// Requires the client to be constructed with streamIdleTimeoutSeconds.
    /// </summary>
    public async Task<string> CallStreamingAsync(object requestBody, CancellationToken cancellationToken)
    {
        if (_sseReader == null)
        {
            throw new InvalidOperationException(
                "CallStreamingAsync requires the client to be constructed with streamIdleTimeoutSeconds.");
        }

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = JsonContent.Create(requestBody, options: JsonOptions),
                };
                // PostAsJsonAsync buffers the full response, which defeats streaming.
                // Use SendAsync with ResponseHeadersRead so we can read the SSE body
                // incrementally as tokens arrive.
                using var response = await _httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCts.Token);
                response.EnsureSuccessStatusCode();

                var content = await _sseReader.ReadAllAsync(response.Content, attemptCts.Token);

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("API returned empty streaming response");
                }

                _logger.LogInformation("API streaming call completed in {ElapsedMs}ms, output length={Length}",
                    sw.ElapsedMilliseconds, content.Length);
                return content;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "API streaming call attempt {Attempt}/{MaxRetries} failed after {ElapsedMs}ms, retrying",
                    attempt, _options.MaxRetries, sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(1 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("API streaming call failed after all retries");
    }
}
