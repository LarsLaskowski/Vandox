namespace Vandox.Storage;

/// <summary>
/// A time-series query.
/// </summary>
public sealed class TimeSeriesQuery
{
    #region Properties

    /// <summary>
    /// Gets the series to read.
    /// </summary>
    public IReadOnlyList<SeriesKey> Series { get; init; } = [];

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the target number of axis times.
    /// </summary>
    public int Target { get; init; }

    #endregion // Properties
}