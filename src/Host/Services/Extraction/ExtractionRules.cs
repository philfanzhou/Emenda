using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Extraction;

internal static class ExtractionRules
{
    internal const int MaxAttempts = 5;
    internal const int MaxSources = 30;
    internal const int MaxImageBytes = 15 * 1024 * 1024;
    internal const int MaxCropBytes = 64 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new ExtractionException("invalid_retained_state", 503);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Hash(byte[] bytes) => Hash((ReadOnlySpan<byte>)bytes);
    internal static string Hash<T>(T value) => Hash((ReadOnlySpan<byte>)JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static Guid Identity(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    internal static long LockKey(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);

    /// <summary>Newly covering more than this share of another question's region needs clarification.</summary>
    internal const double AdjustmentOverlapLimit = 0.2;

    /// <summary>
    /// Accepts a model adjustment only when it changes the selected question alone: same identity, subject,
    /// printed number, grade and incorrect outcome, no split, merge or new record, and no crop reaching into
    /// another question.
    /// </summary>
    internal static ExtractionQuestionDraft Adjustment(List<ExtractionQuestionDraft> current, ExtractionQuestionDraft target,
        List<ExtractionQuestionDraft> proposed, IReadOnlyCollection<ExtractionSource> sources)
    {
        if (proposed.Count != 1 || proposed[0] is not { } adjusted || adjusted.Id != target.Id
            || adjusted.Subject != target.Subject || adjusted.Outcome != "incorrect")
            throw new ExtractionException("adjustment_out_of_scope", 422);
        // An adjustment changes crops and content; labels and grades are corrected through the question PUT.
        adjusted = adjusted with { Grade = target.Grade, QuestionNumber = target.QuestionNumber };
        try { ValidateQuestions(current.Select(q => q.Id == target.Id ? adjusted : q).ToList(), sources, automatic: false); }
        catch (ExtractionException error) when (error.Code == "duplicate_question")
        { throw new ExtractionException("adjustment_out_of_scope", 422); }
        catch (ExtractionException) { throw new ExtractionException("invalid_analysis_response", 502); }
        foreach (var region in current.Where(q => q.Id != target.Id).SelectMany(q => q.Regions))
        {
            // Only coverage the adjustment adds counts, summed over all of its regions; an existing
            // shared boundary is not a new claim.
            var added = CoveredArea(adjusted.Regions, region) - CoveredArea(target.Regions, region);
            if (added > AdjustmentOverlapLimit * (region.X1 - region.X0) * (region.Y1 - region.Y0))
                throw new ExtractionException("clarification_required", 422);
        }
        return adjusted;
    }

    /// <summary>Area of the union of <paramref name="regions"/> inside <paramref name="within"/>.</summary>
    private static double CoveredArea(IEnumerable<ExtractionRegion> regions, ExtractionRegion within)
    {
        var clipped = regions.Where(r => r.SourceId == within.SourceId)
            .Select(r => (X0: Math.Max(r.X0, within.X0), Y0: Math.Max(r.Y0, within.Y0), X1: Math.Min(r.X1, within.X1), Y1: Math.Min(r.Y1, within.Y1)))
            .Where(r => r.X0 < r.X1 && r.Y0 < r.Y1).ToList();
        var xs = clipped.SelectMany(r => new[] { r.X0, r.X1 }).Distinct().Order().ToList();
        var ys = clipped.SelectMany(r => new[] { r.Y0, r.Y1 }).Distinct().Order().ToList();
        var area = 0d;
        for (var i = 0; i + 1 < xs.Count; i++)
            for (var j = 0; j + 1 < ys.Count; j++)
            {
                var (x, y) = ((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2);
                if (clipped.Any(r => r.X0 <= x && x <= r.X1 && r.Y0 <= y && y <= r.Y1))
                    area += (xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
            }
        return area;
    }

    internal static void ValidateUpload(ExtractionUpload upload)
    {
        if (upload is null || upload.Grade is < 1 or > 12 || string.IsNullOrWhiteSpace(upload.SourceKey) || upload.SourceKey.Length > 64 ||
            upload.SourceKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ExtractionException("invalid_request");
        ValidateImages(upload.StudentId, upload.Images);
    }

    internal static void ValidateImages(string studentId, List<ExtractionImage> images)
    {
        if (string.IsNullOrWhiteSpace(studentId) || studentId.Length > 36 ||
            images is null || images.Count is < 1 or > MaxSources ||
            images.Any(i => i is null || i.Content is null || i.Content.Length is 0 or > MaxImageBytes ||
                i.ContentType is not ("image/jpeg" or "image/png")))
            throw new ExtractionException("invalid_request");
    }

    internal static bool ValidQuestionNumber(string? value) => value is null ||
        value.Length is >= 1 and <= 64 && value == value.Trim() && !value.Any(char.IsControl);

    internal static void ValidateQuestions(List<ExtractionQuestionDraft>? questions,
        IReadOnlyCollection<ExtractionSource> sources, bool automatic)
    {
        if (questions is null ||
            questions.Any(q => q is null) || questions.Select(q => q.Id).Distinct().Count() != questions.Count)
            throw new ExtractionException("invalid_questions");
        var sourceIds = sources.Select(s => s.Id).ToHashSet();
        foreach (var q in questions)
        {
            if (q.Id == Guid.Empty || !ValidQuestionNumber(q.QuestionNumber) || q.Subject is < 1 or > 9 || q.Grade is < 1 or > 12 || string.IsNullOrWhiteSpace(q.Statement) ||
                q.Statement.Length > 8000 || q.StudentAnswer?.Length > 8000 ||
                q.ReferenceAnswer is null || q.ReferenceAnswer.Length > 16000 ||
                q.Explanation is null || q.Explanation.Length > 16000 || q.Evidence is null || q.Evidence.Length > 16000 ||
                q.Outcome is not ("correct" or "incorrect" or "unknown") || q.Regions is null || q.Regions.Count is < 1 or > 32)
                throw new ExtractionException("invalid_questions");
            if (q.Outcome != "unknown" && (string.IsNullOrWhiteSpace(q.StudentAnswer) ||
                string.IsNullOrWhiteSpace(q.ReferenceAnswer) || string.IsNullOrWhiteSpace(q.Evidence)))
                throw new ExtractionException("judgment_requires_evidence");
            foreach (var r in q.Regions)
            {
                if (r is null || !sourceIds.Contains(r.SourceId) || !double.IsFinite(r.X0) || !double.IsFinite(r.Y0) ||
                    !double.IsFinite(r.X1) || !double.IsFinite(r.Y1) || r.X0 < 0 || r.Y0 < 0 || r.X1 > 1 ||
                    r.Y1 > 1 || r.X0 >= r.X1 || r.Y0 >= r.Y1)
                    throw new ExtractionException("invalid_region");
            }
            if (q.Regions.Distinct().Count() != q.Regions.Count) throw new ExtractionException("duplicate_region");
        }
        if (automatic && questions.Count > 0 && !sourceIds.SetEquals(questions.SelectMany(q => q.Regions).Select(r => r.SourceId)))
            throw new ExtractionException("source_omitted");
        var canonical = questions.Select(q => Hash(new { q.Subject, q.Statement, q.StudentAnswer,
            Regions = q.Regions.OrderBy(r => r.SourceId).ThenBy(r => r.X0).ThenBy(r => r.Y0).ThenBy(r => r.X1).ThenBy(r => r.Y1) })).ToList();
        if (canonical.Distinct().Count() != canonical.Count) throw new ExtractionException("duplicate_question");
    }
}
