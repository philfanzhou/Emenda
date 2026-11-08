using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(SourceTargetSchema.Migration)]
public sealed class AddSourceTargets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SourceTargetSchema.Tables+SourceTargetSchema.Guards);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SourceTargetSchema.Down);
}
