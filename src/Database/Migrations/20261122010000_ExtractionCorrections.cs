using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(ExtractionCorrectionsSchema.Migration)]
public sealed class ExtractionCorrections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionCorrectionsSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionCorrectionsSchema.Down);
}
