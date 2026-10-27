namespace Vandox.Storage;

/// <summary>
/// The kinds of a gap.
/// </summary>
public static class GapKind
{
    #region Constants

    /// <summary>
    /// Sequence numbers are missing.
    /// </summary>
    public const string Sequence = "sequence";

    /// <summary>
    /// Metric samples are more than the time gap span apart.
    /// </summary>
    public const string Time = "time";

    #endregion // Constants
}