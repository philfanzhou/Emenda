using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext)), Migration(ConversationMistakeSchema.Migration)]
public sealed class AddConversationMistakes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ConversationMistakeSchema.Up);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ConversationMistakeSchema.Down);
}
