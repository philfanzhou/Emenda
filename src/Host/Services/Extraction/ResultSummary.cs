using System.Text.Json.Serialization;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Extraction;

internal sealed record ResultSummary(Guid Id, string Status, int Version, string? ErrorCode,
    IReadOnlyList<ResultGroup> Groups)
{
    // Match the persisted Portal/Student subject IDs, not a model-provided ordering.
    private static readonly string[] Subjects = ["语文", "数学", "英语", "物理", "化学", "生物", "历史", "地理", "政治"];

    public static ResultSummary From(ExtractionJobView job)
    {
        var groups = job.Questions.GroupBy(q => (q.Question.Subject, q.Question.Grade))
            .OrderBy(g => g.Key.Subject).ThenBy(g => g.Key.Grade ?? int.MaxValue)
            .Select(group =>
            {
                // Collected correct questions are listed for review but still count as correct.
                var mistakes = group.Where(q => Rules.Collected(q.Question)).Select(q =>
                    new ResultMistake(q.Question.Id, q.ItemId, q.ItemDeleted, q.Question.QuestionNumber,
                        q.Question.Statement, q.Question.StudentAnswer, q.Question.ReferenceAnswer,
                        q.Question.Explanation, q.Question.Evidence,
                        q.Images.Select((_, index) =>
                            $"/api/intake/extractions/{job.Id:D}/questions/{q.Question.Id:D}/images/{job.Version}/{index}").ToArray(),
                        q.Question.IncludedByRequest))
                    .ToArray();
                return new ResultGroup(new ResultCounts(group.Key.Subject,
                    Subjects[group.Key.Subject - 1], group.Key.Grade, group.Count(),
                    group.Count(q => q.Question.Outcome == "incorrect")), mistakes);
            }).ToArray();
        return new(job.Id, job.Status, job.Version, job.ErrorCode, groups);
    }
}

internal sealed record ResultGroup(ResultCounts Summary, IReadOnlyList<ResultMistake> Mistakes);
internal sealed record ResultCounts(int Subject, string SubjectName, int? Grade, int Total, int Incorrect);
internal sealed record ResultMistake(Guid QuestionId, Guid? ItemId, bool ItemDeleted, string? QuestionNumber,
    string Statement, string? StudentAnswer, string ReferenceAnswer, string Explanation, string Evidence, IReadOnlyList<string> Images,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IncludedByRequest = false);
