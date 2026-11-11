using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emenda.Host.Security;

/// <summary>
/// One caller entry of the dynamic caller file. The shape mirrors
/// <see cref="ProductCallerCredential"/> (plain credential value, the same
/// value a caller presents in <c>X-Emenda-Api-Key</c>) plus management
/// metadata shown in the admin console.
/// </summary>
public sealed class DynamicCallerEntry
{
    public string Name { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Note { get; set; } = string.Empty;

    public string CreatedAtUtc { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public string UpdatedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// File document of the dynamic caller store (version 1).
/// </summary>
public sealed class DynamicCallerDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("callers")]
    public List<DynamicCallerEntry> Callers { get; set; } = new();
}

/// <summary>
/// Caller credentials persisted in a JSON file under a configurable path
/// (default <c>/app/data/emenda-callers.json</c>, volume-mountable). The file
/// and the static <c>Emenda:Security:Callers</c> configuration are merged by
/// caller name: a dynamic entry with the same name replaces the static one,
/// so the admin console can rotate a deployment-provided credential without
/// touching the container environment. The store is the only writer of the
/// file; concurrent requests serialize on a single async lock and every write
/// is atomic (temp file + move) so a crash never leaves a partial document.
/// </summary>
public sealed class DynamicCallerStore
{
    private static readonly JsonSerializerOptions DocumentJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly ILogger<DynamicCallerStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _cacheLock = new();
    private List<DynamicCallerEntry> _cache = new();

    public string Path => _path;

    public DynamicCallerStore(string path, ILogger<DynamicCallerStore> logger)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The dynamic caller file path must not be empty.", nameof(path));
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Reads the current entries. The file is read at most once per changed
    /// write; missing or invalid files log a warning and yield no dynamic
    /// callers (fail closed) instead of failing the request path.
    /// </summary>
    public async Task<IReadOnlyList<DynamicCallerEntry>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            lock (_cacheLock)
            {
                return _cache.Select(entry => Clone(entry)).ToList();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates a new caller or replaces an existing one with the given key.</summary>
    public async Task<DynamicCallerEntry> UpsertAsync(
        string name, string apiKey, string createdBy, string note, CancellationToken ct = default)
    {
        Validate(name, apiKey);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            DynamicCallerEntry entry;
            lock (_cacheLock)
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                var existing = _cache.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    entry = new DynamicCallerEntry
                    {
                        Name = name,
                        ApiKey = apiKey,
                        Enabled = true,
                        Note = note,
                        CreatedAtUtc = now,
                        CreatedBy = createdBy,
                        UpdatedAtUtc = now
                    };
                    _cache.Add(entry);
                }
                else
                {
                    existing.ApiKey = apiKey;
                    existing.Note = note;
                    existing.UpdatedAtUtc = now;
                    entry = existing;
                }
                SaveLocked();
            }
            _logger.LogInformation(
                "Dynamic caller store: caller {CallerName} created/rotated by {Actor}", name, createdBy);
            return Clone(entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Updates the management fields (enabled/note) of an existing caller.</summary>
    public async Task<DynamicCallerEntry?> UpdateAsync(
        string name, bool? enabled, string? note, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            lock (_cacheLock)
            {
                var existing = _cache.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is null) return null;
                if (enabled is { } value) existing.Enabled = value;
                if (note is not null) existing.Note = note;
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                SaveLocked();
                return Clone(existing);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes a caller; returns the removed entry or null when absent.</summary>
    public async Task<DynamicCallerEntry?> RemoveAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            lock (_cacheLock)
            {
                var existing = _cache.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is null) return null;
                _cache.Remove(existing);
                SaveLocked();
                return Clone(existing);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Loads the file into the cache when it has not been loaded yet. Called
    /// under <see cref="_lock"/>; a missing file leaves the cache empty (the
    /// file appears on the first write).
    /// </summary>
    private void EnsureLoaded()
    {
        lock (_cacheLock)
        {
            if (_cache.Count > 0 || _loaded) return;
            try
            {
                if (!File.Exists(_path))
                {
                    _loaded = true;
                    return;
                }
                using var stream = File.OpenRead(_path);
                var document = JsonSerializer.Deserialize<DynamicCallerDocument>(stream, DocumentJson);
                _cache = document?.Callers?
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.ApiKey))
                    .ToList() ?? new List<DynamicCallerEntry>();
                _loaded = true;
                _logger.LogInformation(
                    "Dynamic caller store: loaded {Count} caller(s) from {Path}", _cache.Count, _path);
            }
            catch (Exception ex)
            {
                // Fail closed: an unreadable store contributes no callers, and
                // the static configuration keeps serving.
                _cache = new List<DynamicCallerEntry>();
                _loaded = true;
                _logger.LogError(ex,
                    "Dynamic caller store: failed to read {Path}; dynamic callers are not active until the file is fixed",
                    _path);
            }
        }
    }

    private bool _loaded;

    /// <summary>Persists the cache atomically. Called under <see cref="_lock"/> and <see cref="_cacheLock"/>.</summary>
    private void SaveLocked()
    {
        var document = new DynamicCallerDocument { Callers = _cache.Select(Clone).ToList() };
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(document, DocumentJson);
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private static void Validate(string name, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Any(char.IsControl) || name.Any(char.IsWhiteSpace))
            throw new ArgumentException("The caller name must be 1-128 characters without whitespace.");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 256 || apiKey.Any(char.IsControl))
            throw new ArgumentException("The caller api key must be 1-256 characters.");
    }

    private static DynamicCallerEntry Clone(DynamicCallerEntry entry) => new()
    {
        Name = entry.Name,
        ApiKey = entry.ApiKey,
        Enabled = entry.Enabled,
        Note = entry.Note,
        CreatedAtUtc = entry.CreatedAtUtc,
        CreatedBy = entry.CreatedBy,
        UpdatedAtUtc = entry.UpdatedAtUtc
    };
}
