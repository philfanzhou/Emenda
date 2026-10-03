namespace Ruoyu.Study.Mistake.Service.Intake;

public sealed record SourceHandoffTarget(string SourcePath, string TargetKey, long Length, string Sha256);
