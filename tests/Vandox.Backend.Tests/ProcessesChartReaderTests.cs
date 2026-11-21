using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.History;
using Vandox.Backend.Processes;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessesChartReader"/>
/// </summary>
[TestClass]
public class ProcessesChartReaderTests
{
    #region Constants

    private const string NoDataNote = "No process data is stored in this range.";
    private const string CutListNote = "The list shows the first 256 sources.";
    private const string RssNote = "RSS counts shared memory in every program that maps it, so the stack can exceed the server's memory.";

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
    /// The source list is read once, and then the ten programs with the highest memory and the ten with the highest CPU of the range, each over the range of the address, in two reads that carry the token; the charts are stacked views with the notes of the memory read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ProcessesChartReaderReadsTenProgramsByMemoryAndByCpu()
    {
        // Arrange
        var range = Day();
        var memoryPrograms = Enumerable.Range(1, 10).Select(index => $"mem{index}").ToArray();
        var cpuPrograms = Enumerable.Range(1, 10).Select(index => $"cpu{index}").ToArray();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Results[ProgramMeasure.Memory] = ProcessesSamples.Result(range.Start, ProgramMemoryKey.Rss, memoryPrograms);
        rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(range.Start, null, cpuPrograms);

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        var queries = rollups.Queries;

        Assert.AreSequenceEqual(["Sources", "Rollups Memory", "Rollups Cpu"], rollups.Sequence.ToArray(), "one source list, then the memory read, then the CPU read");
        Assert.HasCount(2, queries, "two rollup reads");
        Assert.AreEqual(ProgramMeasure.Memory, queries[0].Measure, "the first read is by memory");
        Assert.AreEqual(ProgramMeasure.Cpu, queries[1].Measure, "the second read is by CPU");
        Assert.IsTrue(queries.All(query => query.Count == ProcessesView.Programs && query.Count == 10), "ten programs per read");
        Assert.IsTrue(queries.All(query => query.Source == "agent" && query.From == range.Start && query.To == range.End), "the source and the range of the address");
        Assert.IsTrue(rollups.Calls.All(call => call.Token == TestContext.CancellationToken), "every read carries the token");
        Assert.AreEqual("agent", charts.Source, "the source of the charts");
        Assert.AreSequenceEqual(["agent"], charts.Sources.ToArray(), "the sources of the choice");
        Assert.IsNotNull(charts.Memory, "the memory chart");
        Assert.IsNotNull(charts.Cpu, "the CPU chart");
        Assert.IsTrue(charts.Memory.Stacked && charts.Cpu.Stacked, "both charts are stacked");
        Assert.HasCount(11, charts.Memory.Lines, "ten programs and the other programs");
        Assert.HasCount(11, charts.Cpu.Lines, "ten programs and the other programs");
        Assert.AreEqual("mem1", charts.Memory.Lines[0].Label, "the first memory program");
        Assert.AreEqual("cpu1", charts.Cpu.Lines[0].Label, "the first CPU program");
        Assert.AreSequenceEqual(["Memory (RSS), MiB"], charts.Memory.Axes.ToArray(), "the memory axis");
        Assert.AreSequenceEqual(["CPU %"], charts.Cpu.Axes.ToArray(), "the CPU axis");
        Assert.AreEqual(RssNote, charts.MemoryNote, "the note of an RSS range");
    }

    /// <summary>
    /// A PSS read has no memory note.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ProcessesChartReaderGivesNoMemoryNoteForAPssRange()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Results[ProgramMeasure.Memory] = ProcessesSamples.Result(range.Start, ProgramMemoryKey.Pss, "nginx");
        rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(range.Start, null, "nginx");

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(string.Empty, charts.MemoryNote, "no note for PSS");
        Assert.AreSequenceEqual(["Memory (PSS), MiB"], charts.Memory?.Axes.ToArray(), "the PSS axis");
    }

    /// <summary>
    /// The step is the smallest of the ladder with at most the chart's width in steps, whole hours or more once the range starts before the five-minute rollups, and the same step reaches both reads and the step note.
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
    [DataRow(1, 5000.0, 5, "5 minutes")]
    [DataRow(30, 1000.0, 60, "1 hour")]
    [DataRow(365, 1000.0, 720, "12 hours")]
    public async Task ProcessesChartReaderChoosesTheStepFromTheWidth(int days, double width, int minutes, string stepText)
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-days), _now);

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Results[ProgramMeasure.Memory] = ProcessesSamples.Result(range.Start, ProgramMemoryKey.Pss, "nginx");
        rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(range.Start, null, "nginx");

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, width, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(minutes)], rollups.Queries.Select(query => query.Step).ToArray(), "both reads use the step of the width");
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
    public async Task ProcessesChartReaderReadsWithAnyWidth(double width)
    {
        // Arrange
        var range = Day();
        var expected = SeriesLadder.Choose(range.Start, range.End, _now, HistoryView.Target(width), _policy);

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        Script(rollups, range.Start);

        // Act
        await ChartReader(rollups).ReadAsync(null, range, width, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, rollups.Queries, "both reads happen");
        Assert.IsTrue(rollups.Queries.All(query => query.Step == expected), "the step of the target the history view makes of the width");
    }

    /// <summary>
    /// Without a source in the address the source with the latest bucket in the range is shown, and the choice lists the sources in the order of the store.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task ProcessesChartReaderDefaultsToTheSourceWithTheLatestBucket()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now.AddHours(-3)), ("import", _now.AddHours(-1)), ("zeta", _now.AddHours(-2))));

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("import", charts.Source, "the source with the latest bucket");
        Assert.AreSequenceEqual(["agent", "import", "zeta"], charts.Sources.ToArray(), "the order of the store");
        Assert.IsTrue(rollups.Queries.All(query => query.Source == "import"), "both reads are of that source");
    }

    /// <summary>
    /// With equal latest buckets the first listed source is shown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task ProcessesChartReaderTakesTheFirstListedOnATie()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("beta", _now.AddHours(-5)), ("agent", _now.AddHours(-1)), ("import", _now.AddHours(-1))));

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("agent", charts.Source, "the first listed of the latest buckets");
        Assert.IsTrue(rollups.Queries.All(query => query.Source == "agent"), "both reads are of that source");
    }

    /// <summary>
    /// A source of the address is read as it is, even when the list does not name it, and the list is still read for the choice.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task ProcessesChartReaderKeepsTheAddressSource()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now), ("import", _now.AddHours(-1))));

        Script(rollups, range.Start);

        // Act
        var listed = await ChartReader(rollups).ReadAsync("import", range, 200, TestContext.CancellationToken);
        var unlisted = await ChartReader(rollups).ReadAsync("gone-source", range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("import", listed.Source, "a listed source is kept although another has a later bucket");
        Assert.AreEqual("gone-source", unlisted.Source, "a source the list does not name is kept");
        Assert.AreSequenceEqual(["agent", "import"], unlisted.Sources.ToArray(), "the list is the store's");
        Assert.AreSequenceEqual(["import", "import", "gone-source", "gone-source"], rollups.Queries.Select(query => query.Source).ToArray(), "the reads are of the sources of the address");
        Assert.AreEqual(2, rollups.Sequence.Count(entry => entry == "Sources"), "the source list is read for each call");
    }

    /// <summary>
    /// A cut source list gives its note, before the step note.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task ProcessesChartReaderNotesACutSourceList()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(true, ("agent", _now)));

        rollups.Results[ProgramMeasure.Memory] = ProcessesSamples.Result(range.Start, ProgramMemoryKey.Pss, "nginx");
        rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(range.Start, null, "nginx");

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([CutListNote, "Each point is the average of 10 minutes; the legend shows the minimum and maximum."], charts.Notes.ToArray(), "the note of the cut list, then the step note");
    }

    /// <summary>
    /// A source without a bucket in the range leaves both charts out and says so alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ProcessesChartReaderLeavesTheChartsOutWithoutData()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        // Act
        var charts = await ChartReader(rollups).ReadAsync("agent", range, 200, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(charts.Memory, "no memory chart");
        Assert.IsNull(charts.Cpu, "no CPU chart");
        Assert.AreEqual("agent", charts.Source, "the source stays the address's");
        Assert.AreSequenceEqual([NoDataNote], charts.Notes.ToArray(), "the note of E1 alone");
    }

    /// <summary>
    /// With no source in the range and none in the address nothing is read but the list, the source is empty and the note says there is no data.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ProcessesChartReaderReadsNoRollupsWithoutASource()
    {
        // Arrange
        var range = Day();

        using var rollups = Reader(ProcessesSamples.SourceList(false));

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(string.Empty, charts.Source, "no source");
        Assert.IsEmpty(charts.Sources, "no listed source");
        Assert.IsNull(charts.Memory, "no memory chart");
        Assert.IsNull(charts.Cpu, "no CPU chart");
        Assert.AreSequenceEqual(["Sources"], rollups.Sequence.ToArray(), "only the list was read");
        Assert.AreSequenceEqual([NoDataNote], charts.Notes.ToArray(), "the note of E1");
    }

    /// <summary>
    /// The notes follow the memory read: a range that starts before the buckets, incomplete steps and steps not folded yet are all noted once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ProcessesChartReaderNotesWhatTheMemoryReadReports()
    {
        // Arrange
        var range = Day();
        var memory = new ProgramRollupResult
                     {
                         Times = ProcessesSamples.Times(range.Start, TimeSpan.FromMinutes(10), 2),
                         Programs = [ProcessesSamples.Line("nginx", ProcessesSamples.Point(1, 1, 1), ProcessesSamples.Point(1, 1, 1))],
                         Others = [0, 0],
                         Incomplete = [0, 3],
                         MemoryKey = ProgramMemoryKey.Pss,
                         StartsBeforeBuckets = true,
                         EarliestUnfolded = range.End.AddMinutes(-5)
                     };

        using var rollups = Reader(ProcessesSamples.SourceList(false, ("agent", _now)));

        rollups.Results[ProgramMeasure.Memory] = memory;
        rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(range.Start, null, "nginx");

        // Act
        var charts = await ChartReader(rollups).ReadAsync(null, range, 200, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([
                                    "This range starts before the oldest stored process data of this source.",
                                    "Some snapshots in this range do not list every program.",
                                    "Values after 2026-10-01 09:55:00 UTC are not complete yet and may still change.",
                                    "Each point is the average of 10 minutes; the legend shows the minimum and maximum."
                                ],
                                charts.Notes.ToArray(),
                                "the notes of the memory read in order");
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
    private static FakeProgramRollupReader Reader(ProgramSourceList sources)
    {
        return new FakeProgramRollupReader
               {
                   Sources = sources
               };
    }

    /// <summary>
    /// Scripts a memory and a CPU result with one program each.
    /// </summary>
    /// <param name="rollups">The rollup reader</param>
    /// <param name="start">The first step start</param>
    private static void Script(FakeProgramRollupReader rollups, DateTimeOffset start)
    {
        rollups.Results[ProgramMeasure.Memory] = ProcessesSamples.Result(start, ProgramMemoryKey.Pss, "nginx");
        rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(start, null, "nginx");
    }

    /// <summary>
    /// Creates the chart reader over a rollup reader with the policy and the clock of the tests.
    /// </summary>
    /// <param name="rollups">The rollup reader</param>
    /// <returns>The chart reader</returns>
    private static ProcessesChartReader ChartReader(FakeProgramRollupReader rollups)
    {
        return new ProcessesChartReader(rollups, _policy, new FakeTimeProvider(_now));
    }

    #endregion // Methods
}