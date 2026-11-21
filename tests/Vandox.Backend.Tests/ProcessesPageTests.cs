using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.Processes;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessesPage"/>. The tests read the page the way a browser shows it: the presets, <c>Apply</c>, <c>Compare from here</c> and <c>Cancel</c>
/// are buttons found by their text, the range fields are inputs named <c>from</c> and <c>to</c>, the source choice is a select named <c>source</c> whose option texts
/// are the sources, the two charts are the components with the class <c>history-chart</c> (the memory chart first), the panels are found by their <c>h2</c> headings
/// <c>Top processes</c> and <c>Comparison</c>, the link of a moment to the history view is a link with the text <c>History of this moment</c>, and a hostile text is
/// shown in an element with the class <c>escape</c>. A button that has no click handler submits the form that holds it. The click on a chart is the callback the
/// browser reports to the events object of the chart.
/// </summary>
[TestClass]
public partial class ProcessesPageTests
{
    #region Constants

    private const string FailureLog = "processes view: reading failed";

    private const string TopProcesses = "Top processes";

    private const string ComparisonHeading = "Comparison";

    private const string CompareFromHere = "Compare from here";

    private const string ArmedText = "Click a second point in a chart to compare.";

    private const string MemoryChart = "Memory per program";

    private const string CpuChart = "CPU per program";

    private const string MomentQuery = "?at=2026-10-13T11:00";

    private const string ComparisonQuery = "?source=agent&at=2026-10-13T11:00&compare=2026-10-13T11:30";

    private const string Agent = "agent";

    private const string Import = "import";

    private const ulong Mib = 1048576;

    #endregion // Constants

    #region Fields

    private static readonly string[] _comparisonWords = ["nginx", "mysqld", "redis", "new", "gone"];

    private static readonly string[] _presets = ["1 h", "24 h", "7 d", "30 d", "1 y"];

    private static readonly DateTimeOffset _momentAt = new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _compareAt = new(2026, 10, 13, 11, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _firstSnapshotAt = new(2026, 10, 13, 10, 59, 30, TimeSpan.Zero);

    private static readonly DateTimeOffset _secondSnapshotAt = new(2026, 10, 13, 11, 29, 30, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An address with an error shows the text of the error and the controls, draws no chart, shows no panel and calls neither reader nor browser.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <param name="text">The text of the error</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("?source=a%20b", "The address has an invalid value for the parameter source.")]
    [DataRow("?source=a&source=b", "The address gives the parameter source more than once.")]
    [DataRow("?range=2h", "The address has an invalid value for the parameter range.")]
    [DataRow("?compare=2026-10-13T11:30", ProcessesView.CompareNeedsMomentText)]
    public async Task ProcessesPageShowsOnlyTheControlsOnAnAddressError(string query, string text)
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(query);

        // Act
        await harness.AddAsync();

        // Assert
        Assert.Contains(text, harness.Text(), "the error of the address");
        Assert.AreEqual(5, _presets.Count(label => harness.Buttons(label).Count == 1), "the five presets are shown");
        Assert.IsEmpty(harness.WithClass("history-chart"), "no chart");
        Assert.IsEmpty(harness.Headings(), "no panel");
        Assert.IsEmpty(harness.Rollups.Calls, "no call of the rollup reader");
        Assert.IsEmpty(harness.Reader.Calls, "no call of the history reader");
        Assert.IsEmpty(harness.Runtime.Calls, "no call into the browser");
    }

    /// <summary>
    /// A range that begins after now says so and reads nothing, with or without a moment.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("?from=2026-10-14T00:00&to=2026-10-15T00:00")]
    [DataRow("?source=agent&from=2026-10-14T00:00&to=2026-10-15T00:00&at=2026-10-14T01:00")]
    public async Task ProcessesPageReadsNothingForAFutureRange(string query)
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(query);

        // Act
        await harness.AddAsync();

        // Assert
        Assert.Contains(HistoryView.FutureRangeText, harness.Text(), "the text of a future range");
        Assert.IsEmpty(harness.Rollups.Calls, "no call of the rollup reader");
        Assert.IsEmpty(harness.Reader.Calls, "no call of the history reader");
        Assert.IsEmpty(harness.WithClass("history-chart"), "no chart");
        Assert.IsEmpty(harness.Drawn, "nothing is drawn");
    }

    /// <summary>
    /// Both chart hosts are rendered before the first read has an answer, without a view, with the names of their charts; nothing is drawn yet.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ProcessesPageRendersBothChartHostsBeforeTheFirstRead()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ProcessesPageHarness(string.Empty);

        harness.Rollups.Hold = hold.Task;

        var add = harness.AddAsync();

        // Act
        await harness.WaitForRollupCallsAsync(1);

        // Assert
        var hosts = harness.WithClass("history-chart");

        Assert.HasCount(2, hosts, "both chart hosts are rendered while the first read runs");
        Assert.AreSequenceEqual([MemoryChart, CpuChart], hosts.Select(host => host.Attributes["aria-label"]).ToArray(), "the names of the hosts");
        Assert.IsEmpty(harness.Drawn, "nothing is drawn before the read has an answer");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// The first width starts the read of the charts and the second width of the other chart does not: one source list and one read per chart.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ProcessesPageReadsTheChartsOnceForBothWidths()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ProcessesPageHarness(string.Empty);

        harness.Rollups.Hold = hold.Task;

        var add = harness.AddAsync();

        // Act
        await harness.WaitForWidthsAsync(2);
        await harness.WaitForRollupCallsAsync(1);

        var whileHeld = harness.Rollups.Sequence.ToArray();

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await harness.WaitForDrawsAsync(2);

        // Assert
        Assert.AreSequenceEqual(["Sources"], whileHeld, "only the source list is asked for while the first read runs");
        Assert.AreSequenceEqual(["Sources", "Rollups Memory", "Rollups Cpu"], harness.Rollups.Sequence.ToArray(), "one read of the sources and of each chart");
        Assert.IsFalse(harness.Rollups.Calls[0].Token.IsCancellationRequested, "the first read was not superseded by the second width");
        Assert.HasCount(2, harness.Widths, "both charts reported their width");
        Assert.HasCount(2, harness.Drawn, "each chart is drawn once");
    }

    /// <summary>
    /// Each chart has its own accessible name, and the page has a heading of that name above it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ProcessesPageNamesEachChart()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(string.Empty);

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Assert
        Assert.AreSequenceEqual([MemoryChart, CpuChart], harness.WithClass("history-chart").Select(host => host.Attributes["aria-label"]).ToArray(), "the names of the hosts, the memory chart first");
        Assert.AreSequenceEqual([MemoryChart, CpuChart], harness.Headings().Take(2).ToArray(), "the headings above the charts");
    }

    /// <summary>
    /// A range without a source leaves both charts out and says there is no process data; nothing is read beyond the source list and nothing is drawn.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ProcessesPageLeavesTheChartsOutWithoutData()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(string.Empty);

        harness.Rollups.Sources = ProcessesSamples.SourceList(false);

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(ProcessesView.NoDataText);

        // Assert
        Assert.IsEmpty(harness.WithClass("history-chart"), "no chart host");
        Assert.IsEmpty(harness.Drawn, "nothing is drawn");
        Assert.AreSequenceEqual(["Sources"], harness.Rollups.Sequence.ToArray(), "only the source list is read");
        Assert.DoesNotContain(MemoryChart, harness.Headings(), "no heading of the memory chart");
    }

    /// <summary>
    /// The source choice lists the sources of the range in order, and a source of the address that is not listed is added.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task ProcessesPageOffersTheListedSourcesAndTheAddressSource()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness("?source=ghost");

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddMinutes(-5)), (Import, ProcessesPageHarness.Now.AddHours(-3)));

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Assert
        var texts = harness.Options("source").Select(option => option.Text).ToArray();

        Assert.AreSequenceEqual([Agent, Import], texts.Where(text => text != "ghost").ToArray(), "the listed sources in the order of the store");
        Assert.Contains("ghost", texts, "the source of the address is offered although it is not listed");
    }

    /// <summary>
    /// A value of the source choice that is not a listed source nor the source of the address writes nothing.
    /// </summary>
    /// <param name="value">The value the browser sends</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("forged")]
    [DataRow("99")]
    [DataRow("AGENT")]
    [DataRow(" agent")]
    [DataRow("")]
    public async Task ProcessesPageIgnoresAnUnlistedSourceChoice(string value)
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(string.Empty);

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddMinutes(-5)), (Import, ProcessesPageHarness.Now.AddHours(-3)));

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Act
        await harness.EnterAsync(("source", value));

        // Assert
        Assert.IsEmpty(harness.Navigation.Navigations, "nothing is navigated to");
        Assert.HasCount(2, harness.Rollups.Queries, "the charts are not read again");
    }

    /// <summary>
    /// A preset, <c>Apply</c> and a drag selection write the new range as a new history entry and keep the source, the moment and the second point.
    /// </summary>
    /// <param name="control">The control: <c>preset</c>, <c>apply</c> or <c>drag</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("preset")]
    [DataRow("apply")]
    [DataRow("drag")]
    public async Task ProcessesPageWritesANewEntryForPresetApplyDragAndSource(string control)
    {
        // Arrange
        var from = ProcessesPageHarness.Now.AddHours(-5);
        var to = ProcessesPageHarness.Now.AddHours(-3);
        var applied = (From: new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), To: new DateTimeOffset(2026, 10, 13, 10, 30, 15, TimeSpan.Zero));
        var expected = new Dictionary<string, string>
                       {
                           ["preset"] = Href(Agent, HistoryPreset.SevenDays, null, null, _momentAt, _compareAt),
                           ["apply"] = Href(Agent, null, applied.From, applied.To, _momentAt, _compareAt),
                           ["drag"] = Href(Agent, null, from, to, _momentAt, _compareAt)
                       };

        await using var harness = new ProcessesPageHarness(ComparisonQuery);

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Act
        await ChangeRangeAsync(harness, control, from, to);
        await harness.WaitForAsync(() => harness.Rollups.Queries.Count >= 4);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(expected[control], harness.Navigation.Navigations[0].Uri, "the address has the new range, the source, the moment and the second point");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// Another source writes an address without the moment and the second point, which belong to the other source, and keeps the range, as a new history entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task ProcessesPageDropsTheMomentsWhenTheSourceChanges()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness("?source=agent&range=7d&at=2026-10-13T11:00&compare=2026-10-13T11:30");

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddMinutes(-5)), (Import, ProcessesPageHarness.Now.AddHours(-3)));

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Act
        await harness.ChooseSourceAsync(Import);
        await harness.WaitForAsync(() => harness.Rollups.Queries.Count >= 4);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href(Import, HistoryPreset.SevenDays, null, null, null, null), harness.Navigation.Navigations[0].Uri, "the address has the new source and the range only");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// A click writes the moment, floored to the second, and drops the second point, replaces the entry of the history and reads neither chart again; the charts mark
    /// the moment.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("")]
    [DataRow(ComparisonQuery)]
    public async Task ProcessesPageClickSetsTheMomentAndReplacesTheEntry(string query)
    {
        // Arrange
        var at = ProcessesPageHarness.Now.AddHours(-2);

        await using var harness = new ProcessesPageHarness(query);

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        var events = harness.Events(0);

        // Act
        await harness.OnDispatcherAsync(() => events.Pick(at.ToUnixTimeSeconds() + 0.7, 8));
        await harness.WaitForAsync(() => harness.Marked.Count >= 2);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href(query.Length == 0 ? null : Agent, null, null, null, at, null), harness.Navigation.Navigations[0].Uri, "the address has the moment, floored to the second, and no second point");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.HasCount(2, harness.Rollups.Queries, "the charts are not read again");
        Assert.HasCount(2, harness.Drawn, "the charts are not drawn again");
        Assert.IsTrue(harness.Marked.All(call => call.Arguments[0] is double seconds && (long)Math.Round(seconds) == at.ToUnixTimeSeconds()), "the charts mark the moment");
    }

    /// <summary>
    /// <c>Compare from here</c> arms the second point; the next click writes it as <c>compare</c>, keeps the moment and replaces the entry of the history, and the
    /// panel <c>Comparison</c> takes the place of <c>Top processes</c>.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesPageSecondClickWritesCompare()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(MomentQuery);

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        var events = harness.Events(1);

        // Act
        await harness.WaitForAsync(() => harness.Buttons(CompareFromHere).Count == 1);
        await harness.PressAsync(CompareFromHere);
        await harness.WaitForTextAsync(ArmedText);
        await harness.OnDispatcherAsync(() => events.Pick(_compareAt.ToUnixTimeSeconds() + 0.6, 8));
        await harness.WaitForAsync(() => harness.Headings().Contains(ComparisonHeading));

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href(null, null, null, null, _momentAt, _compareAt), harness.Navigation.Navigations[0].Uri, "the address keeps the moment and has the second point");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.DoesNotContain(TopProcesses, harness.Headings(), "the comparison takes the place of the top processes");
        Assert.DoesNotContain(ArmedText, harness.Text(), "the second point is no longer awaited");
        Assert.HasCount(2, harness.Rollups.Queries, "the charts are not read again");
    }

    /// <summary>
    /// The panel shows the process snapshot of the shown source closest to the moment, with the link to the history view of that moment, which does not name a
    /// source; a closer snapshot of another source is not shown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageShowsTopProcessesWithTheHistoryLink()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(MomentQuery);

        AddSnapshot(harness, 1, Agent, _firstSnapshotAt, ("agentprog", 100 * Mib, 1.5, 80 * Mib));
        AddSnapshot(harness, 2, Import, _momentAt.AddSeconds(5), ("importprog", 90 * Mib, 1.0, null));

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync("agentprog");

        // Assert
        var text = Collapsed(harness.Text());
        var links = harness.Links("History of this moment");

        Assert.Contains(TopProcesses, harness.Headings(), "the heading of the panel");
        Assert.Contains("Moment 2026-10-13 11:00:00 UTC", text, "the moment");
        Assert.Contains("Process snapshot of 2026-10-13 10:59:30 UTC", text, "the capture time of the snapshot");
        Assert.DoesNotContain("importprog", text, "the snapshot of the other source is not shown although it is closer");
        Assert.HasCount(1, links, "one link to the history view");
        Assert.Contains("history?", links[0].Attributes["href"], "the link goes to the history view");
        Assert.Contains($"at={LogsAddress.FormatTime(_momentAt)}", links[0].Attributes["href"], "the link has the moment");
        Assert.DoesNotContain("source", links[0].Attributes["href"], "the link names no source, so the history view shows the closest snapshot of any source");
        Assert.IsTrue(harness.StampSources.All(source => source == Agent), "every stamp read is for the source of the charts");
    }

    /// <summary>
    /// A moment without a snapshot of the source within ten minutes shows the text of the missing snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageSaysNoSnapshotWithinTenMinutes()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(MomentQuery);

        AddSnapshot(harness, 1, Agent, _momentAt.AddMinutes(-20), ("agentprog", 100 * Mib, 1.5, null));
        AddSnapshot(harness, 2, Import, _momentAt.AddSeconds(5), ("importprog", 90 * Mib, 1.0, null));

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(HistoryView.NoProcessSnapshotText);

        // Assert
        Assert.DoesNotContain("agentprog", harness.Text(), "the snapshot twenty minutes away is not shown");
        Assert.DoesNotContain("importprog", harness.Text(), "the snapshot of the other source is not shown");
        Assert.Contains(TopProcesses, harness.Headings(), "the panel is shown");
    }

    /// <summary>
    /// With the source in the address the panel reads at once, while the charts still wait for their answer, and reads the snapshot of that source only.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageReadsTheMomentOfTheAddressSourceAtOnce()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ProcessesPageHarness("?source=import&at=2026-10-13T11:00");

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddMinutes(-5)), (Import, ProcessesPageHarness.Now.AddHours(-3)));
        harness.Rollups.Hold = hold.Task;

        AddSnapshot(harness, 1, Agent, _momentAt.AddSeconds(-10), ("agentprog", 100 * Mib, 1.5, null));
        AddSnapshot(harness, 2, Import, _momentAt.AddSeconds(-60), ("importprog", 90 * Mib, 1.0, null));

        var add = harness.AddAsync();

        // Act
        await harness.WaitForTextAsync("importprog");

        // Assert
        Assert.IsEmpty(harness.Drawn, "the charts still wait for their answer");
        Assert.DoesNotContain("agentprog", harness.Text(), "the snapshot of the other source is not shown");
        Assert.IsTrue(harness.StampSources.All(source => source == Import), "every stamp read is for the source of the address");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// Without a source in the address the panel waits for the source of the charts, then shows its snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageSaysLoadingUntilTheChartsChooseASource()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ProcessesPageHarness(MomentQuery);

        harness.Rollups.Hold = hold.Task;

        AddSnapshot(harness, 1, Agent, _firstSnapshotAt, ("agentprog", 100 * Mib, 1.5, null));

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(1);

        // Act
        var whileWaiting = Collapsed(harness.Text());
        var readsWhileWaiting = harness.StampReads.Count;

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await harness.WaitForTextAsync("agentprog");

        // Assert
        Assert.Contains(HistoryView.LoadingText, whileWaiting, "the waiting text");
        Assert.AreEqual(0, readsWhileWaiting, "no snapshot is read before the charts chose a source");
        Assert.IsTrue(harness.StampSources.All(source => source == Agent), "the snapshot is read for the source of the charts");
    }

    /// <summary>
    /// When no source has process data in the range the panel says so and reads nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageSaysNoSourceInTheRange()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(MomentQuery);

        harness.Rollups.Sources = ProcessesSamples.SourceList(false);

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(ProcessesView.NoSourceText);

        // Assert
        Assert.Contains(ProcessesView.NoDataText, harness.Text(), "the charts say there is no data");
        Assert.IsEmpty(harness.Reader.Calls, "no snapshot is read");
    }

    /// <summary>
    /// When the read of the charts fails or runs into the deadline the panel says the source could not be read, below the text of the charts, and reads nothing.
    /// </summary>
    /// <param name="deadline">Whether the read runs into the deadline instead of failing</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProcessesPageSaysTheSourceCouldNotBeRead(bool deadline)
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chartText = deadline ? ProcessesView.ChartTimeoutText : ProcessesView.ChartFailureText;

        await using var harness = new ProcessesPageHarness(MomentQuery);

        if (deadline)
        {
            harness.Rollups.Hold = hold.Task;
        }
        else
        {
            harness.Rollups.Failure = new InvalidOperationException("detail of the store");
        }

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(1);

        // Act
        if (deadline)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        await harness.WaitForTextAsync(chartText);
        await harness.WaitForTextAsync(ProcessesView.SourceFailureText);

        // Assert
        var text = harness.Text();

        Assert.IsLessThan(text.IndexOf(ProcessesView.SourceFailureText, StringComparison.Ordinal), text.IndexOf(chartText, StringComparison.Ordinal), "the text of the source below the text of the charts");
        Assert.IsEmpty(harness.Reader.Calls, "no snapshot is read");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A range in the future shows no panel for a moment and reads no snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageShowsNoPanelForAFutureRange()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness("?source=agent&from=2026-10-14T00:00&to=2026-10-15T00:00&at=2026-10-14T01:00&compare=2026-10-14T02:00");

        // Act
        await harness.AddAsync();

        // Assert
        Assert.Contains(HistoryView.FutureRangeText, harness.Text(), "the text of a future range");
        Assert.DoesNotContain(TopProcesses, harness.Headings(), "no panel of the top processes");
        Assert.DoesNotContain(ComparisonHeading, harness.Headings(), "no panel of the comparison");
        Assert.IsEmpty(harness.Reader.Calls, "no snapshot is read");
    }

    /// <summary>
    /// A range change that moves the default source reads the moment again for the new source and shows its snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesPageReadsTheMomentAgainWhenTheDefaultSourceChanges()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(MomentQuery);

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddMinutes(-5)), (Import, ProcessesPageHarness.Now.AddHours(-3)));

        AddSnapshot(harness, 1, Agent, _firstSnapshotAt, ("agentprog", 100 * Mib, 1.5, null));
        AddSnapshot(harness, 2, Import, _momentAt.AddSeconds(-20), ("importprog", 90 * Mib, 1.0, null));

        await harness.AddAsync();
        await harness.WaitForTextAsync("agentprog");

        var readsBefore = harness.StampReads.Count;

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddHours(-30)), (Import, ProcessesPageHarness.Now.AddHours(-1)));

        // Act
        await harness.GoToAsync("?range=7d&at=2026-10-13T11:00");
        await harness.WaitForTextAsync("importprog");

        // Assert
        Assert.DoesNotContain("agentprog", harness.Text(), "the snapshot of the former source is gone");
        Assert.IsTrue(harness.StampSources.Take(readsBefore).All(source => source == Agent), "the first reads are for the first default source");
        Assert.IsTrue(harness.StampSources.Skip(readsBefore).All(source => source == Import), "the later reads are for the new default source");
    }

    /// <summary>
    /// A range change that moves the default source reads the comparison again for the new source.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesPageComparesAgainWhenTheDefaultSourceChanges()
    {
        // Arrange
        const string query = "?at=2026-10-13T11:00&compare=2026-10-13T11:30";

        await using var harness = new ProcessesPageHarness(query);

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddMinutes(-5)), (Import, ProcessesPageHarness.Now.AddHours(-3)));

        AddSnapshot(harness, 1, Agent, _firstSnapshotAt, ("agentprog", 100 * Mib, 1.5, null));
        AddSnapshot(harness, 2, Agent, _secondSnapshotAt, ("agentprog", 120 * Mib, 1.5, null));
        AddSnapshot(harness, 3, Import, _firstSnapshotAt, ("importprog", 90 * Mib, 1.0, null));
        AddSnapshot(harness, 4, Import, _secondSnapshotAt, ("importprog", 95 * Mib, 1.0, null));

        await harness.AddAsync();
        await harness.WaitForTextAsync("(agent)");

        var readsBefore = harness.StampReads.Count;

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, ProcessesPageHarness.Now.AddHours(-30)), (Import, ProcessesPageHarness.Now.AddHours(-1)));

        // Act
        await harness.GoToAsync("?range=7d&at=2026-10-13T11:00&compare=2026-10-13T11:30");
        await harness.WaitForTextAsync("(import)");

        // Assert
        Assert.Contains("importprog", harness.Text(), "the programs of the new source");
        Assert.DoesNotContain("agentprog", harness.Text(), "the programs of the former source are gone");
        Assert.IsTrue(harness.StampSources.Skip(readsBefore).All(source => source == Import), "the later reads are for the new default source");
    }

    /// <summary>
    /// The comparison shows the heading with both points and the source, a row per program with the state of a new and a gone program, and nothing of another source.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesPageShowsTheComparisonOfTheSourceSnapshots()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(ComparisonQuery);

        var first = AddSnapshot(harness, 1, Agent, _firstSnapshotAt, ("nginx", 100 * Mib, 1.5, 80 * Mib), ("mysqld", 50 * Mib, 2.0, 40 * Mib));
        var second = AddSnapshot(harness, 2, Agent, _secondSnapshotAt, ("nginx", 150 * Mib, 1.5, 120 * Mib), ("redis", 40 * Mib, 0.5, 30 * Mib));

        AddSnapshot(harness, 3, Import, _momentAt, ("importprog", 90 * Mib, 1.0, null));

        var expected = ProgramComparisonView.Compare(ProgramFigures.FromRecord(first), ProgramFigures.FromRecord(second));

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(expected.Heading);

        // Assert
        var text = Collapsed(harness.Text());

        Assert.Contains(ComparisonHeading, harness.Headings(), "the heading of the panel");
        Assert.DoesNotContain(TopProcesses, harness.Headings(), "the panel of the top processes is replaced");
        Assert.Contains("2026-10-13 10:59:30 UTC to 2026-10-13 11:29:30 UTC (agent)", text, "both points and the source");
        Assert.IsTrue(_comparisonWords.All(word => text.Contains(word, StringComparison.Ordinal)), "the programs and the states");
        Assert.DoesNotContain("importprog", text, "no program of the other source");
    }

    /// <summary>
    /// A comparison that has more rows than the cap ends with the closing line of the rows left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesPageShowsTheClosingLineOfTheComparison()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(ComparisonQuery);

        var first = AddSnapshot(harness, 1, Agent, _firstSnapshotAt, ("base", 10 * Mib, 1.0, 10 * Mib));
        var second = AddSnapshot(harness, 2, Agent, _secondSnapshotAt, [.. Enumerable.Range(0, 22).Select(index => ($"program{index:D2}", (ulong)(index + 1) * Mib, 1.0, (ulong?)((ulong)(index + 1) * Mib)))]);
        var expected = ProgramComparisonView.Compare(ProgramFigures.FromRecord(first), ProgramFigures.FromRecord(second));

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(expected.Heading);

        // Assert
        Assert.StartsWith("and 3 more programs (", expected.Closing, "the expected closing line counts the rows left out");
        Assert.Contains(expected.Closing, Collapsed(harness.Text()), "the closing line");
    }

    /// <summary>
    /// A snapshot that does not list every program is explained, nothing is marked new or gone and the closing line has no counts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesPageExplainsAnIncompleteComparison()
    {
        // Arrange
        await using var harness = new ProcessesPageHarness(ComparisonQuery);

        var first = HistorySamples.Record(1,
                                          Agent,
                                          _firstSnapshotAt,
                                          new ProcessSnapshot
                                          {
                                              Complete = false,
                                              Programs = [HistorySamples.Program("base", 1, 1.0, 10 * Mib)]
                                          });
        var second = AddSnapshot(harness, 2, Agent, _secondSnapshotAt, [.. Enumerable.Range(0, 22).Select(index => ($"program{index:D2}", (ulong)(index + 1) * Mib, 1.0, (ulong?)null))]);

        harness.Reader.Stamps.Add(HistorySamples.Stamp(1, RecordKind.ProcessSnapshot, _firstSnapshotAt));
        harness.Reader.Records[1] = first;

        var expected = ProgramComparisonView.Compare(ProgramFigures.FromRecord(first), ProgramFigures.FromRecord(second));

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(expected.Heading);

        // Assert
        var text = Collapsed(harness.Text());

        Assert.Contains(ProcessesView.IncompleteComparisonNote, text, "the note");
        Assert.Contains("and 3 more programs.", text, "the closing line has no counts of new and gone programs");
        Assert.DoesNotContain("more programs (", text, "no counts");
    }

    /// <summary>
    /// A point without a snapshot, or two points that show one snapshot, are named in the panel.
    /// </summary>
    /// <param name="snapshotAt">The capture time of the only snapshot, in minutes after 11:00</param>
    /// <param name="text">The text of the panel</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow(25, ProcessesView.NoSnapshotFirstPointText)]
    [DataRow(5, ProcessesView.NoSnapshotSecondPointText)]
    [DataRow(15, ProcessesView.SameSnapshotText)]
    public async Task ProcessesPageSaysWhichPointHasNoSnapshot(int snapshotAt, string text)
    {
        // Arrange
        await using var harness = new ProcessesPageHarness("?source=agent&at=2026-10-13T11:10&compare=2026-10-13T11:20");

        AddSnapshot(harness, 1, Agent, _momentAt.AddMinutes(snapshotAt), ("nginx", 100 * Mib, 1.5, null));

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(text);

        // Assert
        Assert.Contains(ComparisonHeading, harness.Headings(), "the panel is shown");
    }

    /// <summary>
    /// A program name in the snapshot of the top processes or in the comparison is shown as external text: markup is text and a direction character is a visible escape.
    /// </summary>
    /// <param name="comparison">Whether the comparison is shown instead of the top processes</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProcessesPageShowsSnapshotTextsAsExternalText(bool comparison)
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var evil = $"ev{bidi}il";
        var markup = "<script>alert(1)</script>";

        await using var harness = new ProcessesPageHarness(comparison ? ComparisonQuery : "?source=agent&at=2026-10-13T11:00");

        AddSnapshot(harness, 1, Agent, _firstSnapshotAt, (evil, 10 * Mib, 1.0, null), (markup, 5 * Mib, 1.0, null));
        AddSnapshot(harness, 2, Agent, _secondSnapshotAt, (evil, 20 * Mib, 1.0, null), (markup, 6 * Mib, 1.0, null));

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Enc(markup), StringComparison.Ordinal));

        // Assert
        var elements = harness.Renderer.Elements(ProcessesPageHarness.RootId);

        Assert.Contains(bidi, evil, "the input holds U+202E");
        Assert.DoesNotContain("<script>", harness.Html(), "no markup from external text");
        Assert.DoesNotContain(bidi, harness.Html(), "no direction character in the page");
        Assert.Contains(element => element.Classes.Contains("escape") && element.Text == "\\u202e", elements, "the direction character is a visible escape");
        Assert.IsTrue(elements.All(element => element.Tag != "script"), "no script element");
    }

    /// <summary>
    /// A program name in a chart label is escaped and capped before it reaches the script; no direction character reaches the browser.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ProcessesPageEscapesProgramLabels()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var evil = $"ev{bidi}il";

        await using var harness = new ProcessesPageHarness(string.Empty);

        harness.Rollups.Results[ProgramMeasure.Memory] = ProcessesSamples.Result(ProcessesPageHarness.Now.AddHours(-2), ProgramMemoryKey.Pss, evil);
        harness.Rollups.Results[ProgramMeasure.Cpu] = ProcessesSamples.Result(ProcessesPageHarness.Now.AddHours(-2), null, evil);

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Assert
        var labels = harness.Drawn.SelectMany(LabelsOf).ToArray();

        Assert.Contains(bidi, evil, "the input holds U+202E");
        Assert.HasCount(4, labels, "the program and the other programs of both charts");
        Assert.AreEqual(2, labels.Count(label => label == LogText.Escape(evil, HistoryView.LabelChars)), "the program is labelled with its escaped name in both charts");
        Assert.DoesNotContain(label => label.Contains(bidi, StringComparison.Ordinal), labels, "no direction character in a label");
        Assert.DoesNotContain(bidi, harness.Html(), "no direction character in the page");
    }

    /// <summary>
    /// A read of the charts and a read of the snapshots that run past ten seconds are stopped with the text of the deadline.
    /// </summary>
    /// <param name="chart">Whether the read of the charts is held, else the read of the snapshots</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProcessesPageShowsTheDeadlineText(bool chart)
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ProcessesPageHarness("?source=agent&at=2026-10-13T11:00");

        if (chart)
        {
            harness.Rollups.Hold = hold.Task;
        }
        else
        {
            harness.Reader.Hold = hold.Task;
        }

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(1);
        await harness.WaitForStampReadsAsync(1);

        // Act
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        await harness.WaitForTextAsync(ProcessesView.ChartTimeoutText);

        // Assert
        var held = chart ? harness.Rollups.Calls[0].Token : harness.StampReads[0].Token;

        Assert.IsTrue(held.IsCancellationRequested, "the held read was cancelled at the deadline");
        Assert.DoesNotContain(ProcessesView.ChartFailureText, harness.Text(), "the deadline is not a failure");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A failure of the store shows the fixed text without its details and is logged with its exception at level error, event 78, and without any value of the
    /// address, for the charts and for the snapshots.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ProcessesPageShowsTheFailureTextAndLogsWithoutAddressValue()
    {
        // Arrange
        var chartFailure = new InvalidOperationException("secret detail of the rollups");
        var momentFailure = new StoreException("secret detail of the snapshots");

        await using var harness = new ProcessesPageHarness("?source=secretname&at=2026-10-13T11:00&compare=2026-10-13T11:30");

        harness.Rollups.Failure = chartFailure;
        harness.Reader.Failure = momentFailure;

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.Logger.Exceptions.Contains(chartFailure) && harness.Logger.Exceptions.Contains(momentFailure));
        await harness.WaitForTextAsync(ProcessesView.ChartFailureText);

        // Assert
        Assert.DoesNotContain("secret detail", harness.Html(), "no detail of a failure");
        Assert.Contains(FailureLog, harness.Logger.Messages, "the message of the log method");
        Assert.Contains(exception => ReferenceEquals(exception, chartFailure), harness.Logger.Exceptions, "the exception of the charts is logged");
        Assert.Contains(exception => ReferenceEquals(exception, momentFailure), harness.Logger.Exceptions, "the exception of the snapshots is logged");
        Assert.IsTrue(harness.Logger.Entries.Where(entry => entry.EventId.Id == 78).All(entry => entry.Level == LogLevel.Error), "the entries of event 78 are errors");
        Assert.Contains(entry => entry.EventId.Id == 78, harness.Logger.Entries, "event 78");
        Assert.DoesNotContain(message => message.Contains("secretname", StringComparison.Ordinal) || message.Contains("2026", StringComparison.Ordinal), harness.Logger.Messages, "no value of the address in the log");
    }

    /// <summary>
    /// Leaving the page cancels the read of the charts and the read of the snapshots.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ProcessesPageCancelsReadsWhenLeft()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ProcessesPageHarness("?source=agent&at=2026-10-13T11:00");

        harness.Rollups.Hold = hold.Task;
        harness.Reader.Hold = hold.Task;

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(1);
        await harness.WaitForStampReadsAsync(1);

        var chart = harness.Rollups.Calls[0].Token;
        var moment = harness.StampReads[0].Token;

        // Act
        await harness.DisposePageAsync();

        // Assert
        Assert.IsTrue(chart.IsCancellationRequested, "the read of the charts was cancelled");
        Assert.IsTrue(moment.IsCancellationRequested, "the read of the snapshots was cancelled");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes the address that the page writes for a state, as the page does.
    /// </summary>
    /// <param name="source">The source, or <see langword="null"/> for none</param>
    /// <param name="preset">The preset, or <see langword="null"/> for none</param>
    /// <param name="from">The start of a fixed range, or <see langword="null"/></param>
    /// <param name="to">The end of a fixed range, or <see langword="null"/></param>
    /// <param name="at">The moment, or <see langword="null"/></param>
    /// <param name="compare">The second point, or <see langword="null"/></param>
    /// <returns>The relative address</returns>
    private static string Href(string? source, HistoryPreset? preset, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? at, DateTimeOffset? compare)
    {
        return ProcessesView.ProcessesHref(new ProcessesAddress
                                           {
                                               Source = source,
                                               Preset = preset,
                                               From = from,
                                               To = to,
                                               At = at,
                                               Compare = compare
                                           });
    }

    /// <summary>
    /// Changes the range with a control of the page.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="control">The control: <c>preset</c>, <c>apply</c> or <c>drag</c></param>
    /// <param name="from">The start of the drag selection</param>
    /// <param name="to">The end of the drag selection</param>
    /// <returns>A task that completes when the control is done</returns>
    private static async Task ChangeRangeAsync(ProcessesPageHarness harness, string control, DateTimeOffset from, DateTimeOffset to)
    {
        if (control == "preset")
        {
            await harness.PressAsync("7 d");
        }
        else if (control == "apply")
        {
            await harness.EnterAsync(("from", "2026-10-13T08:00"), ("to", "2026-10-13T10:30:15"));
            await harness.PressAsync("Apply");
        }
        else
        {
            var events = harness.Events(0);

            await harness.OnDispatcherAsync(() => events.Select(from.ToUnixTimeSeconds() + 0.25, to.ToUnixTimeSeconds() - 0.25));
        }
    }

    /// <summary>
    /// Stores a process snapshot the history reader finds by its time and source.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="id">The record ID</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs: name, resident bytes, CPU percent and proportional bytes</param>
    /// <returns>The stored record</returns>
    private static StoredRecord AddSnapshot(ProcessesPageHarness harness, long id, string source, DateTimeOffset capturedAt, params (string Program, ulong Rss, double Cpu, ulong? Pss)[] programs)
    {
        var record = ProcessesSamples.Snapshot(id, source, capturedAt, programs);

        harness.Reader.Stamps.Add(HistorySamples.Stamp(id, RecordKind.ProcessSnapshot, capturedAt));
        harness.Reader.Records[id] = record;

        return record;
    }

    /// <summary>
    /// Gets the labels of the lines of a chart that was drawn.
    /// </summary>
    /// <param name="draw">The call that drew the chart</param>
    /// <returns>The labels in order</returns>
    private static IEnumerable<string> LabelsOf(RecordedJsCall draw)
    {
        var setup = JsonSerializer.SerializeToElement(draw.Arguments[1], JsonSerializerOptions.Web);

        return [.. setup.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("label").GetString() ?? string.Empty)];
    }

    /// <summary>
    /// Encodes a text as the page encodes it.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The encoded text</returns>
    private static string Enc(string text)
    {
        return HtmlEncoder.Default.Encode(text);
    }

    /// <summary>
    /// Collapses the white space of a text to single spaces.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The collapsed text</returns>
    private static string Collapsed(string text)
    {
        return WhiteSpace().Replace(text, " ").Trim();
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    #endregion // Patterns
}