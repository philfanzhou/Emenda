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

            migrationBuilder.AlterColumn<string>(
                name: "image_paths",
                table: "mistake_practice",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

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

            migrationBuilder.AlterColumn<string>(
                name: "image_paths",
                table: "mistake_practice",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb");
        }
    }
}
