using Ruoyu.Study.Common.Ai;

namespace Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;

/// <summary>
/// Configuration options for VL (Vision-Language) image analysis.
/// Inherits common OpenAI-compatible API settings (ApiKey/BaseUrl/Model/TimeoutSeconds/MaxRetries/RetryDelayMs)
/// from AiClientOptions. Service-specific fields (Temperature) are defined here.
/// </summary>
public class VlAnalyzerOptions : AiClientOptions
{
    public VlAnalyzerOptions()
    {
        // Override base defaults to match the original mistake VL behavior.
        BaseUrl = "https://api.siliconflow.cn/v1";
        Model = "Qwen/Qwen3-VL-30B-A3B-Instruct";
    }

    /// <summary>
    /// Temperature parameter for VL response (default 0.1 for structured output).
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    // max_tokens is intentionally NOT sent in the request body. The API provider
    // uses its own default output limit based on the model's context window
    // (e.g. mimo 1M, stepfun 256K). Setting it does not help handle oversized
    // inputs — the provider returns an error if the input exceeds the limit.
    // See docs/overview/SystemContext.md "VL Provider" section.
}
