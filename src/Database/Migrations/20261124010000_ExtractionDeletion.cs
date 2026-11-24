using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(ExtractionDeletionSchema.Migration)]
public sealed class ExtractionDeletion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ExtractionDeletionSchema.Install);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'extraction_deletion_irreversible'; END $$;");
}
