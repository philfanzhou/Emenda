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

    internal static void ValidateUpload(ExtractionUpload upload)
    {
        if (upload is null || string.IsNullOrWhiteSpace(upload.StudentId) || upload.StudentId.Length > 36 ||
            upload.Grade is < 1 or > 12 || string.IsNullOrWhiteSpace(upload.SourceKey) || upload.SourceKey.Length > 64 ||
            upload.SourceKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) ||
            upload.Images is null || upload.Images.Count is < 1 or > MaxSources ||
            upload.Images.Any(i => i is null || i.Content is null || i.Content.Length is 0 or > MaxImageBytes ||
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
            if (q.Id == Guid.Empty || !ValidQuestionNumber(q.QuestionNumber) || q.Subject is < 1 or > 9 || string.IsNullOrWhiteSpace(q.Statement) ||
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
