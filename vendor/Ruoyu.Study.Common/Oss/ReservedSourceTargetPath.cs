namespace Ruoyu.Study.Common.Oss;

/// <summary>The protected source-target namespace and its legacy prefix-mapped alias.</summary>
public static class ReservedSourceTargetPath
{
    public const string Prefix = "mistakes/source-targets/";
    public static bool IsReserved(string? path) => path != null &&
        (path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("uploads/source-targets/", StringComparison.OrdinalIgnoreCase));
    public static void EnsureLegacy(string? path)
    {
        if (IsReserved(path)) throw new InvalidOperationException("Reserved source target path cannot be used by legacy writers.");
    }
}
