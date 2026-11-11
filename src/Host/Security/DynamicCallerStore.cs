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

    /// <summary>A durable tombstone suppressing any same-name configuration caller.</summary>
    public bool Deleted { get; set; }

    public string Note { get; set; } = string.Empty;

    public string CreatedAtUtc { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public string UpdatedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// File document of the dynamic caller store (version 2; version 1 remains readable).
/// </summary>
public sealed class DynamicCallerDocument
{
    [JsonRequired]
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonRequired]
    [JsonPropertyName("callers")]
    public List<DynamicCallerEntry> Callers { get; set; } = new();
}

/// <summary>
/// Caller credentials persisted in a JSON file under a configurable path
/// (default <c>/app/data/emenda-callers.json</c>, volume-mountable). Dynamic
/// entries override configuration by name, including disabled entries and
/// durable deletion tombstones. Bootstrap requires an explicitly provisioned
/// empty document; missing or invalid state never falls back to configuration.
/// The file is read once per process. Writes serialize on one async lock and
/// publish the new cache only after an atomic temp-file move succeeds.
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
    private List<DynamicCallerEntry> _cache = new();
    private bool _loaded;
    private DynamicCallerStoreUnavailableException? _loadFailure;

    public string Path => _path;

    public DynamicCallerStore(string path, ILogger<DynamicCallerStore> logger)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The dynamic caller file path must not be empty.", nameof(path));
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Reads entries including deletion tombstones. State is loaded once;
    /// unreadable or invalid state blocks both authentication and management
    /// until operators restore a valid file and restart the process.
    /// </summary>
    public async Task<IReadOnlyList<DynamicCallerEntry>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return _cache.Select(Clone).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates or rotates a caller; recreating a tombstone requires a new key.</summary>
    public async Task<DynamicCallerEntry> UpsertAsync(
        string name, string apiKey, string createdBy, string note, CancellationToken ct = default)
    {
        Validate(name, apiKey);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var next = _cache.Select(Clone).ToList();
            var entry = next.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (entry is null || entry.Deleted)
            {
                if (entry is not null) next.Remove(entry);
                entry = new DynamicCallerEntry
                {
                    Name = name,
                    ApiKey = apiKey,
                    Note = note,
                    CreatedAtUtc = now,
                    CreatedBy = createdBy,
                    UpdatedAtUtc = now
                };
                next.Add(entry);
            }
            else
            {
                entry.ApiKey = apiKey;
                entry.Note = note;
                entry.UpdatedAtUtc = now;
            }
            Save(next);
            _logger.LogInformation(
                "Dynamic caller store: caller {CallerName} created/rotated by {Actor}", name, createdBy);
            return Clone(entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Updates enabled/note fields; deletion tombstones cannot be re-enabled.</summary>
    public async Task<DynamicCallerEntry?> UpdateAsync(
        string name, bool? enabled, string? note, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var next = _cache.Select(Clone).ToList();
            var entry = next.FirstOrDefault(c => !c.Deleted &&
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            if (enabled is { } value) entry.Enabled = value;
            if (note is not null) entry.Note = note;
            entry.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            Save(next);
            return Clone(entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Replaces a caller with a durable tombstone, never exposing a static predecessor.</summary>
    public async Task<DynamicCallerEntry?> RemoveAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var next = _cache.Select(Clone).ToList();
            var entry = next.FirstOrDefault(c => !c.Deleted &&
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            var removed = Clone(entry);
            entry.Deleted = true;
            entry.Enabled = false;
            entry.ApiKey = string.Empty;
            entry.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            Save(next);
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loadFailure is not null) throw _loadFailure;
        if (_loaded) return;
        try
        {
            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize<DynamicCallerDocument>(stream, DocumentJson);
            if (document is null || document.Version is not (1 or 2) || document.Callers is null)
                throw new InvalidDataException("Invalid caller document version or callers collection.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Callers)
            {
                if (entry is null || !names.Add(entry.Name))
                    throw new InvalidDataException("Caller entries must be non-null and uniquely named.");
                Validate(entry.Name, entry.Deleted ? "tombstone" : entry.ApiKey);
                if (entry.Deleted && (document.Version != 2 || entry.Enabled || entry.ApiKey != string.Empty))
                    throw new InvalidDataException("Invalid caller deletion tombstone.");
            }
            _cache = document.Callers;
            _loaded = true;
            _logger.LogInformation(
                "Dynamic caller store: loaded {Count} caller record(s) from {Path}", _cache.Count, _path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _logger.LogError(ex,
                "Dynamic caller store: cannot load {Path}; authentication and management are blocked. Restore the file and restart; never bootstrap over lost revocation state.",
                _path);
            _loadFailure = new DynamicCallerStoreUnavailableException();
            throw _loadFailure;
        }
    }

    /// <summary>Persists before publishing the next cache; a failed write leaves current keys unchanged.</summary>
    private void Save(List<DynamicCallerEntry> next)
    {
        try
        {
            var document = new DynamicCallerDocument { Callers = next };
            var temp = _path + ".tmp";
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, fileOptions))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, document, DocumentJson);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _cache = next;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Dynamic caller store: failed to persist {Path}; requested change was not published", _path);
            throw new DynamicCallerStoreUnavailableException();
        }
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
        Deleted = entry.Deleted,
        Note = entry.Note,
        CreatedAtUtc = entry.CreatedAtUtc,
        CreatedBy = entry.CreatedBy,
        UpdatedAtUtc = entry.UpdatedAtUtc
    };
}
