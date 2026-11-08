namespace Emenda.Host.Services.Layout;

/// <summary>
/// Startup snapshot of the StructaDoc layout capability switch, resolved once in Program.cs
/// from <c>Mistake:Layout:Enabled</c> before any database access or hosted service can start.
/// Default off; the value is fixed for the process lifetime and re-enabling requires a
/// restart with valid configuration.
/// </summary>
public sealed record LayoutCapabilityOptions(bool Enabled);
