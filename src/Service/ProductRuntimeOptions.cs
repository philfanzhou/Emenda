namespace Ruoyu.Study.Mistake.Service;

/// <summary>
/// Fixed-at-startup product runtime shape (<c>Emenda:Runtime:Mode=Standalone</c>).
/// A standalone product deployment has no Student service, so the mistake
/// endpoints that would otherwise delegate to a Student-backed return flow must
/// be able to complete locally instead of failing closed. Legacy Local/Platform
/// deployments keep <see cref="Standalone"/> = false and their existing Student
/// return/callback behaviour unchanged.
/// </summary>
public sealed class ProductRuntimeOptions
{
    public bool Standalone { get; }

    public ProductRuntimeOptions(bool standalone)
    {
        Standalone = standalone;
    }
}