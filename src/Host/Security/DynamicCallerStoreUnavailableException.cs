namespace Emenda.Host.Security;

/// <summary>Persisted caller state cannot be trusted or the requested change could not be committed.</summary>
public sealed class DynamicCallerStoreUnavailableException : Exception
{
    public DynamicCallerStoreUnavailableException()
        : base("Caller credential storage is unavailable. Restore the persisted state and restart; do not bootstrap over lost revocations.")
    {
    }
}
