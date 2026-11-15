namespace Vandox.Storage;

/// <summary>
/// A range read of time-series context rows.
/// </summary>
public sealed class TimeRangeQuery
{
    #region Properties

    /// <summary>
    /// Gets the first capture time of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the maximum number of rows.
    /// </summary>
    public int Limit { get; init; }

    /// <summary>
    /// Gets the ID after which rows at the capture time <see cref="From"/> are returned; rows at that time with an ID at most this are skipped.
    /// </summary>
    public long AfterId { get; init; }

    /// <summary>
    /// Gets the ID before which rows at the capture time <see cref="To"/> are returned by a newest-first read; 0 keeps <see cref="To"/> exclusive.
    /// </summary>
    public long BeforeId { get; init; }

    #endregion // Properties
}