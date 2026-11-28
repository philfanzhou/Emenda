using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Database;

/// <summary>
/// Values of <c>mistake_item.source_kind</c>, and the only writers and readers of what each source owns in the
/// unified columns (schema v26 dropped <c>analysis_snapshot</c>, <c>conversation_source</c> and <c>conversation_caller</c>).
/// </summary>
public static class MistakeSource
{
    public const string Extraction = Emenda.Domain.Models.MistakeSourceKinds.Extraction;
    public const string Conversation = Emenda.Domain.Models.MistakeSourceKinds.Conversation;
    public const string Legacy = Emenda.Domain.Models.MistakeSourceKinds.Legacy;

    internal const string EmptyContent = "{}";

    // The text members an extraction analysis keeps in content (formerly analysis_snapshot, schema v21).
    private static readonly string[] AnalysisText = ["statement", "studentAnswer", "referenceAnswer", "explanation", "evidence"];

    /// <summary>Makes the row a conversation mistake owned by <paramref name="caller"/>, with its complete evidence snapshot.</summary>
    public static void SetConversation(MistakeItemEntity item, string? caller, string snapshot)
    {
        item.SourceKind = Conversation;
        item.SourceCaller = caller;
        item.SourceDetail = snapshot;
    }

    /// <summary>The evidence snapshot of a conversation mistake, byte for byte as collected; otherwise null.</summary>
    public static string? ConversationSnapshot(MistakeItemEntity item) => item.SourceKind == Conversation ? item.SourceDetail : null;

    /// <summary>
    /// Replaces an extraction mistake's analysis: <paramref name="snapshot"/> is the former <c>analysis_snapshot</c> JSON
    /// (<c>statement</c>, <c>studentAnswer</c>, <c>referenceAnswer</c>, <c>explanation</c>, <c>evidence</c>,
    /// <c>sourceVersion</c> and, after task deletion, <c>retainedFiles</c>). Its text goes to <c>content</c>, the rest to
    /// <c>source_detail</c>; null removes the analysis, as a NULL snapshot did.
    /// </summary>
    public static void SetAnalysis(MistakeItemEntity item, string? snapshot)
    {
        var analysis = snapshot is null ? null : JsonNode.Parse(snapshot) as JsonObject
            ?? throw new ArgumentException("An extraction analysis is a JSON object.", nameof(snapshot));
        var content = Object(item.Content);
        foreach (var name in AnalysisText) content[name] = analysis?[name]?.DeepClone();
        item.Content = content.ToJsonString();
        var detail = Object(item.SourceDetail);
        detail.Remove("sourceVersion");
        detail.Remove("retainedFiles");
        if (analysis?["sourceVersion"] is { } version) detail["sourceVersion"] = version.DeepClone();
        if (analysis?["retainedFiles"] is { } files) detail["retainedFiles"] = files.DeepClone();
        item.SourceDetail = detail.ToJsonString();
    }

    /// <summary>
    /// The analysis of an extraction mistake in the former <c>analysis_snapshot</c> shape, or null when it has none
    /// (every analysis carries its <c>sourceVersion</c>).
    /// </summary>
    public static string? Analysis(MistakeItemEntity item)
    {
        if (item.SourceKind == Conversation) return null;
        var detail = Object(item.SourceDetail);
        if (detail["sourceVersion"] is not { } version) return null;
        var content = Object(item.Content);
        var analysis = new JsonObject();
        foreach (var name in AnalysisText) analysis[name] = content[name]?.DeepClone();
        analysis["sourceVersion"] = version.DeepClone();
        if (detail["retainedFiles"] is { } files) analysis["retainedFiles"] = files.DeepClone();
        return analysis.ToJsonString();
    }

    /// <summary>Records the archived file manifest an extraction mistake keeps after its task is deleted.</summary>
    public static void SetRetainedFiles(MistakeItemEntity item, JsonNode files)
    {
        var detail = Object(item.SourceDetail);
        detail["retainedFiles"] = files.DeepClone();
        item.SourceDetail = detail.ToJsonString();
    }

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

    internal static JsonObject Object(string? json) =>
        json is not null && JsonNode.Parse(json) is JsonObject value ? value : new JsonObject();
}

/// <summary>
/// Completes the unified source shape of every <c>mistake_item</c> row an EF save inserts or updates.
/// <para>
/// Since schema v26 the unified columns are the record. The pipelines write what only they know through
/// <see cref="MistakeSource"/>: a conversation's kind, caller and evidence, an extraction's analysis text and
/// retained files. This component derives the rest the way the v24 trigger did: <c>extraction</c> or <c>legacy</c>
/// from the source id, the extraction task and its caller, <c>source_detail</c>'s task members, the content text of a
/// conversation (from its evidence) and the content images, which match <c>source_regions</c> one to one.
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
        item.SourceKind != MistakeSource.Conversation && item.SourceUploadId?.StartsWith("extract:", StringComparison.Ordinal) == true;

    private static Guid? JobId(MistakeItemEntity item) =>
        IsExtraction(item) && item.SourceUploadId.Length >= 8 && JobText().IsMatch(item.SourceUploadId[8..])
            ? Guid.ParseExact(item.SourceUploadId[8..], "N") : null;

    /// <summary>Completes the unified columns of one row; <paramref name="jobCallers"/> maps task ids to product callers.</summary>
    internal static void Apply(MistakeItemEntity item, IReadOnlyDictionary<Guid, string?> jobCallers)
    {
        var conversation = item.SourceKind == MistakeSource.Conversation;
        // A conversation keeps the caller and evidence its pipeline set; the evidence supplies its text and image roles.
        var evidence = conversation && item.SourceDetail is not null ? JsonNode.Parse(item.SourceDetail) : null;
        // An extraction's text is its own: the analysis its pipeline wrote into content.
        JsonNode? analysis = null;
        if (IsExtraction(item))
        {
            item.SourceKind = MistakeSource.Extraction;
            analysis = MistakeSource.Object(item.Content);
            var stored = MistakeSource.Object(item.SourceDetail);
            var job = JobId(item);
            // A deleted task leaves only an owner hash, so the caller resolved earlier is kept.
            item.SourceCaller = (job is { } id ? jobCallers.GetValueOrDefault(id) : null) ?? item.SourceCaller;
            var detail = new JsonObject
            {
                ["jobId"] = job?.ToString("D"),
                ["questionId"] = item.QuestionId,
                ["sourceVersion"] = Field(stored, "sourceVersion").Value?.DeepClone(),
                ["retainedFiles"] = Field(stored, "retainedFiles").Value?.DeepClone()
            };
            item.SourceDetail = StripNulls(detail)!.ToJsonString();
        }
        else if (!conversation)
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
            ["statement"] = Coalesce(Field(evidence, "statement"), Field(analysis, "statement")),
            ["studentAnswer"] = Coalesce(Field(attempt, "quote"), Field(analysis, "studentAnswer")),
            ["referenceAnswer"] = Coalesce(Field(evidence, "referenceAnswer"), Field(analysis, "referenceAnswer")),
            ["explanation"] = Field(analysis, "explanation").Value?.DeepClone(),
            ["evidence"] = Coalesce(Field(evidence, "evidence"), Field(analysis, "evidence")),
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
