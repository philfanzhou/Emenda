namespace Ruoyu.Study.Mistake.Host.Services.Layout;

/// <summary>The typed layout-job failure (IKQA2O), mirroring the intake convention: a
/// stable error kind plus the HTTP-style status the future administrative surface maps
/// onto. Messages never contain image bytes, the credential or raw provider text.</summary>
public sealed class LayoutJobException(string errorKind, int statusCode = 409) : Exception(errorKind)
{
    public string ErrorKind { get; } = errorKind;
    public int StatusCode { get; } = statusCode;
}
