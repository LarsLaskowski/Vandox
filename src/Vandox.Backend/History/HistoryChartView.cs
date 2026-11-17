using System.Globalization;

using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The data of the history chart.
/// </summary>
public sealed class HistoryChartView
{
    #region Constants

    /// <summary>
    /// The number of chart colors; lines beyond them repeat the colors dashed.
    /// </summary>
    private const int ColorCount = 6;

    #endregion // Constants

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
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(range);

        var step = result.Resolution == TimeSeriesResolution.Step;

        return new HistoryChartView
               {
                   Times = result.Times,
                   Lines = [.. result.Lines.Select((line, index) => BuildLine(line, index, metrics, step))],
                   Axes = [.. metrics.Take(2).Select(metric => LogText.Escape($"{metric.Source} {metric.Name}", HistoryView.LabelChars))],
                   Marks = [.. result.Markers.Select(marker => BuildMark(marker, range))],
                   Events = [.. result.Markers.Select(BuildEvent)],
                   Notes = BuildNotes(result, metrics, range),
                   StepNote = step && result.Step is not null ? HistoryView.Fill(HistoryView.StepNoteFormat, HistoryView.StepText(result.Step.Value)) : string.Empty,
                   Moment = moment is not null && moment.Value >= range.Start && moment.Value <= range.End ? moment : null
               };
    }

    /// <summary>
    /// Gives the same view with another moment, sharing its times and lines, so that the chart only moves its mark.
    /// </summary>
    /// <param name="moment">The moment; it is marked only inside the range</param>
    /// <param name="range">The range of the view</param>
    /// <returns>The view</returns>
    internal HistoryChartView WithMoment(DateTimeOffset? moment, HistoryRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        return new HistoryChartView
               {
                   Times = Times,
                   Lines = Lines,
                   Axes = Axes,
                   Marks = Marks,
                   Events = Events,
                   Notes = Notes,
                   StepNote = StepNote,
                   Moment = moment is not null && moment.Value >= range.Start && moment.Value <= range.End ? moment : null
               };
    }

    /// <summary>
    /// Builds the line of a series.
    /// </summary>
    /// <param name="line">The series</param>
    /// <param name="index">The place of the line</param>
    /// <param name="metrics">The chosen metrics</param>
    /// <param name="step">Whether the points are steps</param>
    /// <returns>The line</returns>
    private static HistoryLine BuildLine(SeriesLine line, int index, IReadOnlyList<SeriesKey> metrics, bool step)
    {
        var scale = 0;

        for (var position = 0; position < metrics.Count; position++)
        {
            if (IsMetric(metrics[position], line.Source, line.Name))
            {
                scale = position;

                break;
            }
        }

        var name = line.Labels.Length == 0 ? $"{line.Source} {line.Name}" : $"{line.Source} {line.Name} {line.Labels}";

        return new HistoryLine
               {
                   Label = LogText.Escape(name, HistoryView.LabelChars),
                   Scale = scale,
                   Color = index % ColorCount,
                   Dashed = index >= ColorCount,
                   Values = [.. line.Points.Select(point => point.State == PointState.Value ? point.Average : (double?)null)],
                   Bridged = [.. line.Points.Select(point => point.State == PointState.BetweenSamples)],
                   Minimums = step ? [.. line.Points.Select(point => point.State == PointState.Value ? point.Minimum : (double?)null)] : [],
                   Maximums = step ? [.. line.Points.Select(point => point.State == PointState.Value ? point.Maximum : (double?)null)] : []
               };
    }

    /// <summary>
    /// Tells whether a metric is the one of a source and name.
    /// </summary>
    /// <param name="metric">The metric</param>
    /// <param name="source">The source</param>
    /// <param name="name">The name</param>
    /// <returns><c>true</c> when both are equal</returns>
    private static bool IsMetric(SeriesKey metric, string source, string name)
    {
        return string.Equals(metric.Source, source, StringComparison.Ordinal) && string.Equals(metric.Name, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the mark of a marker.
    /// </summary>
    /// <param name="marker">The marker</param>
    /// <param name="range">The range</param>
    /// <returns>The mark</returns>
    private static HistoryMark BuildMark(SeriesMarker marker, HistoryRange range)
    {
        DateTimeOffset? endsAt = null;

        if (marker.Kind == MarkerKind.BackupRun)
        {
            endsAt = marker.End == BackupRunEnd.Ended && marker.EndsAt is not null ? marker.EndsAt : range.End;
        }

        return new HistoryMark
               {
                   Kind = marker.Kind,
                   At = marker.At,
                   EndsAt = endsAt
               };
    }

    /// <summary>
    /// Builds the event row of a marker.
    /// </summary>
    /// <param name="marker">The marker</param>
    /// <returns>The row</returns>
    private static HistoryEventRow BuildEvent(SeriesMarker marker)
    {
        var (kind, text) = marker.Kind switch
                           {
                               MarkerKind.OomKill => ("OOM kill", marker.Pid is null ? marker.Text : $"{marker.Text} (PID {marker.Pid.Value.ToString(CultureInfo.InvariantCulture)})"),
                               MarkerKind.Boot => ("Boot", marker.BootId),
                               MarkerKind.ServiceRestart => ("Service restart", marker.Text),
                               _ => ("Backup run", BackupText(marker))
                           };

        return new HistoryEventRow
               {
                   At = marker.At,
                   Kind = kind,
                   Text = LogText.Runs(text, HistoryView.TextChars, HistoryView.TextRuns)
               };
    }

    /// <summary>
    /// Builds the text of a backup run.
    /// </summary>
    /// <param name="marker">The marker</param>
    /// <returns>The text with the command and the end</returns>
    private static string BackupText(SeriesMarker marker)
    {
        if (marker.End == BackupRunEnd.Ended && marker.EndsAt is not null)
        {
            return $"{marker.Text} until {FormatTime(marker.EndsAt.Value)} UTC";
        }

        return marker.End == BackupRunEnd.Running ? $"{marker.Text}, running" : $"{marker.Text}, end unknown";
    }

    /// <summary>
    /// Formats a time as <c>yyyy-MM-dd HH:mm:ss</c>, UTC.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    private static string FormatTime(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the notes of a result in the order E3 to E7.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="metrics">The chosen metrics</param>
    /// <param name="range">The range</param>
    /// <returns>The notes</returns>
    private static List<string> BuildNotes(TimeSeriesResult result, IReadOnlyList<SeriesKey> metrics, HistoryRange range)
    {
        var notes = new List<string>();

        foreach (var metric in metrics)
        {
            if (result.CutSeries.Any(cut => IsMetric(cut, metric.Source, metric.Name)) || result.Lines.Any(line => IsMetric(metric, line.Source, line.Name)))
            {
                continue;
            }

            notes.Add(HistoryView.Fill(HistoryView.NoValuesFormat, LogText.Escape(metric.Source, HistoryView.LabelChars), LogText.Escape(metric.Name, HistoryView.LabelChars)));
        }

        if (result.CutSeries.Count > 0)
        {
            var names = string.Join(", ", result.CutSeries.Select(cut => LogText.Escape($"{cut.Source} {cut.Name}", HistoryView.LabelChars)));

            notes.Add(HistoryView.Fill(HistoryView.CutLinesFormat, names));
        }

        if (result.StartsBeforeRollups)
        {
            notes.Add(HistoryView.StartsBeforeRollupsText);
        }

        if (result.CompleteUntil is not null && result.CompleteUntil.Value < range.End)
        {
            notes.Add(HistoryView.Fill(HistoryView.IncompleteValuesFormat, FormatTime(result.CompleteUntil.Value)));
        }

        if (result.MarkersCompleteUntil is not null && result.MarkersCompleteUntil.Value < range.End)
        {
            notes.Add(HistoryView.Fill(HistoryView.CutEventsFormat, FormatTime(result.MarkersCompleteUntil.Value)));
        }

        return notes;
    }

    #endregion // Methods
}