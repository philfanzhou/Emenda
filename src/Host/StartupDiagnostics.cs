namespace Emenda.Host;

/// <summary>Startup log helpers; secrets are masked before they reach a log line.</summary>
public static class StartupDiagnostics
{
    /// <summary>Keeps the first and last 4 characters; shorter keys are fully masked.</summary>
    public static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return string.Empty;
        if (apiKey.Length < 8) return "****";
        return string.Concat(apiKey.AsSpan(0, 4), "****", apiKey.AsSpan(apiKey.Length - 4));
    }

    public static string SummarizePostgreSqlPool(int maximum, int minimum, int idleSeconds, int pruningSeconds, int timeoutSeconds, bool pooling) =>
        $"MaximumPoolSize={maximum}, MinimumPoolSize={minimum}, IdleLifetimeSeconds={idleSeconds}, PruningIntervalSeconds={pruningSeconds}, ConnectionTimeoutSeconds={timeoutSeconds}, Pooling={pooling}";
}
