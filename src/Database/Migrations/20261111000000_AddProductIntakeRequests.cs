using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration(ProductIntakeSchema.Migration)]
public sealed class AddProductIntakeRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ProductIntakeSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ProductIntakeSchema.Down);
}
