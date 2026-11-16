using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration("20261116000000_AddExtractions")]
public sealed class AddExtractions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionSchema.Down);
}
