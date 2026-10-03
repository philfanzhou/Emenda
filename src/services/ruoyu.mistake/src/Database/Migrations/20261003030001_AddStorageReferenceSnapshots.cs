using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ruoyu.Study.Common.StorageReferences;
namespace Ruoyu.Study.Mistake.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration("20261003030001_AddStorageReferenceSnapshots")]
public sealed class AddStorageReferenceSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(StorageReferenceSchema.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(StorageReferenceSchema.DownSql);
}
