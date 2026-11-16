using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003

/// <summary>
/// The data of the history chart.
/// </summary>
public sealed class HistoryChartView
{
    #region Properties

    /// <summary>
    /// Gets the axis times.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times { get; init; } = [];

    /// <summary>
    /// Gets the lines.
    /// </summary>
    public IReadOnlyList<HistoryLine> Lines { get; init; } = [];

    /// <summary>
    /// Gets the labels of the drawn axes.
    /// </summary>
    public IReadOnlyList<string> Axes { get; init; } = [];

    /// <summary>
    /// Gets the markers.
    /// </summary>
    public IReadOnlyList<HistoryMark> Marks { get; init; } = [];

    /// <summary>
    /// Gets the event rows.
    /// </summary>
    public IReadOnlyList<HistoryEventRow> Events { get; init; } = [];

    /// <summary>
    /// Gets the notes in the order of the spec.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Gets the note on the step; empty at raw resolution.
    /// </summary>
    public string StepNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the moment inside the range.
    /// </summary>
    public DateTimeOffset? Moment { get; init; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the view of a time-series result.
    /// </summary>
    /// <param name="result">The result of the query</param>
    /// <param name="metrics">The metrics of the address, in order</param>
    /// <param name="range">The range drawn</param>
    /// <param name="moment">The moment, if any</param>
    /// <returns>The view</returns>
    public static HistoryChartView From(TimeSeriesResult result, IReadOnlyList<SeriesKey> metrics, HistoryRange range, DateTimeOffset? moment)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003