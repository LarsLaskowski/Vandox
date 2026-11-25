using Vandox.Backend.History;
using Vandox.Backend.Overview;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Builds the section MariaDB from the metric series and the status read.
/// </summary>
public static class MariaDbChartView
{
    #region Constants

    /// <summary>
    /// The source of the metrics of MariaDB.
    /// </summary>
    internal const string MariaDbSource = "mariadb";

    /// <summary>
    /// The source of the system metrics.
    /// </summary>
    internal const string SystemSource = "system";

    /// <summary>
    /// The metric of the theoretical maximum of MariaDB's memory.
    /// </summary>
    internal const string MaximumName = "mariadb_memory_max_bytes";

    /// <summary>
    /// The metric of the RAM.
    /// </summary>
    internal const string RamName = "mem_total_bytes";

    /// <summary>
    /// The metric of the queries per second.
    /// </summary>
    internal const string QueriesName = "mariadb_questions_per_second";

    /// <summary>
    /// The metric of the used part of the InnoDB buffer pool.
    /// </summary>
    internal const string UsedName = "mariadb_innodb_buffer_pool_used_percent";

    /// <summary>
    /// The metric of the read hit part of the InnoDB buffer pool.
    /// </summary>
    internal const string HitName = "mariadb_innodb_buffer_pool_read_hit_percent";

    private const string UnknownText = "unknown";

    private const string BudgetFormat = "In use {0} of a theoretical maximum of {1}; RAM {2}.";

    private const string ExceededFormat = "The theoretical maximum exceeds the RAM by {0}.";

    private const string SummaryFormat = "Up {0}, down {1}, not answering {2}, unknown {3} of this range.";

    private const double GibFactor = 1d / 1073741824;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the section.
    /// </summary>
    /// <param name="series">The result of the metric query</param>
    /// <param name="status">The result of the status read</param>
    /// <param name="range">The range</param>
    /// <param name="snapshots">The period the status records are kept, <see langword="null"/> for unlimited</param>
    /// <param name="now">The current time</param>
    /// <returns>The section</returns>
    public static MariaDbCharts Build(TimeSeriesResult series, MariaDbStatusResult status, HistoryRange range, TimeSpan? snapshots, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(range);

        if (HasNoData(series, status))
        {
            return new MariaDbCharts
                   {
                       Notes = [ServicesView.NoMariaDbDataText]
                   };
        }

        var step = series.Resolution == TimeSeriesResolution.Step;
        var stepNote = step && series.Step is { } stepSize ? HistoryView.Fill(HistoryView.StepNoteFormat, HistoryView.StepText(stepSize)) : string.Empty;
        var count = series.Times.Count;
        var maximum = Points(series, MariaDbSource, MaximumName, count);
        var ram = Points(series, SystemSource, RamName, count);
        var exceeds = Last(maximum) is { } maximumBytes && Last(ram) is { } ramBytes && maximumBytes > ramBytes ? maximumBytes - ramBytes : (double?)null;

        return new MariaDbCharts
               {
                   Notes = Notes(series, status, range, snapshots, now),
                   Summary = Summary(status, range, now),
                   Budget = HistoryView.Fill(BudgetFormat, status.LatestMemory is { } use ? OverviewText.Size(use) : UnknownText, Size(Last(maximum)), Size(Last(ram))),
                   BudgetExceeded = exceeds is { } excess ? HistoryView.Fill(ExceededFormat, OverviewText.Size(excess)) : string.Empty,
                   Availability = Chart(series, stepNote, "%", [("Up", status.Up, 1)], step),
                   Memory = Chart(series, stepNote, "GiB", [("Theoretical maximum", maximum, GibFactor), ("RAM", ram, GibFactor), ("In use", status.Memory, GibFactor)], step),
                   Connections = Chart(series, stepNote, "Connections", [("Connections", status.Connections, 1)], step),
                   Queries = Chart(series, stepNote, "Queries/s", [("Queries per second", Points(series, MariaDbSource, QueriesName, count), 1)], step),
                   BufferPool = Chart(series, stepNote, "%", [("Used", Points(series, MariaDbSource, UsedName, count), 1), ("Read hit", Points(series, MariaDbSource, HitName, count), 1)], step)
               };
    }

    /// <summary>
    /// Tells whether the range has no status record and no value of a MariaDB metric.
    /// </summary>
    /// <param name="series">The result of the metric query</param>
    /// <param name="status">The result of the status read</param>
    /// <returns><c>true</c> when the section has no data</returns>
    private static bool HasNoData(TimeSeriesResult series, MariaDbStatusResult status)
    {
        return status.Records == 0 && series.Lines.Where(line => string.Equals(line.Source, MariaDbSource, StringComparison.Ordinal)).All(line => line.Points.All(point => point.State != PointState.Value));
    }

    /// <summary>
    /// Gives the points of a metric, or points without a value when the query has no such line.
    /// </summary>
    /// <param name="series">The result of the metric query</param>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="count">The number of times</param>
    /// <returns>The points</returns>
    private static IReadOnlyList<SeriesPoint> Points(TimeSeriesResult series, string source, string name, int count)
    {
        var line = series.Lines.FirstOrDefault(candidate => string.Equals(candidate.Source, source, StringComparison.Ordinal) && string.Equals(candidate.Name, name, StringComparison.Ordinal));

        return line is null
                   ? [
                         .. Enumerable.Repeat(new SeriesPoint
                                              {
                                                  State = PointState.Gap
                                              },
                                              count)
                     ]
                   : line.Points;
    }

    /// <summary>
    /// Gives the last value of the latest point that has a value.
    /// </summary>
    /// <param name="points">The points</param>
    /// <returns>The value, <see langword="null"/> when no point has one</returns>
    private static double? Last(IReadOnlyList<SeriesPoint> points)
    {
        for (var index = points.Count - 1; index >= 0; index--)
        {
            if (points[index].State == PointState.Value)
            {
                return points[index].Last;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a size, or <c>unknown</c>.
    /// </summary>
    /// <param name="bytes">The size, <see langword="null"/> for unknown</param>
    /// <returns>The text</returns>
    private static string Size(double? bytes)
    {
        return bytes is { } size ? OverviewText.Size(size) : UnknownText;
    }

    /// <summary>
    /// Writes the summary line of the availability: the shares of the range up to now.
    /// </summary>
    /// <param name="status">The result of the status read</param>
    /// <param name="range">The range</param>
    /// <param name="now">The current time</param>
    /// <returns>The line</returns>
    private static string Summary(MariaDbStatusResult status, HistoryRange range, DateTimeOffset now)
    {
        var total = (range.End < now ? range.End : now) - range.Start;

        return HistoryView.Fill(SummaryFormat,
                                ServicesView.ShareDown(status.UpTime, total),
                                ServicesView.ShareDown(status.DownTime, total),
                                ServicesView.ShareDown(status.NotAnsweringTime, total),
                                ServicesView.ShareDown(status.UnknownTime, total));
    }

    /// <summary>
    /// Builds the notes in the order of the edge cases: the retention of the status, the gaps not read, then the notes of the query.
    /// </summary>
    /// <param name="series">The result of the metric query</param>
    /// <param name="status">The result of the status read</param>
    /// <param name="range">The range</param>
    /// <param name="snapshots">The period the status records are kept</param>
    /// <param name="now">The current time</param>
    /// <returns>The notes</returns>
    private static List<string> Notes(TimeSeriesResult series, MariaDbStatusResult status, HistoryRange range, TimeSpan? snapshots, DateTimeOffset now)
    {
        var notes = new List<string>();

        if (snapshots is { } kept && now - kept > range.Start && status.Oldest is { } oldest)
        {
            notes.Add(HistoryView.Fill(ServicesView.RetentionFormat, HistoryView.StepText(kept), ServicesView.FormatTime(oldest)));
        }

        if (status.GapsCompleteUntil is { } gaps)
        {
            notes.Add(HistoryView.Fill(ServicesView.GapsNotReadFormat, ServicesView.FormatTime(gaps)));
        }

        if (series.StartsBeforeRollups)
        {
            notes.Add(HistoryView.StartsBeforeRollupsText);
        }

        if (series.CompleteUntil is { } complete && complete < range.End)
        {
            notes.Add(HistoryView.Fill(HistoryView.IncompleteValuesFormat, ServicesView.FormatTime(complete)));
        }

        return notes;
    }

    /// <summary>
    /// Builds a chart of lines on one scale and one axis.
    /// </summary>
    /// <param name="series">The result of the metric query, which gives the times</param>
    /// <param name="stepNote">The note on the step</param>
    /// <param name="axis">The label of the axis</param>
    /// <param name="lines">The label, the points and the factor of each line</param>
    /// <param name="step">Whether the points are steps</param>
    /// <returns>The chart</returns>
    private static HistoryChartView Chart(TimeSeriesResult series, string stepNote, string axis, IReadOnlyList<(string Label, IReadOnlyList<SeriesPoint> Points, double Factor)> lines, bool step)
    {
        return new HistoryChartView
               {
                   Times = series.Times,
                   Lines = [.. lines.Select((line, index) => HistoryChartView.Line(line.Label, 0, index, line.Points, step, line.Factor))],
                   Axes = [axis],
                   StepNote = stepNote
               };
    }

    #endregion // Methods
}