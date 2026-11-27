using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(UnifiedSourceContractionSchema.Migration)]
public sealed class DropSourceSpecificMistakeColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UnifiedSourceContractionSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UnifiedSourceContractionSchema.Down);
}
