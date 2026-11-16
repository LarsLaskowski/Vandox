using System.Globalization;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

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
        return preset switch
               {
                   HistoryPreset.OneHour => "1h",
                   HistoryPreset.OneDay => "24h",
                   HistoryPreset.SevenDays => "7d",
                   HistoryPreset.ThirtyDays => "30d",
                   HistoryPreset.OneYear => "1y",
                   _ => throw new ArgumentOutOfRangeException(nameof(preset))
               };
    }

    /// <summary>
    /// Gives the label of a preset.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The label, such as 24 h</returns>
    public static string PresetLabel(HistoryPreset preset)
    {
        return preset switch
               {
                   HistoryPreset.OneHour => "1 h",
                   HistoryPreset.OneDay => "24 h",
                   HistoryPreset.SevenDays => "7 d",
                   HistoryPreset.ThirtyDays => "30 d",
                   HistoryPreset.OneYear => "1 y",
                   _ => throw new ArgumentOutOfRangeException(nameof(preset))
               };
    }

    /// <summary>
    /// Gives the span of a preset.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The span</returns>
    public static TimeSpan PresetSpan(HistoryPreset preset)
    {
        return preset switch
               {
                   HistoryPreset.OneHour => TimeSpan.FromHours(1),
                   HistoryPreset.OneDay => TimeSpan.FromDays(1),
                   HistoryPreset.SevenDays => TimeSpan.FromDays(7),
                   HistoryPreset.ThirtyDays => TimeSpan.FromDays(30),
                   HistoryPreset.OneYear => TimeSpan.FromDays(365),
                   _ => throw new ArgumentOutOfRangeException(nameof(preset))
               };
    }

    /// <summary>
    /// Names a step.
    /// </summary>
    /// <param name="step">The step</param>
    /// <returns>The text, such as 5 minutes or 1 hour</returns>
    public static string StepText(TimeSpan step)
    {
        if (step >= TimeSpan.FromDays(1) && step.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return Count((long)step.TotalDays, "day");
        }

        if (step >= TimeSpan.FromHours(1) && step.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return Count((long)step.TotalHours, "hour");
        }

        return Count((long)step.TotalMinutes, "minute");
    }

    /// <summary>
    /// Gives the target number of points of a chart width.
    /// </summary>
    /// <param name="chartWidth">The chart width in CSS pixels</param>
    /// <returns>1 for a width that is not finite or below 1, else the floor of the width, at most 1,000</returns>
    public static int Target(double chartWidth)
    {
        if (double.IsFinite(chartWidth) == false || chartWidth < 1)
        {
            return 1;
        }

        return (int)Math.Floor(Math.Min(chartWidth, MaxTarget));
    }

    /// <summary>
    /// Converts a selection of the chart to a range widened outward to whole seconds.
    /// </summary>
    /// <param name="startSeconds">The start in Unix seconds</param>
    /// <param name="endSeconds">The end in Unix seconds</param>
    /// <returns>The range, or <see langword="null"/> when a value is refused</returns>
    public static HistoryRange? Selection(double startSeconds, double endSeconds)
    {
        if (IsStorableSeconds(startSeconds) == false || IsStorableSeconds(endSeconds) == false)
        {
            return null;
        }

        var start = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(startSeconds));
        var end = DateTimeOffset.FromUnixTimeSeconds((long)Math.Ceiling(endSeconds));

        if (StorableTime.Contains(start) == false || StorableTime.Contains(end) == false || start >= end)
        {
            return null;
        }

        return new HistoryRange(start, end);
    }

    /// <summary>
    /// Snaps a click to the nearest marker within the tolerance, the earlier on a tie, else the pointer time, floored to the second.
    /// </summary>
    /// <param name="markers">The markers</param>
    /// <param name="pick">The click</param>
    /// <returns>The moment</returns>
    public static DateTimeOffset Pick(IReadOnlyList<SeriesMarker> markers, HistoryPick pick)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(pick);

        var chosen = pick.At;
        var near = markers.Select(marker => marker.At).Where(at => (at - pick.At).Duration() <= pick.Tolerance).Order().ToList();

        if (near.Count > 0)
        {
            chosen = near.MinBy(at => (at - pick.At).Duration());
        }

        var floored = new DateTimeOffset(chosen.UtcTicks - (chosen.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var firstSecond = new DateTimeOffset(StorableTime.Min.UtcTicks + (TimeSpan.TicksPerSecond - (StorableTime.Min.UtcTicks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);

        return floored < firstSecond ? firstSecond : floored;
    }

    /// <summary>
    /// Rounds a time up to the whole second, within the storable range.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The rounded time</returns>
    public static DateTimeOffset RoundUpToSecond(DateTimeOffset time)
    {
        var lastSecond = new DateTimeOffset(StorableTime.Max.UtcTicks - (StorableTime.Max.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var remainder = time.UtcTicks % TimeSpan.TicksPerSecond;
        var rounded = remainder == 0 ? time.UtcTicks : time.UtcTicks - remainder + TimeSpan.TicksPerSecond;

        return rounded >= lastSecond.UtcTicks ? lastSecond : new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    /// <summary>
    /// Gives the metrics of the list the address accepts.
    /// </summary>
    /// <param name="list">The stored metrics</param>
    /// <returns>The options in the order of the list</returns>
    public static IReadOnlyList<SeriesKey> MetricOptions(StoredMetricList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return [.. list.Metrics.Where(HistoryAddress.IsValidMetric)];
    }

    /// <summary>
    /// Gives the link of the history view for an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The link</returns>
    public static string HistoryHref(HistoryAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return "history" + address.ToQuery();
    }

    /// <summary>
    /// Fills a format text with its arguments.
    /// </summary>
    /// <param name="format">The format text</param>
    /// <param name="arguments">The arguments</param>
    /// <returns>The text</returns>
    internal static string Fill(string format, params string[] arguments)
    {
        return string.Format(CultureInfo.InvariantCulture, format, [.. arguments]);
    }

    /// <summary>
    /// Names a count of a unit.
    /// </summary>
    /// <param name="count">The count</param>
    /// <param name="unit">The singular unit</param>
    /// <returns>The text, with the plural for any count but one</returns>
    private static string Count(long count, string unit)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{count} {unit}{(count == 1 ? string.Empty : "s")}");
    }

    /// <summary>
    /// Tells whether a number of Unix seconds is finite and inside the storable range, checked before any conversion.
    /// </summary>
    /// <param name="seconds">The seconds</param>
    /// <returns><c>true</c> when the value can be converted</returns>
    private static bool IsStorableSeconds(double seconds)
    {
        return double.IsFinite(seconds)
               && seconds >= (StorableTime.Min.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond
               && seconds <= (StorableTime.Max.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
    }

    #endregion // Methods
}