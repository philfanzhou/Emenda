namespace Emenda.Host.Services.Layout;

/// <summary>
/// One layout-detection input: the exact bytes of a single source image and, when the
/// retained source metadata carries one, the declared media type. The bytes are transmitted
/// unchanged — the client never auto-orients, resizes or compresses them — and the declared
/// media type must match the actual byte signature. Callers must supply verified original
/// image bytes (byte verification against the retained snapshot is the persistence stage's
/// responsibility).
/// </summary>
public sealed record LayoutDetectionImage(byte[] Bytes, string? DeclaredMediaType);

/// <summary>
/// The small application interface the StructaDoc layout client sits behind (IKQA2N). One
/// call maps to exactly one synchronous HTTP exchange with the dedicated non-archiving
/// <c>POST /api/v1/layout/detect</c> endpoint; there is no submission, polling or retrieval
/// follow-up. Cancellation is propagated; every classified failure surfaces as a
/// <see cref="LayoutDetectionException"/> whose <see cref="LayoutDetectionException.Failure"/>
/// category distinguishes transient transport/overload from authorization, unsupported
/// capability/schema, invalid input and malformed output — operational outcomes, not signals
/// to retry everything or to ask a human to crop the photo.
/// </summary>
public interface ILayoutDetectionClient
{
    /// <exception cref="LayoutDetectionException">Any classified exchange failure, including
    /// the capability-disabled and local input-validation categories, which are reported
    /// without any network exchange.</exception>
    Task<Domain.Models.LayoutDetectionResult> DetectLayoutAsync(
        LayoutDetectionImage image, CancellationToken cancellationToken = default);
}
