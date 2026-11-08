using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Emenda.Common.Database;
namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(StorageReferenceRemovalSchema.Added)]
public sealed class AddStorageReferenceSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RetiredStorageReferenceSchema.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RetiredStorageReferenceSchema.DownSql);
}
