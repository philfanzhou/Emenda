using System.Text.Json;

namespace Emenda.Domain.Models;

public sealed class MistakeAnalysisReport
{
    public string Summary { get; set; } = "";
    public List<MistakeAnalysisError> Errors { get; set; } = [];
    public List<MistakeAnalysisCause> Causes { get; set; } = [];
    public List<MistakeAnalysisKnowledge> KnowledgePoints { get; set; } = [];
    public string Limitations { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Summary) || Summary.Length > 4000 ||
            Errors is null || Causes is null || KnowledgePoints is null || Limitations is null ||
            Errors.Count > 50 || Causes.Count > 50 || KnowledgePoints.Count > 50 ||
            Errors.Any(e => e is null || e.Location is null || string.IsNullOrWhiteSpace(e.Evidence) || string.IsNullOrWhiteSpace(e.Explanation)) ||
            Causes.Any(c => c is null || c.Uncertainty is null || string.IsNullOrWhiteSpace(c.Description) || string.IsNullOrWhiteSpace(c.Evidence)) ||
            KnowledgePoints.Any(k => k is null || string.IsNullOrWhiteSpace(k.Name)))
            throw new InvalidOperationException("invalid_analysis_report");
        if (JsonSerializer.Serialize(this).Length > 64000)
            throw new InvalidOperationException("analysis_report_too_large");
    }
}

public sealed class MistakeAnalysisError
{
    public string Location { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Explanation { get; set; } = "";
}

public sealed class MistakeAnalysisCause
{
    public string Description { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Uncertainty { get; set; } = "";
}

public sealed class MistakeAnalysisKnowledge
{
    public string Name { get; set; } = "";
    public string? KnowledgePointId { get; set; }
}

public sealed class AnalysisFeedback
{
    public Guid JobId { get; set; }
    public Guid RequestId { get; set; }
    public string Caller { get; set; } = "";
    public string ReviewerId { get; set; } = "";
    public string? Description { get; set; }
    public long ExpectedRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MistakeAnalysisModel
{
    public Guid MistakeItemId { get; set; }
    public long Revision { get; set; }
    public MistakeAnalysisReport? DraftReport { get; set; }
    public MistakeAnalysisReport? ConfirmedReport { get; set; }
    public Guid? DraftJobId { get; set; }
    public Guid? ConfirmedJobId { get; set; }
    public List<AnalysisFeedback> TeacherFeedback { get; set; } = [];
    public DateTimeOffset? GeneratedAt { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public bool InputChanged { get; set; }
    public bool ReferenceAnswerAvailable => false;
}

public sealed class StructuredAnalysisInput
{
    public long Revision { get; set; }
    public int Subject { get; set; }
    public int Grade { get; set; }
    public List<SourceRegionModel> SourceRegions { get; set; } = [];
    public MistakeAnalysisReport? PreviousReport { get; set; }
    public List<string> TeacherFeedback { get; set; } = [];
}

public sealed class StructuredAnalysisResult
{
    public MistakeAnalysisReport Report { get; set; } = new();
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
}

public sealed class AnalysisConflictException(string code) : Exception(code);
