using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.Mistake.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveImageFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mistake_image_fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mistake_image_fingerprint",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    image_index = table.Column<int>(type: "integer", nullable: false),
                    oss_path = table.Column<string>(type: "text", nullable: false),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false)
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
        }
    }
}
