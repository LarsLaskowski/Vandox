namespace Vandox.Storage;

/// <summary>
/// The range of one query after the query time is applied: the start, the end (never after the query time), the query time and the target.
/// </summary>
internal sealed class SeriesRange
{
    #region Properties

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    internal DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range: the end of the query or the query time, whichever is earlier.
    /// </summary>
    internal DateTimeOffset End { get; init; }

    /// <summary>
    /// Gets the query time.
    /// </summary>
    internal DateTimeOffset Now { get; init; }

    /// <summary>
    /// Gets the target number of points.
    /// </summary>
    internal int Target { get; init; }

    #endregion // Properties
}