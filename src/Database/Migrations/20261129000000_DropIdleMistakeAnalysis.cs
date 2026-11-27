using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(IdleAnalysisRemovalSchema.Migration)]
public sealed class DropIdleMistakeAnalysis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(IdleAnalysisRemovalSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(IdleAnalysisRemovalSchema.Down);
}
