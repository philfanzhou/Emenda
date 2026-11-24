using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(WorksheetAnalysisSchema.Migration)]
public sealed class WorksheetAnalysis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("analysis_snapshot", "mistake_item", type: "jsonb", nullable: true);
        migrationBuilder.Sql(WorksheetAnalysisSchema.Backfill);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("analysis_snapshot", "mistake_item");
}
