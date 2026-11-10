using Microsoft.Extensions.Options;

namespace Emenda.Host.Security;

/// <summary>Where one effective caller entry comes from.</summary>
public enum CallerEntrySource
{
    /// <summary>Static <c>Emenda:Security:Callers</c> configuration (file/env).</summary>
    Configuration = 0,

    /// <summary>Dynamic caller file managed by the admin console.</summary>
    Dynamic = 1
}

/// <summary>
/// One effective caller credential: the static configuration entry or the
/// dynamic file entry that won the merge, with its source and management
/// metadata. The API key is only handed to the admin console endpoints.
/// </summary>
public sealed class EffectiveCallerEntry
{
    public required string Name { get; init; }

    public required string ApiKey { get; init; }

    public required CallerEntrySource Source { get; init; }

    public bool Enabled { get; init; } = true;

    public string Note { get; init; } = string.Empty;

    public string CreatedAtUtc { get; init; } = string.Empty;

    public string UpdatedAtUtc { get; init; } = string.Empty;
}

/// <summary>
/// The effective caller set: static configuration callers merged with the
/// dynamic caller file by name (a dynamic entry replaces a static entry with
/// the same name, so the admin console can rotate a deployment credential).
/// Matching uses a fixed-time comparison over every candidate, mirroring the
/// original middleware behaviour.
/// </summary>
public sealed class CallerRegistry
{
    private readonly IOptionsMonitor<ProductCallerAuthenticationOptions> _options;
    private readonly DynamicCallerStore? _dynamic;

    public CallerRegistry(
        IOptionsMonitor<ProductCallerAuthenticationOptions> options,
        DynamicCallerStore? dynamic = null)
    {
        _options = options;
        _dynamic = dynamic;
    }

    public bool HasDynamicStore => _dynamic is not null;

    /// <summary>The dynamic store, or null when only static callers are configured.</summary>
    public DynamicCallerStore? DynamicStore => _dynamic;

    /// <summary>
    /// The effective entries, configuration entries first (replaced ones
    /// omitted), then dynamic-only entries.
    /// </summary>
    public async Task<IReadOnlyList<EffectiveCallerEntry>> ListAsync(CancellationToken ct = default)
    {
        var dynamicEntries = _dynamic is null
            ? new List<DynamicCallerEntry>()
            : await _dynamic.ListAsync(ct).ConfigureAwait(false);
        var dynamicByName = dynamicEntries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<EffectiveCallerEntry>();
        foreach (var caller in _options.CurrentValue.ValidCallers)
        {
            if (dynamicByName.TryGetValue(caller.Name, out var replacement))
            {
                result.Add(new EffectiveCallerEntry
                {
                    Name = caller.Name,
                    ApiKey = replacement.ApiKey,
                    Source = CallerEntrySource.Dynamic,
                    Enabled = replacement.Enabled,
                    Note = replacement.Note,
                    CreatedAtUtc = replacement.CreatedAtUtc,
                    UpdatedAtUtc = replacement.UpdatedAtUtc
                });
                dynamicByName.Remove(caller.Name);
            }
            else
            {
                result.Add(new EffectiveCallerEntry
                {
                    Name = caller.Name,
                    ApiKey = caller.ApiKey,
                    Source = CallerEntrySource.Configuration,
                    Enabled = true
                });
            }
        }
        foreach (var entry in dynamicByName.Values)
        {
            result.Add(new EffectiveCallerEntry
            {
                Name = entry.Name,
                ApiKey = entry.ApiKey,
                Source = CallerEntrySource.Dynamic,
                Enabled = entry.Enabled,
                Note = entry.Note,
                CreatedAtUtc = entry.CreatedAtUtc,
                UpdatedAtUtc = entry.UpdatedAtUtc
            });
        }
        return result;
    }

    /// <summary>
    /// Matches a presented credential against every enabled effective entry
    /// with a fixed-time comparison. Returns the caller name or null.
    /// </summary>
    public async Task<string?> MatchAsync(string provided, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provided)) return null;
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
        string? matched = null;
        foreach (var entry in await ListAsync(ct).ConfigureAwait(false))
        {
            if (!entry.Enabled) continue;
            var expected = System.Text.Encoding.UTF8.GetBytes(entry.ApiKey);
            if (expected.Length == providedBytes.Length &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, providedBytes))
            {
                matched = entry.Name;
            }
        }
        return matched;
    }
}
