using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.Mistake.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentIdToProcessLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "student_id",
                table: "process_log",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.Sql("ALTER TABLE mistake_practice ALTER COLUMN image_paths TYPE jsonb USING image_paths::jsonb");

            migrationBuilder.CreateIndex(
                name: "IX_process_log_student_id",
                table: "process_log",
                column: "student_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_process_log_student_id",
                table: "process_log");

            migrationBuilder.DropColumn(
                name: "student_id",
                table: "process_log");

            migrationBuilder.Sql("ALTER TABLE mistake_practice ALTER COLUMN image_paths TYPE text USING image_paths::text");
        }
    }
}
