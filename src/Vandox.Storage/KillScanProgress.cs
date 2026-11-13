namespace Vandox.Storage;

/// <summary>
/// How far the kill scan has read.
/// </summary>
public sealed class KillScanProgress
{
    #region Properties

    /// <summary>
    /// Gets the highest record ID scanned.
    /// </summary>
    public long ScannedId { get; init; }

    /// <summary>
    /// Gets the highest record ID that existed when the pass began.
    /// </summary>
    public long HighestId { get; init; }

    #endregion // Properties
}