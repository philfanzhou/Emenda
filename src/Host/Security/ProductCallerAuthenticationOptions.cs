namespace Ruoyu.Study.Mistake.Host.Security;

/// <summary>
/// Product-owned inbound authentication for standalone deployments
/// (<c>Emenda:Runtime:Mode=Standalone</c>). Callers authenticate with a static
/// per-caller credential; the product does not depend on the platform identity
/// provider. The authority to act for a student remains the caller's
/// responsibility (ADR-0031 decision 2).
/// </summary>
public sealed class ProductCallerAuthenticationOptions
{
    public const string SectionName = "Emenda:Security";
    public const string ApiKeyHeaderName = "X-Emenda-Api-Key";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Local-development escape hatch only. Every deployment reachable by
    /// untrusted clients must leave this false and configure callers.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    public List<ProductCallerCredential> Callers { get; set; } = new();

    public IReadOnlyList<ProductCallerCredential> ValidCallers =>
        Callers
            .Where(caller => !string.IsNullOrWhiteSpace(caller.Name) && !string.IsNullOrWhiteSpace(caller.ApiKey))
            .ToList();
}

public sealed class ProductCallerCredential
{
    public string Name { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}