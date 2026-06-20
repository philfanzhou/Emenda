namespace Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;

public class VlAnalyzerOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.siliconflow.cn/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "Qwen/Qwen3-VL-30B-A3B-Instruct";
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMs { get; set; } = 1000;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
