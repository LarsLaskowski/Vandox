namespace Vandox.Storage;

/// <summary>
/// The result of a time-series query.
/// </summary>
public sealed class TimeSeriesResult
{
    #region Properties

    /// <summary>
    /// Gets the resolution.
    /// </summary>
    public TimeSeriesResolution Resolution { get; init; }

    /// <summary>
    /// Gets the step of the axis, for the step resolution.
    /// </summary>
    public TimeSpan? Step { get; init; }

    /// <summary>
    /// Gets the axis times.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times { get; init; } = [];

    /// <summary>
    /// Gets the lines.
    /// </summary>
    public IReadOnlyList<SeriesLine> Lines { get; init; } = [];

    /// <summary>
    /// Gets the series cut at the line limit.
    /// </summary>
    public IReadOnlyList<SeriesKey> CutSeries { get; init; } = [];

    /// <summary>
    /// Gets the flag that the range starts before the rollups.
    /// </summary>
    public bool StartsBeforeRollups { get; init; }

    /// <summary>
    /// Gets the time until which the lines are complete.
    /// </summary>
    public DateTimeOffset? CompleteUntil { get; init; }

    /// <summary>
    /// Gets the time until which the gaps are complete.
    /// </summary>
    public DateTimeOffset? GapsCompleteUntil { get; init; }

    /// <summary>
    /// Gets the time until which the markers are complete.
    /// </summary>
    public DateTimeOffset? MarkersCompleteUntil { get; init; }

    /// <summary>
    /// Gets the gaps.
    /// </summary>
    public IReadOnlyList<SeriesGap> Gaps { get; init; } = [];

    /// <summary>
    /// Gets the markers.
    /// </summary>
    public IReadOnlyList<SeriesMarker> Markers { get; init; } = [];

    #endregion // Properties
}