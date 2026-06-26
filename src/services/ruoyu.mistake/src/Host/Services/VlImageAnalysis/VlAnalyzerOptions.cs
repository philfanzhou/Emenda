namespace Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;

public class VlAnalyzerOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.siliconflow.cn/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "Qwen/Qwen3-VL-30B-A3B-Instruct";
    public double Temperature { get; set; } = 0.1;

    // Required for reasoning VL models (e.g. mimo-v2.5, step-3.7-flash) so that
    // their internal `reasoning_content` does not exhaust the entire token budget
    // and leave `content` empty. See docs/overview/SystemContext.md and HumanReview HR-12.
    public int MaxOutputTokens { get; set; } = 4096;

    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMs { get; set; } = 1000;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
