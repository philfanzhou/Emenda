namespace Ruoyu.Study.Mistake.Service.StudentIntegration;

/// <summary>
/// Fixed-at-startup Student integration capability. The Host resolves and
/// validates the underlying configuration before database access or any
/// hosted service can start; the value never changes for the process
/// lifetime and re-enabling requires a restart with valid configuration.
/// </summary>
public sealed class StudentCapabilityOptions
{
    /// <summary>
    /// Whether outgoing Student integration (upload polling and the reject
    /// review flow) is enabled. When false, reject reviews fail closed with
    /// HTTP 503 before any domain, OSS or Student change.
    /// </summary>
    public bool Enabled { get; init; }
}
