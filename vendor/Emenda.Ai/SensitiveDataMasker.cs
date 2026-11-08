namespace Emenda.Common.Ai;

/// <summary>
/// Sensitive data masking utility. All API keys and secrets written to logs (including Loki)
/// must be masked through this utility before logging.
/// </summary>
public static class SensitiveDataMasker
{
    /// <summary>
    /// API key masking: preserves first 4 + last 4 characters, replaces middle with ****.
    /// Returns **** for keys shorter than 8 characters.
    /// </summary>
    public static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return string.Empty;
        if (apiKey.Length < 8) return "****";

        return string.Concat(apiKey.AsSpan(0, 4), "****", apiKey.AsSpan(apiKey.Length - 4));
    }
}
