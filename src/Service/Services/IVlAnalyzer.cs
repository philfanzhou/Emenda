namespace Emenda.Service.Services;

// Defined in Service layer so gRPC implementations (e.g. MistakeServiceImpl) can inject this
// without creating a circular reference (Host -> Service -> Host).
// The concrete implementation lives in Host/Services/VlImageAnalysis/VlUploadAnalyzer.cs.
public interface IVlAnalyzer
{
    Task<VlAnalysisResult> AnalyzeAsync(string studentId, List<string> imagePaths, CancellationToken ct = default);

    Task<VlAnalysisResult> AnalyzeVerifiedAsync(string studentId, List<string> imagePaths,
        IReadOnlyList<byte[]> imageBytes, CancellationToken ct = default) =>
        throw new InvalidOperationException("Verified image analysis is unsupported by this adapter.");

    // Re-analyze a previously-analyzed mistake using the original root cause and an optional teacher note.
    // Returns only the refined root cause (no subject/grade — these are not changed during re-analysis).
    Task<VlReanalysisResult> ReAnalyzeAsync(
        string studentId,
        List<string> imagePaths,
        string existingRootCause,
        string? teacherDescription,
        CancellationToken ct = default);
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
    public string RootCause { get; set; } = string.Empty;
}

public class VlReanalysisResult
{
    public string RootCause { get; set; } = string.Empty;
    public string? RawResponse { get; set; }
    public bool Skipped { get; set; }
    public bool Success => !string.IsNullOrEmpty(RootCause) || Skipped;
}
