namespace Emenda.HttpContracts;

/// <summary>Owned learning evidence retained independently of conversation history.</summary>
public sealed record ConversationQuestionSnapshot
{
    public int Version { get; init; } = 1;
    public Guid ConversationId { get; init; }
    public Guid QuestionId { get; init; }
    public int Position { get; init; }
    public DateTimeOffset MaterializedAt { get; init; }
    public string Statement { get; init; } = "";
    public ConversationStudentAttempt? StudentAttempt { get; init; }
    public string Evidence { get; init; } = "";
    public string ReferenceAnswer { get; init; } = "";
    public string[] KnowledgePoints { get; init; } = [];
    public ConversationQuestionSource[] Sources { get; init; } = [];
    public ConversationQuestionImage[] Images { get; init; } = [];
    public ConversationQuestionAttempt[] Attempts { get; init; } = [];
    public ConversationQuestionAnalysis? Analysis { get; init; }
    public string EvidenceHash(int subject, int reviewStatus, string? rootCause, IEnumerable<ConversationEvidenceRegion> regions)
    {
        var evidence = new { ConversationId, QuestionId, Position, Statement, StudentAttempt, Evidence, ReferenceAnswer, KnowledgePoints, Sources, Images, Attempts,
            subject, reviewStatus, rootCause = rootCause ?? "", regions = regions.ToArray() };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(evidence, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
public sealed record ConversationStudentAttempt(int Position, string Quote, Guid? AssetId);
public sealed record ConversationQuestionAttempt(int RecordedAtPosition, ConversationStudentAttempt Attempt, string Outcome, string Evidence, bool RetractsMistake = false);
public sealed record ConversationQuestionSource(int Position, string Content);
public sealed record ConversationQuestionImage(int SourcePosition, Guid? AssetId, string Path, int[]? Box);
public sealed record ConversationQuestionAnalysis(string EvidenceHash, int Version, string Model, string Content);
public sealed record ConversationEvidenceRegion(string Path, int[]? Box);
