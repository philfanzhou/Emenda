namespace Ruoyu.Study.Mistake.Database;

/// <summary>
/// Failure raised by the database upgrade itself. Messages carry only object names and counts, never
/// connection settings or row values, so they may be surfaced as-is.
/// </summary>
internal sealed class UpgradeStepException(string message) : Exception(message);
