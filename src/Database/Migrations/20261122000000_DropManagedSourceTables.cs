using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(RetiredSourceSchema.Migration)]
public sealed class DropManagedSourceTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RetiredSourceSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RetiredSourceSchema.Down);
}
