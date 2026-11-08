using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(SourceIntakeSchema.Migration)]
public sealed class AddSourceIntake : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SourceIntakeSchema.Tables + SourceIntakeSchema.Guards);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SourceIntakeSchema.Down);
}
