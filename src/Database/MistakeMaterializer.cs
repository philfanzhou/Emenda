using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Database;

/// <summary>Values of <c>mistake_item.source_kind</c> and readers of the unified source columns (schema v24).</summary>
public static class MistakeSource
{
    public const string Extraction = Emenda.Domain.Models.MistakeSourceKinds.Extraction;
    public const string Conversation = Emenda.Domain.Models.MistakeSourceKinds.Conversation;
    public const string Legacy = Emenda.Domain.Models.MistakeSourceKinds.Legacy;

    internal const string EmptyContent = "{}";

    /// <summary>The extraction task of an extraction mistake, from <c>source_detail.jobId</c>.</summary>
    public static Guid? ExtractionJobId(MistakeItemEntity item)
    {
        if (item.SourceKind != Extraction || item.SourceDetail is null) return null;
        using var detail = JsonDocument.Parse(item.SourceDetail);
        return detail.RootElement.ValueKind == JsonValueKind.Object
            && detail.RootElement.TryGetProperty("jobId", out var job) && job.ValueKind == JsonValueKind.String
            && Guid.TryParse(job.GetString(), out var id) ? id : null;
    }

    /// <summary>The archived file manifest an extraction mistake carries after its task was deleted, as raw JSON.</summary>
    public static string? RetainedFiles(MistakeItemEntity item)
    {
        if (item.SourceKind != Extraction || item.SourceDetail is null) return null;
        using var detail = JsonDocument.Parse(item.SourceDetail);
        return detail.RootElement.ValueKind == JsonValueKind.Object
            && detail.RootElement.TryGetProperty("retainedFiles", out var files) ? files.GetRawText() : null;
    }
}

/// <summary>
/// Writes the unified source shape of every <c>mistake_item</c> row an EF save inserts or updates.
/// <para>
/// Both intake pipelines (extraction publication and conversation collection) and every other EF writer
/// keep writing their source-specific columns; this component derives <c>source_kind</c>,
/// <c>source_caller</c>, <c>content</c> and <c>source_detail</c> from them in one place, so the tracked entity
/// carries the same values the database stores. The trigger <c>trg_mistake_item_source</c> applies the identical
/// mapping inside PostgreSQL (and covers raw SQL writers); <c>MistakeMaterializerParityPostgresTests</c> keeps the
/// two in lockstep. Keep this class and <see cref="UnifiedMistakeSchema"/>'s function body in sync.
/// </para>
/// </summary>
public static partial class MistakeMaterializer
{
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobText();

    internal static void Materialize(MistakeDbContext db)
    {
        var items = Pending(db);
        if (items.Count == 0) return;
        var callers = JobCallers(db, items, ids => db.ExtractionJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => ids.Contains(j.Id)).Select(j => new { j.Id, j.Caller }).ToList()
            .Select(j => (j.Id, j.Caller)).ToList());
        foreach (var item in items) Apply(item, callers);
    }

    internal static async Task MaterializeAsync(MistakeDbContext db, CancellationToken ct)
    {
        var items = Pending(db);
        if (items.Count == 0) return;
        var missing = items.Select(JobId).OfType<Guid>().Distinct()
            .Where(id => db.ExtractionJobs.Local.All(j => j.Id != id)).ToArray();
        var stored = missing.Length == 0 ? [] : (await db.ExtractionJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => missing.Contains(j.Id)).Select(j => new { j.Id, j.Caller }).ToListAsync(ct))
            .Select(j => (j.Id, j.Caller)).ToList();
        var callers = JobCallers(db, items, _ => stored);
        foreach (var item in items) Apply(item, callers);
    }

    private static List<MistakeItemEntity> Pending(MistakeDbContext db) => db.ChangeTracker.Entries<MistakeItemEntity>()
        .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity).ToList();

    private static Dictionary<Guid, string?> JobCallers(MistakeDbContext db, List<MistakeItemEntity> items,
        Func<Guid[], List<(Guid Id, string Caller)>> load)
    {
        var ids = items.Select(JobId).OfType<Guid>().Distinct().ToArray();
        var callers = new Dictionary<Guid, string?>();
        foreach (var job in db.ExtractionJobs.Local.Where(j => ids.Contains(j.Id))) callers[job.Id] = Caller(job.Caller);
        var missing = ids.Where(id => !callers.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            foreach (var (id, caller) in load(missing)) callers[id] = Caller(caller);
        return callers;

        static string? Caller(string? caller) => caller is not null && caller.StartsWith("caller:", StringComparison.Ordinal)
            ? caller["caller:".Length..] : null;
    }

    private static bool IsExtraction(MistakeItemEntity item) =>
        item.ConversationSource is null && item.SourceUploadId?.StartsWith("extract:", StringComparison.Ordinal) == true;

    private static Guid? JobId(MistakeItemEntity item) =>
        IsExtraction(item) && item.SourceUploadId.Length >= 8 && JobText().IsMatch(item.SourceUploadId[8..])
            ? Guid.ParseExact(item.SourceUploadId[8..], "N") : null;

    /// <summary>Derives the unified columns of one row; <paramref name="jobCallers"/> maps task ids to product callers.</summary>
    internal static void Apply(MistakeItemEntity item, IReadOnlyDictionary<Guid, string?> jobCallers)
    {
        var evidence = item.ConversationSource is null ? null : JsonNode.Parse(item.ConversationSource);
        JsonNode? snapshot = null;
        if (item.ConversationSource is not null)
        {
            item.SourceKind = MistakeSource.Conversation;
            item.SourceCaller = item.ConversationCaller;
            item.SourceDetail = item.ConversationSource;
        }
        else if (IsExtraction(item))
        {
            item.SourceKind = MistakeSource.Extraction;
            snapshot = item.AnalysisSnapshot is null ? null : JsonNode.Parse(item.AnalysisSnapshot);
            var job = JobId(item);
            // A deleted task leaves only an owner hash, so the caller resolved earlier is kept.
            item.SourceCaller = (job is { } id ? jobCallers.GetValueOrDefault(id) : null) ?? item.SourceCaller;
            var detail = new JsonObject
            {
                ["jobId"] = job?.ToString("D"),
                ["questionId"] = item.QuestionId,
                ["sourceVersion"] = Field(snapshot, "sourceVersion").Value?.DeepClone(),
                ["retainedFiles"] = Field(snapshot, "retainedFiles").Value?.DeepClone()
            };
            item.SourceDetail = StripNulls(detail)!.ToJsonString();
        }
        else
        {
            item.SourceKind = MistakeSource.Legacy;
            item.SourceCaller = null;
            item.SourceDetail = null;
        }

        var images = new JsonArray();
        foreach (var region in item.SourceRegions ?? [])
        {
            var crop = Field(evidence, "images").Value is JsonArray candidates
                ? candidates.FirstOrDefault(f => f is JsonObject o && Text(o["path"]) == region.SourceImagePath
                    && o["assetId"] is JsonValue asset && asset.GetValueKind() == JsonValueKind.String)
                : null;
            JsonNode? box = evidence is not null
                ? (crop?["box"] is JsonArray cropBox ? cropBox.DeepClone() : null)
                : region.BoundingBox is { } b ? new JsonArray(b.X1, b.Y1, b.X2, b.Y2) : null;
            images.Add(new JsonObject
            {
                ["path"] = region.SourceImagePath,
                ["role"] = evidence is not null && crop is null ? "source" : "question",
                ["box"] = box
            });
        }
        var attempt = Field(evidence, "studentAttempt").Value;
        item.Content = new JsonObject
        {
            ["version"] = 1,
            ["statement"] = Coalesce(Field(evidence, "statement"), Field(snapshot, "statement")),
            ["studentAnswer"] = Coalesce(Field(attempt, "quote"), Field(snapshot, "studentAnswer")),
            ["referenceAnswer"] = Coalesce(Field(evidence, "referenceAnswer"), Field(snapshot, "referenceAnswer")),
            ["explanation"] = Field(snapshot, "explanation").Value?.DeepClone(),
            ["evidence"] = Coalesce(Field(evidence, "evidence"), Field(snapshot, "evidence")),
            ["knowledgePoints"] = Field(evidence, "knowledgePoints").Value is JsonArray points ? points.DeepClone() : new JsonArray(),
            ["images"] = images
        }.ToJsonString();
    }

    // jsonb's "->": a member of an object, or absent. A present JSON null is distinct from an absent member.
    private static (bool Present, JsonNode? Value) Field(JsonNode? node, string name) =>
        node is JsonObject o && o.TryGetPropertyValue(name, out var value) ? (true, value) : (false, null);

    private static JsonNode? Coalesce((bool Present, JsonNode? Value) first, (bool Present, JsonNode? Value) second) =>
        (first.Present ? first.Value : second.Value)?.DeepClone();

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        null => null,
        _ => node.ToJsonString()
    };

    // jsonb_strip_nulls: drops object members whose value is JSON null, recursively.
    private static JsonNode? StripNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Where(p => p.Value is null).Select(p => p.Key).ToArray()) o.Remove(name);
                foreach (var (_, value) in o) StripNulls(value);
                return o;
            case JsonArray a:
                foreach (var value in a) StripNulls(value);
                return a;
            default:
                return node;
        }
    }
}
