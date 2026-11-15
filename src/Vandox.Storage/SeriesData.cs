namespace Vandox.Storage;

/// <summary>
/// The lines read for a query at the resolution that was chosen.
/// </summary>
internal sealed class SeriesData
{
    #region Properties

    /// <summary>
    /// Gets a value indicating whether the lines are raw values; otherwise they are steps.
    /// </summary>
    internal bool Raw { get; init; }

    /// <summary>
    /// Gets the step of a step result.
    /// </summary>
    internal TimeSpan? Step { get; init; }

    /// <summary>
    /// Gets the times of a step result as UTC ticks.
    /// </summary>
    internal IReadOnlyList<long> Axis { get; init; } = [];

    /// <summary>
    /// Gets the kept lines in series order.
    /// </summary>
    internal IReadOnlyList<(SeriesKey Key, MetricLine Line)> Lines { get; init; } = [];

    /// <summary>
    /// Gets the series that lost lines.
    /// </summary>
    internal IReadOnlyList<SeriesKey> Cut { get; init; } = [];

    /// <summary>
    /// Gets the time up to which the points of a step result are complete.
    /// </summary>
    internal DateTimeOffset? CompleteUntil { get; init; }

    #endregion // Properties
}