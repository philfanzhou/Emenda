using Emenda.Service.Intake;

namespace Emenda.Host.Services.Extraction;

internal sealed record ExtractionResultSummary(Guid Id, string Status, int Version, string? ErrorCode,
    IReadOnlyList<ExtractionResultGroup> Groups)
{
    private static readonly string[] Subjects = ["语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史", "地理"];

    public static ExtractionResultSummary From(ExtractionJobView job)
    {
        var groups = job.Questions.GroupBy(q => (q.Question.Subject, q.Question.Grade))
            .OrderBy(g => g.Key.Subject).ThenBy(g => g.Key.Grade ?? int.MaxValue)
            .Select(group =>
            {
                var mistakes = group.Where(q => q.Question.Outcome == "incorrect").Select(q =>
                    new ExtractionResultMistake(q.Question.Id, q.ItemId, q.ItemDeleted, q.Question.QuestionNumber,
                        q.Question.Statement, q.Question.StudentAnswer, q.Question.ReferenceAnswer,
                        q.Question.Explanation, q.Question.Evidence,
                        q.Images.Select((_, index) =>
                            $"/api/intake/extractions/{job.Id:D}/questions/{q.Question.Id:D}/images/{job.Version}/{index}").ToArray()))
                    .ToArray();
                return new ExtractionResultGroup(new ExtractionResultCounts(group.Key.Subject,
                    Subjects[group.Key.Subject - 1], group.Key.Grade, group.Count(), mistakes.Length), mistakes);
            }).ToArray();
        return new(job.Id, job.Status, job.Version, job.ErrorCode, groups);
    }
}

internal sealed record ExtractionResultGroup(ExtractionResultCounts Summary, IReadOnlyList<ExtractionResultMistake> Mistakes);
internal sealed record ExtractionResultCounts(int Subject, string SubjectName, int? Grade, int Total, int Incorrect);
internal sealed record ExtractionResultMistake(Guid QuestionId, Guid? ItemId, bool ItemDeleted, string? QuestionNumber,
    string Statement, string? StudentAnswer, string ReferenceAnswer, string Explanation, string Evidence, IReadOnlyList<string> Images);
