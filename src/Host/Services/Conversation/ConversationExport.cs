using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Emenda.HttpContracts;
using Emenda.Common.Oss;

namespace Emenda.Host.Services.Conversation;

public sealed record ConversationCollectionResponse(string Status, ConversationExport? Result, string? Fingerprint = null);
public sealed record ConversationExport(int Version, Guid ConversationId, ConversationExportEvent[] Events, ConversationExportAsset[] Assets, ConversationExportSource[] Sources)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Validate(string owner)
    {
        if (Version != 1 || ConversationId == Guid.Empty || Events is null or { Length: > 10 } || Assets is null or { Length: > 10 } || Sources is null or { Length: > 80 }) Invalid();
        if (Events!.Any(e => e is null || e.Question is null) || Assets!.Any(a => a is null) || Sources!.Any(s => s is null)) Invalid();
        if (Events.Select(e => e.Question.Id).Distinct().Count() != Events.Length || Assets!.Select(a => a.Id).Distinct().Count() != Assets.Length || Sources!.Select(s => s.Position).Distinct().Count() != Sources.Length) Invalid();
        foreach (var source in Sources!) if (source.Position is < 0 or > 79 || source.Content is null || source.Content.EnumerateRunes().Count() > 2000 || source.Image?.Length > 6_000_000) Invalid();
        foreach (var asset in Assets!)
            if (asset.Id == Guid.Empty || asset.Width <= 0 || asset.Height <= 0 || asset.Box is null || !asset.Box.Valid || asset.Image is null || asset.Image.Length > 6_000_000 || !asset.Image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
                || !Sources!.Any(s => s.Position == asset.SourcePosition && s.Image != null)) Invalid();
        foreach (var item in Events!)
        {
            var q = item.Question;
            if (item.Position is < 0 or > 79 || q is null || q.Id == Guid.Empty || q.Subject is < 0 or > 9 || string.IsNullOrWhiteSpace(q.Statement) || q.Statement.Length > 8000
                || q.SourcePositions is null or { Length: < 1 or > 10 } || q.AssetIds is null or { Length: > 10 } || q.KnowledgePoints is null or { Length: > 10 }
                || q.KnowledgePoints.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200) || q.Evidence?.Length > 8000 || q.ReferenceAnswer?.Length > 16000
                || q.Outcome is not ("unknown" or "incorrect" or "correct")) Invalid();
            var expected = Hash($"{Hash($"{owner}\n{ConversationId:D}\n{item.Position}")}\n{q!.Id:D}");
            if (item.Id != expected || q.SourcePositions!.Any(p => !Sources!.Any(s => s.Position == p)) || q.AssetIds!.Any(id => !Assets!.Any(a => a.Id == id && q.SourcePositions.Contains(a.SourcePosition)))) Invalid();
            if (q.SourcePositions.Any(p => Sources!.Single(s => s.Position == p).Image != null && !Assets!.Any(a => a.SourcePosition == p && q.AssetIds.Contains(a.Id)))) Invalid();
            if (q.Attempt is { } attempt)
            {
                var source = Sources!.SingleOrDefault(s => s.Position == attempt.Position);
                if (source == null || string.IsNullOrWhiteSpace(attempt.Quote) || attempt.Quote.Length > 4000) Invalid();
                if (attempt.AssetId is { } assetId ? !q.AssetIds.Contains(assetId) || !Assets!.Any(a => a.Id == assetId && a.SourcePosition == attempt.Position)
                    : !source!.Content.Contains(attempt.Quote, StringComparison.Ordinal)) Invalid();
            }
            if ((q.Outcome == "incorrect" || q.RetractsMistake) && (q.Attempt == null || string.IsNullOrWhiteSpace(q.Evidence))) Invalid();
            if (q.RetractsMistake && q.Outcome == "incorrect") Invalid();
        }
        if (Assets!.Sum(a => (long)a.Image.Length) + Sources!.Sum(s => (long)(s.Image?.Length ?? 0)) > 16 * 1024 * 1024) Invalid();
    }

    public static (byte[] Data, string MediaType, string Extension) Image(string image)
    {
        var comma = image.IndexOf(','); if (comma < 0) throw new InvalidDataException("Invalid conversation image.");
        var media = image[..comma] switch { "data:image/jpeg;base64" => ("image/jpeg", "jpg"), "data:image/png;base64" => ("image/png", "png"), "data:image/webp;base64" => ("image/webp", "webp"), _ => throw new InvalidDataException("Invalid conversation image.") };
        byte[] data; try { data = Convert.FromBase64String(image[(comma + 1)..]); } catch (FormatException ex) { throw new InvalidDataException("Invalid conversation image.", ex); }
        var actual = data.Length >= 12 && data[0] == 0xff && data[1] == 0xd8 ? "jpg"
            : data.Length >= 12 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 ? "png"
            : data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP" ? "webp" : "";
        if (actual != media.Item2) throw new InvalidDataException("Conversation image type does not match its content.");
        using var decoded = GuardedImageDecoder.LoadQuestion(data);
        return (data, media.Item1, media.Item2);
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("Invalid conversation evidence.");
}
public sealed record ConversationExportEvent(string Id, int Position, ConversationExportQuestion Question);
public sealed record ConversationExportQuestion(Guid Id, int Subject, string Statement, int[] SourcePositions, Guid[] AssetIds,
    ConversationStudentAttempt? Attempt, string Outcome, string? Evidence, string? ReferenceAnswer, string[] KnowledgePoints, bool RetractsMistake = false);
public sealed record ConversationExportAsset(Guid Id, int SourcePosition, ConversationExportBox Box, int Width, int Height, string Image);
public sealed record ConversationExportBox(int X1, int Y1, int X2, int Y2)
{
    public bool Valid => X1 >= 0 && Y1 >= 0 && X1 < X2 && Y1 < Y2 && X2 <= 1000 && Y2 <= 1000;
}
public sealed record ConversationExportSource(int Position, string Content, string? Image);
