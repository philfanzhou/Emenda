using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(RetiredIntakeTablesSchema.Migration)]
public sealed class DropRetiredReanalyzeAndIntakeTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RetiredIntakeTablesSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RetiredIntakeTablesSchema.Down);
}
