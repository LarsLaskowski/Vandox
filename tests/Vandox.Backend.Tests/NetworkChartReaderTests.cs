using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.History;
using Vandox.Backend.Network;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkChartReader"/>
/// </summary>
[TestClass]
public class NetworkChartReaderTests
{
    #region Constants

    private const string NoDataNote = "No connection data is stored in this range.";
    private const string CutListNote = "The list shows the first 256 sources.";
    private const string StepNote = "Each point is the average of 10 minutes; the legend shows the minimum and maximum.";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _now = HistorySamples.Base;

    private static readonly RetentionPolicy _policy = new()
                                                      {
                                                          FiveMinuteRollups = TimeSpan.FromDays(7),
                                                          HourlyRollups = TimeSpan.FromDays(400)
                                                      };

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The source list is read once, then the rollups of the chosen source over the range of the address in one read that carries the token; the states chart and the programs chart are stacked views of that read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task NetworkChartReaderReadsRollupsOfChosenSource()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Rollups = NetworkSamples.Rollups(range.Start,
                                                 [NetworkSamples.Line("ESTABLISHED", 10, 12), NetworkSamples.Line("UDP", 1, 1)],
                                                 [NetworkSamples.Line("nginx", 6, 7)]);

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        var queries = rollups.Queries;

        Assert.AreSequenceEqual(["Sources", "Rollups"], rollups.Sequence.ToArray(), "one source list, then one rollup read");
        Assert.HasCount(1, queries, "one rollup read");
        Assert.AreEqual("agent", queries[0].Source, "the chosen source");
        Assert.AreEqual(range.Start, queries[0].From, "the start of the range");
        Assert.AreEqual(range.End, queries[0].To, "the end of the range");
        Assert.IsTrue(rollups.Calls.All(call => call.Token == TestContext.CancellationToken), "every read carries the token");
        Assert.AreEqual("agent", charts.Source, "the source of the charts");
        Assert.AreSequenceEqual(["agent"], charts.Sources.ToArray(), "the sources of the choice");
        Assert.IsNotNull(charts.States, "the states chart");
        Assert.IsNotNull(charts.Programs, "the programs chart");
        Assert.IsTrue(charts.States.Stacked && charts.Programs.Stacked, "both charts are stacked");
        Assert.AreSequenceEqual(["ESTABLISHED", "UDP"], charts.States.Lines.Select(line => line.Label).ToArray(), "a band per state");
        Assert.AreSequenceEqual(["nginx", "Other programs"], charts.Programs.Lines.Select(line => line.Label).ToArray(), "the programs and the other programs");
        Assert.AreEqual(queries[0].Step, charts.Step, "the step of the read");
        Assert.AreSequenceEqual([StepNote], charts.Notes.ToArray(), "the step note");
    }

    /// <summary>
    /// The step is the smallest of the ladder with at most the chart's width in steps, whole hours or more once the range starts before the five-minute rollups, and the same step reaches the read and the step note.
    /// </summary>
    /// <param name="days">The days of the range up to now</param>
    /// <param name="width">The chart width in pixels</param>
    /// <param name="minutes">The expected step in minutes</param>
    /// <param name="stepText">The step in the note</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(1, 200.0, 10, "10 minutes")]
    [DataRow(1, 100.5, 15, "15 minutes")]
    [DataRow(1, 1000.0, 5, "5 minutes")]
    [DataRow(30, 1000.0, 60, "1 hour")]
    [DataRow(365, 1000.0, 720, "12 hours")]
    public async Task NetworkChartReaderChoosesLadderStep(int days, double width, int minutes, string stepText)
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-days), _now);

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Rollups = NetworkSamples.Rollups(range.Start, [NetworkSamples.Line("UDP", 1, 1)], []);

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, width, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([TimeSpan.FromMinutes(minutes)], rollups.Queries.Select(query => query.Step).ToArray(), "the read uses the step of the width");
        Assert.AreEqual(TimeSpan.FromMinutes(minutes), charts.Step, "the step of the charts");
        Assert.Contains($"Each point is the average of {stepText}; the legend shows the minimum and maximum.", charts.Notes.ToArray(), "the step note says the step");
    }

    /// <summary>
    /// A width that is not a number, below one pixel or beyond the maximum is read as the history view reads it, and the read still happens.
    /// </summary>
    /// <param name="width">The width</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(0.0)]
    [DataRow(-5.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(1000000.0)]
    public async Task NetworkChartReaderReadsWithAnyWidth(double width)
    {
        // Arrange
        var range = Day();
        var expected = SeriesLadder.Choose(range.Start, range.End, _now, HistoryView.Target(width), _policy);

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Rollups = NetworkSamples.Rollups(range.Start, [NetworkSamples.Line("UDP", 1, 1)], []);

        // Act
        await ChartReader(rollups).ReadAsync(null, range, width, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, rollups.Queries, "the read happens");
        Assert.AreEqual(expected, rollups.Queries[0].Step, "the step of the target the history view makes of the width");
    }

    /// <summary>
    /// Without a source in the address the source with the latest data in the range is shown, the first listed on a tie, and the choice lists the sources in the order of the store.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task NetworkChartReaderDefaultsToTheSourceWithTheLatestData()
    {
        // Arrange
        var range = Day();

        using var latest = Reader(ProcessesSamples.SourceList(false, ("agent", _now.AddHours(-3)), ("import", _now.AddHours(-1)), ("zeta", _now.AddHours(-2))));
        using var tie = Reader(ProcessesSamples.SourceList(false, ("beta", _now.AddHours(-5)), ("agent", _now.AddHours(-1)), ("import", _now.AddHours(-1))));

        // Act
        var charts = await ChartReader(latest).ReadAsync(null, range, 200, TestContext.CancellationToken);
        var tied = await ChartReader(tie).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("import", charts.Source, "the source with the latest data");
        Assert.AreSequenceEqual(["agent", "import", "zeta"], charts.Sources.ToArray(), "the order of the store");
        Assert.IsTrue(latest.Queries.All(query => query.Source == "import"), "the read is of that source");
        Assert.AreEqual("agent", tied.Source, "the first listed of the latest data");
    }

    /// <summary>
    /// A source of the address is read as it is, even when the list does not name it, and the list is still read for the choice.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task NetworkChartReaderKeepsTheAddressSource()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now), ("import", _now.AddHours(-1))));

        rollups.Rollups = NetworkSamples.Rollups(range.Start, [NetworkSamples.Line("UDP", 1, 1)], []);

        // Act
        var listed = await ChartReader(rollups).ReadAsync("import", range, 200, TestContext.CancellationToken);
        var unlisted = await ChartReader(rollups).ReadAsync("gone-source", range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("import", listed.Source, "a listed source is kept although another has later data");
        Assert.AreEqual("gone-source", unlisted.Source, "a source the list does not name is kept");
        Assert.AreSequenceEqual(["agent", "import"], unlisted.Sources.ToArray(), "the list is the store's");
        Assert.AreSequenceEqual(["import", "gone-source"], rollups.Queries.Select(query => query.Source).ToArray(), "the reads are of the sources of the address");
    }

    /// <summary>
    /// A cut source list gives its note, before the step note.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task NetworkChartReaderNotesACutSourceList()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(true, ("agent", _now)));

        rollups.Rollups = NetworkSamples.Rollups(range.Start, [NetworkSamples.Line("UDP", 1, 1)], []);

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([CutListNote, StepNote], charts.Notes.ToArray(), "the note of the cut list, then the step note");
    }

    /// <summary>
    /// A source without rollups in the range leaves both charts out and says so alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task NetworkChartReaderLeavesTheChartsOutWithoutData()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        // Act
        var charts = await ChartReader(rollups).ReadAsync("agent", range, 200, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(charts.States, "no states chart");
        Assert.IsNull(charts.Programs, "no programs chart");
        Assert.AreEqual("agent", charts.Source, "the source stays the address's");
        Assert.AreSequenceEqual([NoDataNote], charts.Notes.ToArray(), "the note of E1 alone");
    }

    /// <summary>
    /// With no source in the range and none in the address nothing is read but the list, the source is empty and the note says there is no data.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task NetworkChartReaderReadsNoRollupsWithoutASource()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false));

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(string.Empty, charts.Source, "no source");
        Assert.IsEmpty(charts.Sources, "no listed source");
        Assert.IsNull(charts.States, "no states chart");
        Assert.IsNull(charts.Programs, "no programs chart");
        Assert.AreSequenceEqual(["Sources"], rollups.Sequence.ToArray(), "only the list was read");
        Assert.AreSequenceEqual([NoDataNote], charts.Notes.ToArray(), "the note of E1");
    }

    /// <summary>
    /// The notes follow the rollup read: a range that starts before the data, incomplete steps and steps not folded yet are all noted once, before the step note.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task NetworkChartReaderNotesWhatTheReadReports()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Rollups = new ConnectionRollupResult
                          {
                              Times = ProcessesSamples.Times(range.Start, TimeSpan.FromMinutes(10), 2),
                              States = [NetworkSamples.Line("UDP", 1, 1)],
                              Others = [0, 0],
                              Incomplete = [0, 3],
                              StartsBeforeBuckets = true,
                              EarliestUnfolded = range.End.AddMinutes(-5)
                          };

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([
                                    "This range starts before the oldest stored connection data of this source.",
                                    "Some snapshots in this range may not cover every socket.",
                                    "Values after 2026-10-01 09:55:00 UTC are not complete yet and may still change.",
                                    StepNote
                                ],
                                charts.Notes.ToArray(),
                                "the notes of the read in order");
    }

    /// <summary>
    /// A read that fails is not caught: the failure reaches the caller, which shows the text of E12.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task NetworkChartReaderLetsAFailureThrough()
    {
        // Arrange
        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Failure = new InvalidOperationException("store failed");

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ChartReader(rollups).ReadAsync(null, Day(), 200, TestContext.CancellationToken), "the failure");

        // Assert
        Assert.AreEqual("store failed", thrown.Message, "the failure of the store");
    }

    /// <summary>
    /// No range is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task NetworkChartReaderRefusesNoRange()
    {
        // Arrange
        using var rollups = Reader(ProcessesSamples.SourceList(false));

        // Act and assert
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => ChartReader(rollups).ReadAsync(null, null!, 200, TestContext.CancellationToken), "no range");
    }

    /// <summary>
    /// Creates the range of the last day up to the fake now.
    /// </summary>
    /// <returns>The range</returns>
    private static HistoryRange Day()
    {
        return new HistoryRange(_now.AddDays(-1), _now);
    }

    /// <summary>
    /// Creates a rollup reader answering the given source list.
    /// </summary>
    /// <param name="sources">The sources</param>
    /// <returns>The reader</returns>
    private static FakeConnectionRollupReader Reader(ProgramSourceList sources)
    {
        return new FakeConnectionRollupReader
               {
                   Sources = sources
               };
    }

    /// <summary>
    /// Creates the chart reader over a rollup reader with the policy and the clock of the tests.
    /// </summary>
    /// <param name="rollups">The rollup reader</param>
    /// <returns>The chart reader</returns>
    private static NetworkChartReader ChartReader(FakeConnectionRollupReader rollups)
    {
        return new NetworkChartReader(rollups, _policy, new FakeTimeProvider(_now));
    }

    #endregion // Methods
}