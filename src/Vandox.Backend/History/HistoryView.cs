using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003

/// <summary>
/// The texts, caps and helpers of the history view.
/// </summary>
public static class HistoryView
{
    #region Constants

    /// <summary>
    /// The text without a chosen metric.
    /// </summary>
    public const string ChooseMetricsText = "Choose one or more metrics to draw.";

    /// <summary>
    /// The text while the page is prerendered.
    /// </summary>
    public const string LoadingText = "Loading…";

    /// <summary>
    /// The text after a cut metric list.
    /// </summary>
    public const string MetricListCutText = "The list shows the first 256 metrics; others can be named in the address.";

    /// <summary>
    /// The text for a ninth metric.
    /// </summary>
    public const string TooManyMetricsText = "At most 8 metrics can be drawn at once.";

    /// <summary>
    /// The format of the note at a step; the argument is the step.
    /// </summary>
    public const string StepNoteFormat = "Each point is the average of {0}; the legend shows the minimum and maximum.";

    /// <summary>
    /// The format of the note on a metric without values; the arguments are source and name.
    /// </summary>
    public const string NoValuesFormat = "{0} {1} has no values in this range.";

    /// <summary>
    /// The format of the note on cut lines; the argument is the metrics.
    /// </summary>
    public const string CutLinesFormat = "Only 64 lines can be drawn; lines of {0} are left out.";

    /// <summary>
    /// The text for a range that starts before the kept rollups.
    /// </summary>
    public const string StartsBeforeRollupsText = "This range starts before the oldest kept rollups; older values are no longer stored.";

    /// <summary>
    /// The text for a range that begins after now.
    /// </summary>
    public const string FutureRangeText = "This range lies in the future.";

    /// <summary>
    /// The format of the note on incomplete values; the argument is the time.
    /// </summary>
    public const string IncompleteValuesFormat = "Values after {0} UTC are not complete yet and may still change.";

    /// <summary>
    /// The format of the note on a cut event list; the argument is the time.
    /// </summary>
    public const string CutEventsFormat = "Events after {0} UTC are not shown.";

    /// <summary>
    /// The text without a process snapshot.
    /// </summary>
    public const string NoProcessSnapshotText = "No process snapshot was stored within 10 minutes of this moment.";

    /// <summary>
    /// The text without a connection snapshot.
    /// </summary>
    public const string NoConnectionSnapshotText = "No connection snapshot was stored within 10 minutes of this moment.";

    /// <summary>
    /// The note on a process snapshot that is not complete and has no total.
    /// </summary>
    public const string IncompleteProcessesText = "This snapshot does not list every process.";

    /// <summary>
    /// The note on connection counts that are incomplete.
    /// </summary>
    public const string IncompleteConnectionsText = "These counts may not cover every socket.";

    /// <summary>
    /// The heading of the remotes of a counts-only snapshot.
    /// </summary>
    public const string RemotesCountsOnlyHeading = "Top remote addresses (the snapshot keeps only the 25 most frequent)";

    /// <summary>
    /// The text of a chart read past the deadline.
    /// </summary>
    public const string ChartTimeoutText = "Reading the history took longer than 10 seconds and was stopped. Narrow the range or draw fewer metrics.";

    /// <summary>
    /// The text of a failed chart read.
    /// </summary>
    public const string ChartFailureText = "The history could not be read. The service log has the details.";

    /// <summary>
    /// The text of a snapshot read past the deadline.
    /// </summary>
    public const string MomentTimeoutText = "Reading the snapshots of this moment took longer than 10 seconds and was stopped.";

    /// <summary>
    /// The text of a failed snapshot read.
    /// </summary>
    public const string MomentFailureText = "The snapshots of this moment could not be read. The service log has the details.";

    /// <summary>
    /// The most metrics of one address.
    /// </summary>
    public const int MaxMetrics = 8;

    /// <summary>
    /// The largest target number of points.
    /// </summary>
    public const int MaxTarget = 1000;

    /// <summary>
    /// The rows of a list in the panel.
    /// </summary>
    public const int ListRows = 20;

    /// <summary>
    /// The characters of a line label.
    /// </summary>
    public const int LabelChars = 64;

    /// <summary>
    /// The characters of an external text in the event list and the panel.
    /// </summary>
    public const int TextChars = 256;

    /// <summary>
    /// The runs of an external text in the event list and the panel.
    /// </summary>
    public const int TextRuns = 16;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The window beside a moment in which a snapshot counts.
    /// </summary>
    public static readonly TimeSpan MomentWindow = TimeSpan.FromMinutes(10);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Gives the value of a preset in the address.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The value, such as 24h</returns>
    public static string PresetValue(HistoryPreset preset)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the label of a preset.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The label, such as 24 h</returns>
    public static string PresetLabel(HistoryPreset preset)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the span of a preset.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The span</returns>
    public static TimeSpan PresetSpan(HistoryPreset preset)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Names a step.
    /// </summary>
    /// <param name="step">The step</param>
    /// <returns>The text, such as 5 minutes or 1 hour</returns>
    public static string StepText(TimeSpan step)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the target number of points of a chart width.
    /// </summary>
    /// <param name="chartWidth">The chart width in CSS pixels</param>
    /// <returns>1 for a width that is not finite or below 1, else the floor of the width, at most 1,000</returns>
    public static int Target(double chartWidth)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Converts a selection of the chart to a range widened outward to whole seconds.
    /// </summary>
    /// <param name="startSeconds">The start in Unix seconds</param>
    /// <param name="endSeconds">The end in Unix seconds</param>
    /// <returns>The range, or <see langword="null"/> when a value is refused</returns>
    public static HistoryRange? Selection(double startSeconds, double endSeconds)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Snaps a click to the nearest marker within the tolerance, the earlier on a tie, else the pointer time, floored to the second.
    /// </summary>
    /// <param name="markers">The markers</param>
    /// <param name="pick">The click</param>
    /// <returns>The moment</returns>
    public static DateTimeOffset Pick(IReadOnlyList<SeriesMarker> markers, HistoryPick pick)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Rounds a time up to the whole second, within the storable range.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The rounded time</returns>
    public static DateTimeOffset RoundUpToSecond(DateTimeOffset time)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the metrics of the list the address accepts.
    /// </summary>
    /// <param name="list">The stored metrics</param>
    /// <returns>The options in the order of the list</returns>
    public static IReadOnlyList<SeriesKey> MetricOptions(StoredMetricList list)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the link of the history view for an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The link</returns>
    public static string HistoryHref(HistoryAddress address)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003