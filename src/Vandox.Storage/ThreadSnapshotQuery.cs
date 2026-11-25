namespace Vandox.Storage;

/// <summary>
/// A query for the stamps of the thread snapshots of a range.
/// </summary>
public sealed class ThreadSnapshotQuery
{
    #region Constants

    /// <summary>
    /// The largest limit.
    /// </summary>
    public const int MaxLimit = 51;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the most stamps to read.
    /// </summary>
    public int Limit { get; init; }

    #endregion // Properties
}