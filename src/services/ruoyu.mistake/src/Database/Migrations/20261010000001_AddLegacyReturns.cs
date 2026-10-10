using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ruoyu.Study.Mistake.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(LegacyReturnSchema.Migration)]
public sealed class AddLegacyReturns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(LegacyReturnSchema.Tables + LegacyReturnSchema.Guards);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(LegacyReturnSchema.Down);
}
