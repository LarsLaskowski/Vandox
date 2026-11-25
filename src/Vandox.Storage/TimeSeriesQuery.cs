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

    /// <summary>
    /// Gets a value indicating whether raw resolution is skipped and the lines are read from the rollup steps.
    /// </summary>
    public bool StepsOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether no markers are read.
    /// </summary>
    public bool SkipMarkers { get; init; }

    #endregion // Properties
}