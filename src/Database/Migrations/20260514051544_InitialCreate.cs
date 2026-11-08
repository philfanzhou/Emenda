using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.Mistake.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mistake_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    subject = table.Column<int>(type: "integer", nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false),
                    source_upload_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_regions = table.Column<string>(type: "jsonb", nullable: false),
                    review_status = table.Column<int>(type: "integer", nullable: false),
                    reviewer_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "text", nullable: true),
                    question_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mistake_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mistake_practice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    practice_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    image_paths = table.Column<string>(type: "text", nullable: false),
                    student_answer = table.Column<string>(type: "text", nullable: true),
                    is_correct = table.Column<bool>(type: "boolean", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mistake_practice", x => x.id);
                    table.ForeignKey(
                        name: "FK_mistake_practice_mistake_item_mistake_item_id",
                        column: x => x.mistake_item_id,
                        principalTable: "mistake_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_process_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_process_log_mistake_item_mistake_item_id",
                        column: x => x.mistake_item_id,
                        principalTable: "mistake_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mistake_item_created_at",
                table: "mistake_item",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_item_review_status",
                table: "mistake_item",
                column: "review_status");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_item_source_upload_id",
                table: "mistake_item",
                column: "source_upload_id");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_item_student_id",
                table: "mistake_item",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_item_student_id_subject_grade_review_status",
                table: "mistake_item",
                columns: new[] { "student_id", "subject", "grade", "review_status" });

            migrationBuilder.CreateIndex(
                name: "IX_mistake_practice_mistake_item_id",
                table: "mistake_practice",
                column: "mistake_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_practice_practice_date",
                table: "mistake_practice",
                column: "practice_date");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_practice_student_id",
                table: "mistake_practice",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_process_log_created_at",
                table: "process_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_process_log_mistake_item_id_created_at",
                table: "process_log",
                columns: new[] { "mistake_item_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mistake_practice");

            migrationBuilder.DropTable(
                name: "process_log");

            migrationBuilder.DropTable(
                name: "mistake_item");
        }
    }
}
