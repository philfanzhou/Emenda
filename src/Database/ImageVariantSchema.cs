using Microsoft.EntityFrameworkCore;

namespace Emenda.Database;

/// <summary>
/// Schema v23: a durable backlog of stored images whose display variants (thumbnails) must exist.
/// The upgrade seeds every image a mistake currently shows, so existing uploads and confirmed
/// mistakes are backfilled automatically; new writers enqueue their parents as they publish them.
/// </summary>
public static class ImageVariantSchema
{
    public const string Migration = "20261125000000_ImageVariantBacklog";
    public const string Table = "image_variant_task";

    public const string Up = """
        CREATE TABLE image_variant_task (path text NOT NULL, attempts integer NOT NULL, next_attempt timestamp with time zone NOT NULL, CONSTRAINT "PK_image_variant_task" PRIMARY KEY (path));
        CREATE INDEX "IX_image_variant_task_next_attempt" ON image_variant_task (next_attempt);
        INSERT INTO image_variant_task(path,attempts,next_attempt)
        SELECT DISTINCT p, 0, clock_timestamp() FROM (
            SELECT s->>'path' AS p FROM extraction_job j CROSS JOIN LATERAL jsonb_array_elements(j.sources_json) s
            UNION ALL
            SELECT i->>'path' FROM extraction_revision r JOIN extraction_job j ON j.id=r.job_id AND j.version=r.version
                CROSS JOIN LATERAL jsonb_array_elements(r.snapshot_json->'questions') q
                CROSS JOIN LATERAL jsonb_array_elements(q->'images') i
            UNION ALL
            SELECT r->>'sourceImagePath' FROM mistake_item m CROSS JOIN LATERAL jsonb_array_elements(m.source_regions) r
        ) paths WHERE p LIKE 'mistakes/%' AND p NOT LIKE '%\_thumbnail.jpg' AND p NOT LIKE '%\_small.jpg' AND p NOT LIKE '%\_medium.jpg'
        ON CONFLICT (path) DO NOTHING;
        """;
    // Forward-only like v20 and v22: a downgrade attempt fails before it changes anything.
    public const string Down = "DO $$ BEGIN RAISE EXCEPTION 'image_variant_backlog_irreversible'; END $$;";

    internal static readonly string[] Tables = [Table];
    internal static readonly string[] Columns =
    [
        "image_variant_task|path|text|N",
        "image_variant_task|attempts|integer|N",
        "image_variant_task|next_attempt|timestamp with time zone|N"
    ];
    internal static readonly string[] Indexes =
    [
        "PK_image_variant_task|image_variant_task|path|U",
        "IX_image_variant_task_next_attempt|image_variant_task|next_attempt|N"
    ];
    internal static readonly string[] Constraints = ["PK_image_variant_task|p|path|-|-|-|V"];

    /// <summary>Records stored images whose variants the background worker must ensure. Idempotent.</summary>
    public static Task EnqueueAsync(MistakeDbContext db, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var values = paths.Where(p => p.StartsWith("mistakes/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0) return Task.CompletedTask;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO image_variant_task(path,attempts,next_attempt)
            SELECT p, 0, clock_timestamp() FROM unnest({values}) p ON CONFLICT (path) DO NOTHING
            """, ct);
    }
}
