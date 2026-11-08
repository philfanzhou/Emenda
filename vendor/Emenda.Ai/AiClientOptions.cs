namespace Emenda.Common.Ai;

/// <summary>
/// Base configuration options for OpenAI-compatible API clients.
/// Each service inherits and extends with its own business-specific fields.
/// </summary>
public abstract class AiClientOptions
{
    /// <summary>
    /// API key for the LLM/VL provider (required to enable the service).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the API (e.g. https://api.xiaomimimo.com/v1).
    /// Trailing slash is optional; client normalizes it.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Model ID (e.g. mimo-v2.5-pro, Qwen/Qwen3-VL-30B-A3B-Instruct).
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Hard total timeout in seconds for a single API attempt (request send + response read).
    /// Enforced via CancellationTokenSource per attempt; HttpClient.Timeout is configured
    /// separately by the client (InfiniteTimeSpan for streaming, finite for non-streaming).
    /// Default: 60s.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum retry attempts (in addition to the initial call).
    /// Default: 2 (total 3 attempts).
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Linear backoff delay in milliseconds: RetryDelayMs * attempt.
    /// Default: 1000ms (1s, 2s, 3s, ...).
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Whether the client is configured (ApiKey is non-empty).
    /// Services use this to enable/disable AI features at startup.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
