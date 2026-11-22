namespace Emenda.Host.Services.Extraction;

public sealed class ExtractionOptions
{
    public bool CleanupLegacyObjects { get; set; }
    public const string SectionName = "Emenda:Extraction";
    public bool Enabled { get; set; }
    public string TutorBaseUrl { get; set; } = "";
    public string TutorApiKey { get; set; } = "";
    public int OperationTimeoutSeconds { get; set; } = 300;

    public bool IsConfigured => Enabled && OperationTimeoutSeconds is >= 1 and <= 1800
        && TutorApiKey.Length is >= 32 and <= 256 && !TutorApiKey.Any(char.IsControl)
        && Uri.TryCreate(TutorBaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";
}
