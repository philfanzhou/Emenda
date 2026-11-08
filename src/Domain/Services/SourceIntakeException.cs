namespace Ruoyu.Study.Mistake.Domain.Services;

public sealed class SourceIntakeException(string errorKind, int statusCode = 409, Exception? inner = null) : Exception(errorKind, inner)
{
    public IReadOnlyList<Guid>? ConflictingItemIds { get; init; }
    public string ErrorKind { get; } = errorKind;
    public int StatusCode { get; } = statusCode;
}
