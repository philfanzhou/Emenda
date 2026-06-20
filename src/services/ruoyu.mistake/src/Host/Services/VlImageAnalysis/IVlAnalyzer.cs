namespace Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;

public interface IVlAnalyzer
{
    Task<VlAnalysisResult> AnalyzeAsync(string studentId, List<string> imagePaths, CancellationToken ct = default);
}

public class VlAnalysisResult
{
    public List<VlImageGroup> Groups { get; set; } = new();
    public string? RawResponse { get; set; }
    public bool Skipped { get; set; }
    public bool HasGroups => Groups.Count > 0;
}

public class VlImageGroup
{
    public List<int> ImageIndices { get; set; } = new();
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string Description { get; set; } = string.Empty;
}
