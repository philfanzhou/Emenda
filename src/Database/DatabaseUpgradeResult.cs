namespace Emenda.Database;

/// <summary>
/// Outcome of <see cref="DatabaseInitializer.UpgradeToTargetAsync"/>. Version 0 means an empty database;
/// no steps means the database was already at the target schema and nothing was written.
/// </summary>
public sealed record DatabaseUpgradeResult(int FromVersion, int ToVersion, IReadOnlyList<string> Steps)
{
    public bool Upgraded => Steps.Count > 0;

    /// <summary>Rows, column values, invalid values and history rows copied into <c>legacy_archive</c>.</summary>
    public long ArchivedRows { get; init; }

    /// <summary>Invalid <c>mistake_practice.image_paths</c> values reset to an empty array after archiving.</summary>
    public int ResetImagePaths { get; init; }
}
