namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>
/// Configuration options for the StructaDoc layout-detection client (IKQA2N), bound once at
/// startup from the <c>Mistake:Layout</c> configuration section and fixed for the process
/// lifetime. The scoped StructaDoc credential resolves from deployment secret configuration
/// (<c>Mistake:Layout:ApiKey</c>, environment key <c>Mistake__Layout__ApiKey</c>); it is
/// referenced and masked in logs, never printed. <see cref="BaseUrl"/> is
/// administrator-configured only — no caller-supplied URL or credential is ever accepted.
/// </summary>
public sealed class StructaDocLayoutOptions
{
    public const string SectionName = "Mistake:Layout";

    /// <summary>The per-image budget. The initial default of 180 seconds accommodates the
    /// deployed cloud exchange's 120-second execution deadline with queue-wait headroom.</summary>
    public const int DefaultPerImageBudgetSeconds = 180;

    /// <summary>The concurrency bound matching the deployed cloud adapter's four concurrent
    /// layout exchanges.</summary>
    public const int DefaultMaxConcurrentExchanges = 4;

    /// <summary>The default bound on one accepted layout response body.</summary>
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Root base URL of the StructaDoc deployment (the service root; the client
    /// appends <c>api/v1/layout/detect</c>). Required when the capability is enabled.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The scoped StructaDoc API client credential (scope <c>layout:detect</c> only).
    /// Sent as <c>Authorization: ApiKey &lt;credential&gt;</c>. Required when the capability is
    /// enabled.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Per-image budget in seconds. Must be at least 1 and at most 3600.</summary>
    public int PerImageBudgetSeconds { get; set; } = DefaultPerImageBudgetSeconds;

    /// <summary>Maximum concurrent layout exchanges this process may hold. Must be from 1 to 16.</summary>
    public int MaxConcurrentExchanges { get; set; } = DefaultMaxConcurrentExchanges;

    /// <summary>Maximum accepted layout response body size in bytes. Must be from 4096 to
    /// 67108864 (64 MiB).</summary>
    public int MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    public TimeSpan PerImageBudget => TimeSpan.FromSeconds(PerImageBudgetSeconds);

    /// <summary>Validates the bounded settings. Returns one message per violation; an empty
    /// list means the values are usable. Callers validate only when the capability is enabled:
    /// a disabled process makes zero layout requests and ignores stale values.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PerImageBudgetSeconds is < 1 or > 3600)
            errors.Add($"{SectionName}:PerImageBudgetSeconds must be an integer from 1 to 3600.");
        if (MaxConcurrentExchanges is < 1 or > 16)
            errors.Add($"{SectionName}:MaxConcurrentExchanges must be an integer from 1 to 16.");
        if (MaxResponseBytes is < 4096 or > 64 * 1024 * 1024)
            errors.Add($"{SectionName}:MaxResponseBytes must be from 4096 to 67108864 bytes.");
        return errors;
    }
}
