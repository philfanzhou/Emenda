using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(SourceHandoffSchema.Migration)]
public sealed class AddSourceHandoffs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql(SourceHandoffSchema.Tables+SourceHandoffSchema.Guards);
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql(SourceHandoffSchema.Down);
}
