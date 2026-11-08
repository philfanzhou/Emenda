using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ruoyu.Study.Mistake.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration("20260929000001_RenameReviewCommentToRootCause")]
public sealed class RenameReviewCommentToRootCause : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameColumn("review_comment", "mistake_item", "root_cause");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameColumn("root_cause", "mistake_item", "review_comment");
}
