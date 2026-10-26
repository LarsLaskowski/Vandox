namespace Vandox.Storage;

/// <summary>
/// The states of a gap.
/// </summary>
public static class GapState
{
    #region Constants

    /// <summary>
    /// The gap may still be filled.
    /// </summary>
    public const string Open = "open";

    /// <summary>
    /// The gap is completely filled.
    /// </summary>
    public const string Closed = "closed";

    /// <summary>
    /// The gap stays: nothing more is expected.
    /// </summary>
    public const string Final = "final";

    #endregion // Constants
}