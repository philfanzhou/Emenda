using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(ExtractionCallerRecoverySchema.Migration)]
public sealed class ExtractionCallerRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionCallerRecoverySchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionCallerRecoverySchema.Down);
}
