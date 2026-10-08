using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ruoyu.Study.Mistake.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(LegacyArchiveSchema.Migration)]
public sealed class AddLegacyArchive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(LegacyArchiveSchema.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(LegacyArchiveSchema.DownSql);
}
