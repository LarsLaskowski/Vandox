namespace Vandox.Storage;

/// <summary>
/// The side of a moment a snapshot is read from.
/// </summary>
public enum SnapshotSide
{
    #region Values

    /// <summary>
    /// The latest snapshot captured at or before the moment.
    /// </summary>
    AtOrBefore,

    /// <summary>
    /// The earliest snapshot captured after the moment.
    /// </summary>
    After

    #endregion // Values
}