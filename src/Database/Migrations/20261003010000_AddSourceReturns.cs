using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Ruoyu.Study.Mistake.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(SourceReturnSchema.Migration)]
public sealed class AddSourceReturns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SourceReturnSchema.Tables + SourceReturnSchema.Guards);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SourceReturnSchema.Down);
}
