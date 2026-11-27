using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(PracticeRemovalSchema.Migration)]
public sealed class RetireMistakePractice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("mistake_practice");
        migrationBuilder.Sql("CREATE OR REPLACE FUNCTION guard_extraction_object_reference() RETURNS trigger LANGUAGE plpgsql AS $guard$"
            + PracticeRemovalSchema.ItemGuard + "$guard$");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'mistake_practice_retirement_irreversible'; END $$;");
}
