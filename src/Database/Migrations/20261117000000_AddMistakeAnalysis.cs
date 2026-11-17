using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(MistakeAnalysisSchema.Migration)]
public sealed class AddMistakeAnalysis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MistakeAnalysisSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MistakeAnalysisSchema.Down);
}
