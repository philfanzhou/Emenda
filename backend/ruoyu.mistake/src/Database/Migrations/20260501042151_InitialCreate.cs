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
                name: "mistake",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    subject = table.Column<int>(type: "integer", nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    question_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mistake", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mistake_detail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_images = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mistake_detail", x => x.id);
                    table.ForeignKey(
                        name: "FK_mistake_detail_mistake_mistake_id",
                        column: x => x.mistake_id,
                        principalTable: "mistake",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mistake_image_fingerprint",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    mistake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    image_index = table.Column<int>(type: "integer", nullable: false),
                    oss_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mistake_image_fingerprint", x => x.id);
                    table.ForeignKey(
                        name: "FK_mistake_image_fingerprint_mistake_mistake_id",
                        column: x => x.mistake_id,
                        principalTable: "mistake",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mistake_practice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    practice_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    image_paths = table.Column<string>(type: "text", nullable: false),
                    student_answer = table.Column<string>(type: "text", nullable: true),
                    is_correct = table.Column<bool>(type: "boolean", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mistake_practice", x => x.id);
                    table.ForeignKey(
                        name: "FK_mistake_practice_mistake_mistake_id",
                        column: x => x.mistake_id,
                        principalTable: "mistake",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_process_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_process_log_mistake_mistake_id",
                        column: x => x.mistake_id,
                        principalTable: "mistake",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mistake_created_at",
                table: "mistake",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_date",
                table: "mistake",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_status",
                table: "mistake",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_student_id",
                table: "mistake",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_student_id_subject_grade_status",
                table: "mistake",
                columns: new[] { "student_id", "subject", "grade", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_mistake_type",
                table: "mistake",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_detail_mistake_id",
                table: "mistake_detail",
                column: "mistake_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mistake_image_fingerprint_hash_student_id",
                table: "mistake_image_fingerprint",
                columns: new[] { "hash", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mistake_image_fingerprint_mistake_id",
                table: "mistake_image_fingerprint",
                column: "mistake_id");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_image_fingerprint_oss_path",
                table: "mistake_image_fingerprint",
                column: "oss_path");

            migrationBuilder.CreateIndex(
                name: "IX_mistake_practice_mistake_id",
                table: "mistake_practice",
                column: "mistake_id");

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
                name: "IX_process_log_mistake_id_created_at",
                table: "process_log",
                columns: new[] { "mistake_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mistake_detail");

            migrationBuilder.DropTable(
                name: "mistake_image_fingerprint");

            migrationBuilder.DropTable(
                name: "mistake_practice");

            migrationBuilder.DropTable(
                name: "process_log");

            migrationBuilder.DropTable(
                name: "mistake");
        }
    }
}
