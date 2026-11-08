using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.Mistake.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddReanalyzeJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reanalyze_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    teacher_description = table.Column<string>(type: "text", nullable: true),
                    previous_root_cause = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    new_root_cause = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vl_duration_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reanalyze_job", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reanalyze_job_Status_CreatedAt",
                table: "reanalyze_job",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reanalyze_job_ReviewerId_Status",
                table: "reanalyze_job",
                columns: new[] { "reviewer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_reanalyze_job_CompletedAt",
                table: "reanalyze_job",
                column: "completed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "reanalyze_job");
        }
    }
}
