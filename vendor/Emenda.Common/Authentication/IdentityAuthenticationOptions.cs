namespace Emenda.Common.Authentication;

/// <summary>
/// Defines the identity trust contract shared by every Emenda JWT consumer.
/// Application credentials intentionally remain in each portal's own options.
/// </summary>
public sealed class IdentityAuthenticationOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string[] AdditionalValidIssuers { get; set; } = [];
    public string Audience { get; set; } = string.Empty;
    public string? AdminAudience { get; set; }

    /// <summary>
    /// When true the Mistake host requires an authenticated caller (shared
    /// audience scheme or Admin AppId audience scheme) on its /api endpoint
    /// group instead of the default anonymous compatibility window. Startup
    /// snapshot like every identity trust setting; the default false keeps
    /// the anonymous window and lets the host start without a configured
    /// IdentityService section.
    /// </summary>
    public bool MistakeApiStrictAuth { get; set; }

    /// <summary>
    /// When true the Mistake host additionally enforces per-resource
    /// authorization (student ownership + staff delegation matrix, IKJEPZ) on
    /// its /api endpoint group instead of accepting request-supplied
    /// studentId/reviewerId/object-path values as authority. Requires
    /// <see cref="MistakeApiStrictAuth"/> to be true: resource authorization
    /// presupposes an authenticated caller, so the combination with the
    /// anonymous window fails startup. Startup snapshot like every identity
    /// trust setting; the default false keeps the compatibility-window
    /// behavior byte-identical to the pre-IKJEPZ baseline.
    /// </summary>
    public bool MistakeApiResourceAuth { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 30;

    public IReadOnlyList<string> GetValidIssuers()
    {
        return new[] { Issuer }
            .Concat(AdditionalValidIssuers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Every accepted administrator audience. <see cref="AdminAudience"/> may list several
    /// application ids separated by commas, semicolons or whitespace: the Portal
    /// administration consoles that forward their administrator's token (ADR-0030). Each still has to
    /// match exactly. Empty when not configured, so the admin-audience path fails closed.
    /// </summary>
    public IReadOnlyList<string> GetAdminAudiences()
    {
        return string.IsNullOrWhiteSpace(AdminAudience)
            ? Array.Empty<string>()
            : AdminAudience
                .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }
}
