using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

/// <summary>
/// Drops the StorageReferences v1 snapshot table (ADR-0030 step 4). Snapshots expire and are not
/// business data, so dropping a non-empty table loses nothing; Down recreates the empty table.
/// </summary>
[DbContext(typeof(MistakeDbContext))]
[Migration(StorageReferenceRemovalSchema.Migration)]
public sealed class DropStorageReferenceSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(StorageReferenceRemovalSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(StorageReferenceRemovalSchema.Down);
}
