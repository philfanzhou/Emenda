namespace Ruoyu.Study.Mistake.Database;

/// <summary>
/// Outcome of <see cref="DatabaseInitializer.UpgradeToTargetAsync"/>. Version 0 means an empty database;
/// no steps means the database was already at the target schema and nothing was written.
/// </summary>
public sealed record DatabaseUpgradeResult(int FromVersion, int ToVersion, IReadOnlyList<string> Steps)
{
    public bool Upgraded => Steps.Count > 0;
}
