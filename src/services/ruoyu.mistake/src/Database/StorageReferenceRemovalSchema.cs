using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.Mistake.Database;

/// <summary>
/// Mistake's StorageReferences v1 snapshot table: added by <see cref="Added"/> (schema version 10)
/// and dropped by <see cref="Migration"/> (schema version 14) when the contract was retired with
/// Ruoyu.Admin (ADR-0030 step 4). Versions 10–13 still carry the table.
/// </summary>
public static class StorageReferenceRemovalSchema
{
    public const string Added = "20261003030001_AddStorageReferenceSnapshots";
    public const string Migration = "20261107000001_DropStorageReferenceSnapshots";

    /// <summary>Whether an exact schema version still carries the snapshot table.</summary>
    public static bool Present(int version) => version is >= 10 and < 14;

    public static string Up => RetiredStorageReferenceSchema.DropSql;
    public static string Down => RetiredStorageReferenceSchema.Sql;
}
