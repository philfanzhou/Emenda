using System.Diagnostics.CodeAnalysis;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Extraction;

internal static class Rules
{
    internal const int MaxAttempts = 5;
    internal const int MaxSources = 30;
    internal const int MaxImageBytes = 15 * 1024 * 1024;
    internal const int MaxCropBytes = 64 * 1024 * 1024;

    /// <summary>Newly covering more than this share of another question's region needs clarification.</summary>
    internal const double AdjustmentOverlapLimit = 0.2;

    /// <summary>
    /// A question gets a mistake item and crops when it is incorrect, judged unknown (a person confirms it or removes it
    /// with "不是错题") or the user asked to collect it.
    /// </summary>
    internal static bool Collected(ExtractionQuestionDraft question) =>
        question.Outcome is "incorrect" or "unknown" || question.IncludedByRequest;

    /// <summary>
    /// Accepts a model adjustment only when it changes the selected question alone: same identity, subject,
    /// printed number, grade, outcome and requested collection, no split, merge or new record, and no crop reaching
    /// into another question.
    /// </summary>
    internal static ExtractionQuestionDraft Adjustment(List<ExtractionQuestionDraft> current, ExtractionQuestionDraft target,
        List<ExtractionQuestionDraft> proposed, IReadOnlyCollection<ExtractionSource> sources)
    {
        if (proposed.Count != 1 || proposed[0] is not { } adjusted || adjusted.Id != target.Id
            || adjusted.Subject != target.Subject || adjusted.Outcome != target.Outcome)
            throw new ExtractionException("adjustment_out_of_scope", 422);
        // An adjustment changes crops and content; labels and grades are corrected through the question PUT.
        adjusted = adjusted with { Grade = target.Grade, QuestionNumber = target.QuestionNumber,
            IncludedByRequest = target.IncludedByRequest };
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

    /// <summary>
    /// Merges a whole-upload reanalysis into the current questions. Returned questions with a known ID update it, those
    /// without one are new and take <paramref name="newId"/>; omitted questions stay. Protected questions (reviewed, with a
    /// deleted item, or outside the allowed subjects) keep their current form whatever the model returned. A requested
    /// collection applies to correct questions and, once made, stays while the question is correct. Rejects a change that
    /// would withdraw a pending item or leave the allowed subjects, and output that
    /// may duplicate or reach into another question. New questions are placed in reading order by where they start.
    /// </summary>
    internal static List<ExtractionQuestionDraft> Reanalysis(IReadOnlyList<ExtractionQuestionView> current,
        IReadOnlySet<Guid> protectedIds, IReadOnlySet<int> allowedSubjects, List<ExtractionQuestionDraft>? proposed,
        IReadOnlyCollection<ExtractionSource> sources, Func<int, Guid> newId)
    {
        var known = current.ToDictionary(q => q.Question.Id);
        if (proposed is null || proposed.Any(q => q is null || q.Id != Guid.Empty && !known.ContainsKey(q.Id))
            || proposed.Where(q => q.Id != Guid.Empty).GroupBy(q => q.Id).Any(g => g.Count() > 1))
            throw new ExtractionException("invalid_analysis_response", 502);
        var updates = new Dictionary<Guid, ExtractionQuestionDraft>();
        var added = new List<ExtractionQuestionDraft>();
        foreach (var question in proposed)
        {
            if (question.Id == Guid.Empty)
            {
                if (!allowedSubjects.Contains(question.Subject)) throw new ExtractionException("reanalysis_out_of_scope", 422);
                added.Add(Requested(question, kept: false) with { Id = newId(added.Count) });
                continue;
            }
            if (protectedIds.Contains(question.Id)) continue;
            var view = known[question.Id];
            var updated = Requested(question, kept: view.Question.IncludedByRequest) with { Grade = question.Grade ?? view.Question.Grade,
                QuestionNumber = question.QuestionNumber ?? view.Question.QuestionNumber };
            // Withdrawing a pending item is "不是错题" or the question PUT, a separate, explicit operation.
            if (!allowedSubjects.Contains(question.Subject) || Collected(view.Question) && !Collected(updated))
                throw new ExtractionException("reanalysis_out_of_scope", 422);
            if (Payload.HashJson(updated) != Payload.HashJson(view.Question)) updates[question.Id] = updated;
        }
        var merged = current.Select(view => updates.GetValueOrDefault(view.Question.Id, view.Question)).ToList();
        try { ValidateQuestions(merged.Concat(added).ToList(), sources, automatic: false); }
        catch (ExtractionException error) when (error.Code == "duplicate_question")
        { throw new ExtractionException("clarification_required", 422); }
        catch (ExtractionException) { throw new ExtractionException("invalid_analysis_response", 502); }
        // The model lists only what changed, so position comes from the pages: before the first question starting later.
        var page = sources.Select((s, i) => (s.Id, i)).ToDictionary(p => p.Id, p => p.i);
        (int, double, double) Start(ExtractionQuestionDraft q) => (page[q.Regions[0].SourceId], q.Regions[0].Y0, q.Regions[0].X0);
        foreach (var question in added)
        {
            var at = merged.FindIndex(q => Start(q).CompareTo(Start(question)) > 0);
            merged.Insert(at < 0 ? merged.Count : at, question);
        }
        var created = added.Select(q => q.Id).ToHashSet();
        foreach (var question in merged.Where(q => updates.ContainsKey(q.Id) || created.Contains(q.Id)))
        {
            var before = known.TryGetValue(question.Id, out var view) ? view.Question.Regions : [];
            foreach (var region in merged.Where(q => q.Id != question.Id).SelectMany(q => q.Regions))
            {
                // A new question covering an existing one is probably that question found again; an updated one must
                // not newly reach into another question. Coverage that already existed does not count.
                var gained = CoveredArea(question.Regions, region) - CoveredArea(before, region);
                if (gained > AdjustmentOverlapLimit * (region.X1 - region.X0) * (region.Y1 - region.Y0))
                    throw new ExtractionException("clarification_required", 422);
            }
        }
        return merged;
    }

    /// <summary>
    /// Only a correct question is collected on request: incorrect and unknown ones are collected anyway, so the request is
    /// dropped. <paramref name="kept"/> carries an earlier request the model did not restate.
    /// </summary>
    private static ExtractionQuestionDraft Requested(ExtractionQuestionDraft question, bool kept) =>
        question with { IncludedByRequest = question.Outcome == "correct" && (question.IncludedByRequest || kept) };

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
        if (string.IsNullOrWhiteSpace(studentId) || studentId.Length > 36)
            throw new ExtractionException("invalid_request");
        ValidateImages(images);
    }

    internal static void ValidateImages(List<ExtractionImage> images)
    {
        if (images is null || images.Count is < 1 or > MaxSources ||
            images.Any(i => i is null || i.Content is null || i.Content.Length is 0 or > MaxImageBytes ||
                i.ContentType is not ("image/jpeg" or "image/png")))
            throw new ExtractionException("invalid_request");
    }

    internal static bool ValidQuestionNumber(string? value) => value is null ||
        value.Length is >= 1 and <= 64 && value == value.Trim() && !value.Any(char.IsControl);

    internal static void ValidateQuestions([NotNull] List<ExtractionQuestionDraft>? questions,
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
                q.Outcome is not ("correct" or "incorrect" or "unknown") || q.IncludedByRequest && q.Outcome != "correct" ||
                q.Regions is null || q.Regions.Count is < 1 or > 32)
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
        var canonical = questions.Select(q => Payload.HashJson(new { q.Subject, q.Statement, q.StudentAnswer,
            Regions = q.Regions.OrderBy(r => r.SourceId).ThenBy(r => r.X0).ThenBy(r => r.Y0).ThenBy(r => r.X1).ThenBy(r => r.Y1) })).ToList();
        if (canonical.Distinct().Count() != canonical.Count) throw new ExtractionException("duplicate_question");
    }
}
