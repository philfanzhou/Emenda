namespace Emenda.Host.Services.Conversation;

public sealed class ConversationCollectionOptions
{
    public bool Enabled { get; set; }
    public string CallerName { get; set; } = "platform";
    public string AuthorizationBaseUrl { get; set; } = "";
    public string TutorUrl { get; set; } = "";
    public string CollectionKey { get; set; } = "";
    public void Validate()
    {
        if (Enabled && (!Origin(TutorUrl) || !Origin(AuthorizationBaseUrl) || string.IsNullOrWhiteSpace(CallerName) || CallerName.Any(char.IsControl)
            || CollectionKey.Length is < 32 or > 256 || CollectionKey.Any(char.IsControl)))
            throw new InvalidOperationException("ConversationCollection requires a fixed TutorUrl and an independent CollectionKey.");
    }
    private static bool Origin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0
        && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
    public bool Authorizes(string? supplied) => CollectionKey.Length is >= 32 and <= 256 && supplied?.Length == CollectionKey.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(CollectionKey), System.Text.Encoding.UTF8.GetBytes(supplied));
}
