using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database;

[DbContext(typeof(MistakeDbContext)), Migration(MigrationId)]
public sealed class ReleaseQuestionBinding : Migration
{
    internal const string MigrationId = "20261202000000_ReleaseQuestionBinding";

    // A question keeps its identity. Allow clearing its binding only after the pending item was deleted.
    internal static readonly string Guard = ExtractionDeletionSchema.RetentionGuard.Replace(
        "NEW.item_id IS DISTINCT FROM OLD.item_id)",
        "NEW.item_id IS DISTINCT FROM OLD.item_id AND (NEW.item_id IS NOT NULL OR " +
        "current_setting('emenda.withdraw_item', true) IS DISTINCT FROM OLD.item_id::text OR EXISTS " +
        "(SELECT 1 FROM public.mistake_item WHERE id=OLD.item_id)))", StringComparison.Ordinal);

    private static string Install(string body) =>
        $"CREATE OR REPLACE FUNCTION public.guard_extraction_retention() RETURNS trigger LANGUAGE plpgsql AS $guard${body}$guard$;";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Install(Guard));
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Install(ExtractionDeletionSchema.RetentionGuard));
}
