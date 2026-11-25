using System.Globalization;

using Vandox.Backend.History;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="MariaDbChartView"/>
/// </summary>
[TestClass]
public class MariaDbChartViewTests
{
    #region Constants

    private const string MaxName = "mariadb_memory_max_bytes";
    private const string RamName = "mem_total_bytes";
    private const string QueriesName = "mariadb_questions_per_second";
    private const string UsedName = "mariadb_innodb_buffer_pool_used_percent";
    private const string HitName = "mariadb_innodb_buffer_pool_read_hit_percent";
    private const double Gib = ServicesSamples.Gib;

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _now = ServicesSamples.Now;

    private static readonly TimeSpan _step = TimeSpan.FromHours(1);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The summary line gives the shares of the range for up, down, not answering and unknown, each rounded down to one decimal: 10 minutes down of 24 hours read 99.3 % up and 0.6 % down.
    /// </summary>
    /// <param name="upMinutes">The minutes up</param>
    /// <param name="downMinutes">The minutes down</param>
    /// <param name="notAnsweringMinutes">The minutes not answering</param>
    /// <param name="unknownMinutes">The minutes unknown</param>
    /// <param name="expected">The expected line</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(1430, 10, 0, 0, "Up 99.3 %, down 0.6 %, not answering 0.0 %, unknown 0.0 % of this range.")]
    [DataRow(1440, 0, 0, 0, "Up 100.0 %, down 0.0 %, not answering 0.0 %, unknown 0.0 % of this range.")]
    [DataRow(0, 0, 0, 1440, "Up 0.0 %, down 0.0 %, not answering 0.0 %, unknown 100.0 % of this range.")]
    [DataRow(720, 360, 180, 180, "Up 50.0 %, down 25.0 %, not answering 12.5 %, unknown 12.5 % of this range.")]
    [DataRow(1439, 1, 0, 0, "Up 99.9 %, down 0.0 %, not answering 0.0 %, unknown 0.0 % of this range.")]
    public void MariaDbChartViewWritesSummaryRoundedDown(int upMinutes, int downMinutes, int notAnsweringMinutes, int unknownMinutes, string expected)
    {
        // Arrange
        var range = Range(24);
        var status = Status(24, upTime: TimeSpan.FromMinutes(upMinutes), downTime: TimeSpan.FromMinutes(downMinutes), notAnsweringTime: TimeSpan.FromMinutes(notAnsweringMinutes), unknownTime: TimeSpan.FromMinutes(unknownMinutes));

        // Act
        var charts = MariaDbChartView.Build(Series(24), status, range, null, _now);

        // Assert
        Assert.AreEqual(expected, charts.Summary, "the summary line");
    }

    /// <summary>
    /// The shares are those of the range up to now: a range that ends in the future counts only the time until now.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbChartViewSummaryCountsTheRangeUpToNow()
    {
        // Arrange
        var range = new HistoryRange(_now.AddHours(-24), _now.AddHours(24));
        var status = Status(24, upTime: TimeSpan.FromHours(24));

        // Act
        var charts = MariaDbChartView.Build(Series(24), status, range, null, _now);

        // Assert
        Assert.AreEqual("Up 100.0 %, down 0.0 %, not answering 0.0 %, unknown 0.0 % of this range.", charts.Summary, "the time after now is not part of the range");
    }

    /// <summary>
    /// The availability chart draws one line, <c>Up</c>, in percent of each step, and a step without a value is a gap in the line, never drawn across.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC7")]
    public void MariaDbChartViewDrawsAvailabilityAndBreaksLinesAtGaps()
    {
        // Arrange
        var status = Status(4, up: [ServicesSamples.Value(100), ServicesSamples.Gap(), ServicesSamples.Value(50), ServicesSamples.Between()], connections: [ServicesSamples.Value(7), ServicesSamples.Gap(), ServicesSamples.Gap(), ServicesSamples.Value(9)]);

        // Act
        var charts = MariaDbChartView.Build(Series(4), status, Range(4), null, _now);

        // Assert
        Assert.IsNotNull(charts.Availability, "the availability chart");
        Assert.IsNotNull(charts.Connections, "the connections chart");
        Assert.AreSequenceEqual(["Up"], charts.Availability.Lines.Select(line => line.Label).ToArray(), "one line");
        Assert.AreSequenceEqual(["%"], charts.Availability.Axes.ToArray(), "the axis in percent");
        AssertValues([100, null, 50, null], charts.Availability.Lines[0].Values, "a gap and a step between samples are no values");
        Assert.AreSequenceEqual(["Connections"], charts.Connections.Axes.ToArray(), "the axis of the connections");
        AssertValues([7, null, null, 9], charts.Connections.Lines.Single().Values, "the connections with their gaps");
    }

    /// <summary>
    /// The memory budget reads the latest in-use value of the range and the last value of the latest value point of the maximum and of the RAM, the latest value point being the last one that has a value.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void MariaDbChartViewWritesBudgetLine()
    {
        // Arrange
        var series = Series(4,
                            ServicesSamples.Line("mariadb", MaxName, ServicesSamples.Value(6 * Gib), ServicesSamples.Gap(), ServicesSamples.Value(5.5 * Gib, 6 * Gib), ServicesSamples.Gap()),
                            ServicesSamples.Line("system", RamName, ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib), ServicesSamples.Value(3 * Gib, 4 * Gib), ServicesSamples.Value(3.5 * Gib, 4 * Gib)));

        // Act
        var charts = MariaDbChartView.Build(series, Status(4), Range(4), null, _now);

        // Assert
        Assert.AreEqual("In use 1.2 GiB of a theoretical maximum of 6.0 GiB; RAM 4.0 GiB.", charts.Budget, "the figures of the budget");
    }

    /// <summary>
    /// The sentence that the theoretical maximum exceeds the RAM appears exactly when the maximum is above the RAM, with the difference as a size.
    /// </summary>
    /// <param name="maximumGib">The maximum in GiB</param>
    /// <param name="ramGib">The RAM in GiB</param>
    /// <param name="expected">The expected sentence, empty for none</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow(6.0, 4.0, "The theoretical maximum exceeds the RAM by 2.0 GiB.")]
    [DataRow(4.5, 4.0, "The theoretical maximum exceeds the RAM by 512.0 MiB.")]
    [DataRow(4.0, 4.0, "")]
    [DataRow(3.0, 4.0, "")]
    public void MariaDbChartViewSaysMaximumExceedsRam(double maximumGib, double ramGib, string expected)
    {
        // Arrange
        var series = Series(2,
                            ServicesSamples.Line("mariadb", MaxName, ServicesSamples.Value(maximumGib * Gib), ServicesSamples.Value(maximumGib * Gib)),
                            ServicesSamples.Line("system", RamName, ServicesSamples.Value(ramGib * Gib), ServicesSamples.Value(ramGib * Gib)));

        // Act
        var charts = MariaDbChartView.Build(series, Status(2), Range(2), null, _now);

        // Assert
        Assert.AreEqual(expected, charts.BudgetExceeded, "the sentence of the comparison");
    }

    /// <summary>
    /// A figure of the budget the range lacks reads <c>unknown</c>, never 0, and without the maximum or the RAM the comparison is left out.
    /// </summary>
    /// <param name="missing">The missing figure: <c>maximum</c>, <c>ram</c>, <c>use</c> or <c>values</c> for a line without a value</param>
    /// <param name="expected">The expected line</param>
    /// <param name="exceeds">Whether the comparison is written</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow("maximum", "In use 1.2 GiB of a theoretical maximum of unknown; RAM 4.0 GiB.", false)]
    [DataRow("ram", "In use 1.2 GiB of a theoretical maximum of 6.0 GiB; RAM unknown.", false)]
    [DataRow("use", "In use unknown of a theoretical maximum of 6.0 GiB; RAM 4.0 GiB.", true)]
    [DataRow("values", "In use 1.2 GiB of a theoretical maximum of unknown; RAM unknown.", false)]
    public void MariaDbChartViewLeavesComparisonOutWhenValueUnknown(string missing, string expected, bool exceeds)
    {
        // Arrange
        var maximum = ServicesSamples.Line("mariadb", MaxName, ServicesSamples.Value(6 * Gib), ServicesSamples.Value(6 * Gib));
        var ram = ServicesSamples.Line("system", RamName, ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib));
        var lines = missing switch
                    {
                        "maximum" => new[] { ram },
                        "ram" => [maximum],
                        "values" => [ServicesSamples.Line("mariadb", MaxName, ServicesSamples.Gap(), ServicesSamples.Gap()), ServicesSamples.Line("system", RamName, ServicesSamples.Gap(), ServicesSamples.Gap())],
                        _ => [maximum, ram]
                    };
        var status = Status(2, withMemory: missing != "use");

        // Act
        var charts = MariaDbChartView.Build(Series(2, lines), status, Range(2), null, _now);

        // Assert
        Assert.AreEqual(expected, charts.Budget, "the unknown figure reads unknown");
        Assert.AreEqual(exceeds, charts.BudgetExceeded.Length > 0, "the comparison is written only for a known maximum and RAM");
    }

    /// <summary>
    /// The memory chart draws the lines <c>Theoretical maximum</c>, <c>RAM</c> and <c>In use</c> on one scale in GiB: every value is divided by 2^30, a gap stays a gap.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void MariaDbChartViewDrawsBudgetLinesInGiB()
    {
        // Arrange
        var series = Series(4,
                            ServicesSamples.Line("mariadb", MaxName, ServicesSamples.Value(6 * Gib), ServicesSamples.Gap(), ServicesSamples.Value(5.5 * Gib, 6 * Gib), ServicesSamples.Gap()),
                            ServicesSamples.Line("system", RamName, ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib)));
        var status = Status(4, memory: [ServicesSamples.Value(Gib), ServicesSamples.Value(1.5 * Gib), ServicesSamples.Gap(), ServicesSamples.Value(2 * Gib)]);

        // Act
        var charts = MariaDbChartView.Build(series, status, Range(4), null, _now);

        // Assert
        Assert.IsNotNull(charts.Memory, "the memory chart");
        Assert.AreSequenceEqual(["Theoretical maximum", "RAM", "In use"], charts.Memory.Lines.Select(line => line.Label).ToArray(), "the three lines");
        Assert.AreSequenceEqual(["GiB"], charts.Memory.Axes.ToArray(), "one axis in GiB");
        Assert.HasCount(1, charts.Memory.Lines.Select(line => line.Scale).Distinct().ToArray(), "one scale for the three lines");
        AssertValues([6, null, 5.5, null], charts.Memory.Lines[0].Values, "the maximum in GiB");
        AssertValues([4, 4, 4, 4], charts.Memory.Lines[1].Values, "the RAM in GiB");
        AssertValues([1, 1.5, null, 2], charts.Memory.Lines[2].Values, "the memory in use in GiB");
    }

    /// <summary>
    /// The five charts share the times of the query, and the lines of the metrics and of the status are drawn on the axes the page names: queries per second, the buffer pool in percent
    /// with the lines <c>Used</c> and <c>Read hit</c> on one scale.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void MariaDbChartViewChartsShareTimes()
    {
        // Arrange
        var series = Series(3,
                            ServicesSamples.Line("mariadb", QueriesName, ServicesSamples.Value(10), ServicesSamples.Gap(), ServicesSamples.Value(30)),
                            ServicesSamples.Line("mariadb", UsedName, ServicesSamples.Value(80), ServicesSamples.Value(81), ServicesSamples.Value(82)),
                            ServicesSamples.Line("mariadb", HitName, ServicesSamples.Value(99), ServicesSamples.Value(98), ServicesSamples.Gap()));

        // Act
        var charts = MariaDbChartView.Build(series, Status(3), Range(3), null, _now);

        // Assert
        Assert.IsNotNull(charts.Availability, "the availability chart");
        Assert.IsNotNull(charts.Memory, "the memory chart");
        Assert.IsNotNull(charts.Connections, "the connections chart");
        Assert.IsNotNull(charts.Queries, "the queries chart");
        Assert.IsNotNull(charts.BufferPool, "the buffer pool chart");

        var all = new[] { charts.Availability, charts.Memory, charts.Connections, charts.Queries, charts.BufferPool };

        Assert.IsTrue(all.All(chart => chart.Times.SequenceEqual(series.Times)), "every chart has the times of the query");
        Assert.AreSequenceEqual(["Queries/s"], charts.Queries.Axes.ToArray(), "the axis of the queries");
        AssertValues([10, null, 30], charts.Queries.Lines.Single().Values, "the queries per second");
        Assert.AreSequenceEqual(["Used", "Read hit"], charts.BufferPool.Lines.Select(line => line.Label).ToArray(), "the lines of the buffer pool");
        Assert.AreSequenceEqual(["%"], charts.BufferPool.Axes.ToArray(), "the axis in percent");
        Assert.HasCount(1, charts.BufferPool.Lines.Select(line => line.Scale).Distinct().ToArray(), "one scale for both lines");
        AssertValues([80, 81, 82], charts.BufferPool.Lines[0].Values, "the used part");
        AssertValues([99, 98, null], charts.BufferPool.Lines[1].Values, "the read hit with its gap");
    }

    /// <summary>
    /// Every chart carries the step text of the history view, and the notes of the query appear as in the history view: the rollups the range starts before and the values that are not complete.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void MariaDbChartViewWritesStepNoteAndQueryNotes()
    {
        // Arrange
        var complete = new DateTimeOffset(2026, 10, 1, 8, 30, 15, TimeSpan.Zero);
        var series = Series(4, ServicesSamples.Line("mariadb", QueriesName, ServicesSamples.Value(10), ServicesSamples.Value(10), ServicesSamples.Value(10), ServicesSamples.Value(10)));
        var noted = Noted(4, true, complete, ServicesSamples.Line("mariadb", QueriesName, ServicesSamples.Value(10), ServicesSamples.Value(10), ServicesSamples.Value(10), ServicesSamples.Value(10)));

        // Act
        var charts = MariaDbChartView.Build(noted, Status(4), Range(4), null, _now);
        var plain = MariaDbChartView.Build(series, Status(4), Range(4), null, _now);

        // Assert
        var stepNote = HistoryView.Fill(HistoryView.StepNoteFormat, "1 hour");

        Assert.AreEqual("Each point is the average of 1 hour; the legend shows the minimum and maximum.", stepNote, "the text of the step note");
        Assert.IsTrue(new[] { charts.Availability, charts.Memory, charts.Connections, charts.Queries }.All(chart => chart!.StepNote == stepNote), "every chart has the step text");
        Assert.Contains(HistoryView.StartsBeforeRollupsText, charts.Notes.ToArray(), "the note of the rollups");
        Assert.Contains("Values after 2026-10-01 08:30:15 UTC are not complete yet and may still change.", charts.Notes.ToArray(), "the note of the values that are not complete");
        Assert.DoesNotContain(HistoryView.StartsBeforeRollupsText, plain.Notes.ToArray(), "no note without the cause");
        Assert.DoesNotContain(note => note.StartsWith("Values after", StringComparison.Ordinal), plain.Notes.ToArray(), "no note about values that are complete");
    }

    /// <summary>
    /// A line of a metric with a gap and a line of the status with a gap are drawn as gaps, and a gap of the status that the range's end leaves open stays a gap.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void MariaDbChartViewGapsBreakLines()
    {
        // Arrange
        var series = Series(3,
                            ServicesSamples.Line("mariadb", UsedName, ServicesSamples.Value(80), ServicesSamples.Gap(), ServicesSamples.Value(82)),
                            ServicesSamples.Line("mariadb", HitName, ServicesSamples.Gap(), ServicesSamples.Gap(), ServicesSamples.Gap()));

        // Act
        var charts = MariaDbChartView.Build(series, Status(3), Range(3), null, _now);

        // Assert
        Assert.IsNotNull(charts.BufferPool, "the buffer pool chart");
        AssertValues([80, null, 82], charts.BufferPool.Lines[0].Values, "the gap of the used part");
        AssertValues([null, null, null], charts.BufferPool.Lines[1].Values, "a line without a value draws nothing");
        Assert.IsTrue(charts.BufferPool.Lines.All(line => line.Bridged.All(bridged => bridged == false)), "a gap is not bridged");
    }

    /// <summary>
    /// A range without a status record and without a value of a MariaDB metric says that no MariaDB data is stored in place of the content; a RAM value alone does not count, and a
    /// status record or a value of a MariaDB metric does.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <param name="noData">Whether the page says that no MariaDB data is stored</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("no line and no record", true)]
    [DataRow("only mem_total_bytes with values", true)]
    [DataRow("mariadb lines without values", true)]
    [DataRow("a mariadb metric with a value", false)]
    [DataRow("status records only", false)]
    public void MariaDbChartViewShowsNoMariaDbData(string scenario, bool noData)
    {
        // Arrange
        var lines = scenario switch
                    {
                        "only mem_total_bytes with values" => new[] { ServicesSamples.Line("system", RamName, ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib)) },
                        "mariadb lines without values" => [ServicesSamples.Line("mariadb", QueriesName, ServicesSamples.Gap(), ServicesSamples.Gap()), ServicesSamples.Line("system", RamName, ServicesSamples.Value(4 * Gib), ServicesSamples.Value(4 * Gib))],
                        "a mariadb metric with a value" => [ServicesSamples.Line("mariadb", QueriesName, ServicesSamples.Gap(), ServicesSamples.Value(3))],
                        _ => []
                    };
        var records = scenario == "status records only" ? 5 : 0;
        var gaps = new[] { ServicesSamples.Gap(), ServicesSamples.Gap() };
        var status = Status(2, up: gaps, memory: gaps, connections: gaps, withMemory: false, withOldest: false, records: records);

        // Act
        var charts = MariaDbChartView.Build(Series(2, lines), status, Range(2), null, _now);

        // Assert
        Assert.AreEqual(noData, charts.Notes.Contains(ServicesView.NoMariaDbDataText), "the text that no MariaDB data is stored");

        if (noData)
        {
            Assert.IsNull(charts.Availability, "no availability chart");
            Assert.IsNull(charts.Memory, "no memory chart");
            Assert.IsNull(charts.Connections, "no connections chart");
            Assert.IsNull(charts.Queries, "no queries chart");
            Assert.IsNull(charts.BufferPool, "no buffer pool chart");
            Assert.AreEqual(string.Empty, charts.Summary, "no summary");
            Assert.AreEqual(string.Empty, charts.Budget, "no budget");
        }
        else
        {
            Assert.IsNotNull(charts.Availability, "the availability chart is drawn");
        }
    }

    /// <summary>
    /// The note about the retention of MariaDB's status appears only when the retention is limited and its cut lies after the start of the range, with the period and the time of
    /// the oldest status record: never for an unlimited retention, a range that starts at the cut or later, or a range without a status record.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <param name="expected">The expected note, <see langword="null"/> for none</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("unlimited", null)]
    [DataRow("fresh install", null)]
    [DataRow("within the retention", null)]
    [DataRow("no oldest record", null)]
    [DataRow("past the cut", "MariaDB's status is kept for 30 days; availability, memory in use and connections begin at 2026-09-01 11:00:00 UTC.")]
    [DataRow("past the cut in hours", "MariaDB's status is kept for 36 hours; availability, memory in use and connections begin at 2026-09-29 23:30:00 UTC.")]
    public void MariaDbChartViewShowsRetentionNoteOnlyPastSnapshots(string scenario, string? expected)
    {
        // Arrange
        var (snapshots, days, oldest) = scenario switch
                                        {
                                            "unlimited" => ((TimeSpan?)null, 365, (DateTimeOffset?)new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero)),
                                            "fresh install" => (TimeSpan.FromDays(30), 30, _now.AddHours(-6)),
                                            "within the retention" => (TimeSpan.FromDays(30), 7, _now.AddDays(-30)),
                                            "no oldest record" => (TimeSpan.FromDays(30), 365, null),
                                            "past the cut" => (TimeSpan.FromDays(30), 365, new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero)),
                                            _ => (TimeSpan.FromHours(36), 7, new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.Zero))
                                        };

        var range = new HistoryRange(_now.AddDays(-days), _now);
        var status = Status(2, oldest: oldest, withOldest: oldest is not null);

        // Act
        var charts = MariaDbChartView.Build(Series(2), status, range, snapshots, _now);

        // Assert
        var notes = charts.Notes.Where(note => note.StartsWith("MariaDB's status is kept for", StringComparison.Ordinal)).ToArray();

        if (expected is null)
        {
            Assert.IsEmpty(notes, "no note about the retention");
        }
        else
        {
            Assert.AreSequenceEqual([expected], notes, "the note about the retention");
        }
    }

    /// <summary>
    /// A status read that stopped at its gap cap says that gaps after that time are not read.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbChartViewSaysGapsNotRead()
    {
        // Arrange
        var capped = Status(2, gapsCompleteUntil: new DateTimeOffset(2026, 9, 30, 4, 5, 6, TimeSpan.Zero));

        // Act
        var charts = MariaDbChartView.Build(Series(2), capped, Range(2), null, _now);
        var complete = MariaDbChartView.Build(Series(2), Status(2), Range(2), null, _now);

        // Assert
        Assert.Contains("Gaps after 2026-09-30 04:05:06 UTC are not read; the time after it counts as unknown.", charts.Notes.ToArray(), "the note of the gaps not read");
        Assert.DoesNotContain(note => note.StartsWith("Gaps after", StringComparison.Ordinal), complete.Notes.ToArray(), "no note for a complete read");
    }

    /// <summary>
    /// The notes keep the order of the edge cases: the retention of the status, the gaps not read, then the notes of the query.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbChartViewKeepsTheOrderOfTheNotes()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-365), _now);
        var status = Status(2, oldest: _now.AddDays(-30), gapsCompleteUntil: _now.AddDays(-20));
        var series = Noted(2, true, null, ServicesSamples.Line("mariadb", QueriesName, ServicesSamples.Value(1), ServicesSamples.Value(1)));

        // Act
        var notes = MariaDbChartView.Build(series, status, range, TimeSpan.FromDays(30), _now).Notes.ToArray();

        // Assert
        var retention = Array.FindIndex(notes, note => note.StartsWith("MariaDB's status is kept for", StringComparison.Ordinal));
        var gaps = Array.FindIndex(notes, note => note.StartsWith("Gaps after", StringComparison.Ordinal));
        var rollups = Array.IndexOf(notes, HistoryView.StartsBeforeRollupsText);

        Assert.IsGreaterThanOrEqualTo(0, retention, "the retention note");
        Assert.IsGreaterThan(retention, gaps, "the gaps after the retention");
        Assert.IsGreaterThan(gaps, rollups, "the notes of the query after the gaps");
    }

    /// <summary>
    /// Creates the range of a number of hours that ends now.
    /// </summary>
    /// <param name="hours">The hours</param>
    /// <returns>The range</returns>
    private static HistoryRange Range(int hours)
    {
        return new HistoryRange(_now.AddHours(-hours), _now);
    }

    /// <summary>
    /// Creates the result of a time-series query of hourly steps up to now.
    /// </summary>
    /// <param name="count">The number of steps</param>
    /// <param name="lines">The lines</param>
    /// <returns>The result</returns>
    private static TimeSeriesResult Series(int count, params SeriesLine[] lines)
    {
        return ServicesSamples.Series([.. Enumerable.Range(0, count).Select(index => _now.AddHours(index - count))], _step, lines);
    }

    /// <summary>
    /// Creates the result of a time-series query of hourly steps up to now that carries the notes of the query.
    /// </summary>
    /// <param name="count">The number of steps</param>
    /// <param name="startsBeforeRollups">Whether the range starts before the oldest kept rollups</param>
    /// <param name="completeUntil">The time after which the values are not complete, or <see langword="null"/></param>
    /// <param name="lines">The lines</param>
    /// <returns>The result</returns>
    private static TimeSeriesResult Noted(int count, bool startsBeforeRollups, DateTimeOffset? completeUntil, params SeriesLine[] lines)
    {
        var plain = Series(count, lines);

        return new TimeSeriesResult
               {
                   Resolution = plain.Resolution,
                   Step = plain.Step,
                   Times = plain.Times,
                   Lines = plain.Lines,
                   StartsBeforeRollups = startsBeforeRollups,
                   CompleteUntil = completeUntil
               };
    }

    /// <summary>
    /// Creates the result of a status read with the same times as <see cref="Series"/>: by default MariaDB up in every step, 1 GiB in use in every step, five connections, 1.2 GiB
    /// in use at last, the oldest record at the start of the range and ten records.
    /// </summary>
    /// <param name="count">The number of steps</param>
    /// <param name="up">The points of the availability, or <see langword="null"/> for the default</param>
    /// <param name="memory">The points of the memory in use, or <see langword="null"/> for the default</param>
    /// <param name="connections">The points of the connections, or <see langword="null"/> for the default</param>
    /// <param name="upTime">The time up, or <see langword="null"/> for the whole range</param>
    /// <param name="downTime">The time down</param>
    /// <param name="notAnsweringTime">The time not answering</param>
    /// <param name="unknownTime">The unknown time</param>
    /// <param name="withMemory">Whether the latest memory in use is known</param>
    /// <param name="oldest">The time of the oldest record, or <see langword="null"/> for the start of the range</param>
    /// <param name="withOldest">Whether there is an oldest record</param>
    /// <param name="gapsCompleteUntil">The time after which gaps are not read, or <see langword="null"/></param>
    /// <param name="records">The number of records</param>
    /// <returns>The result</returns>
    private static MariaDbStatusResult Status(int count,
                                              IReadOnlyList<SeriesPoint>? up = null,
                                              IReadOnlyList<SeriesPoint>? memory = null,
                                              IReadOnlyList<SeriesPoint>? connections = null,
                                              TimeSpan? upTime = null,
                                              TimeSpan downTime = default,
                                              TimeSpan notAnsweringTime = default,
                                              TimeSpan unknownTime = default,
                                              bool withMemory = true,
                                              DateTimeOffset? oldest = null,
                                              bool withOldest = true,
                                              DateTimeOffset? gapsCompleteUntil = null,
                                              int records = 10)
    {
        return new MariaDbStatusResult
               {
                   Times = [.. Enumerable.Range(0, count).Select(index => _now.AddHours(index - count))],
                   Up = up ?? ServicesSamples.Points(count, 100),
                   Memory = memory ?? ServicesSamples.Points(count, Gib),
                   Connections = connections ?? ServicesSamples.Points(count, 5),
                   UpTime = upTime ?? TimeSpan.FromHours(count),
                   DownTime = downTime,
                   NotAnsweringTime = notAnsweringTime,
                   UnknownTime = unknownTime,
                   LatestMemory = withMemory ? 1.2 * Gib : null,
                   LatestMemoryAt = withMemory ? _now.AddHours(-1) : null,
                   Oldest = withOldest ? oldest ?? _now.AddHours(-count) : null,
                   GapsCompleteUntil = gapsCompleteUntil,
                   Records = records
               };
    }

    /// <summary>
    /// Checks the values of a line.
    /// </summary>
    /// <param name="expected">The expected values</param>
    /// <param name="actual">The values of the line</param>
    /// <param name="what">What is checked</param>
    private static void AssertValues(double?[] expected, IReadOnlyList<double?> actual, string what)
    {
        Assert.HasCount(expected.Length, actual, $"{what}: the number of values");

        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index] is null, actual[index] is null, $"{what}: value {index.ToString(CultureInfo.InvariantCulture)} is a gap");

            if (expected[index] is { } value)
            {
                Assert.AreEqual(value, actual[index]!.Value, 1e-9, $"{what}: value {index.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }

    #endregion // Methods
}