using System.Text.Json;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.Network;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkPage"/>. The tests read the page the way a browser shows it: the presets, <c>Apply</c> and the range fields are found as on the processes
/// page, the source choice is a select named <c>source</c> whose option texts are the sources, the two charts are the components with the class <c>history-chart</c>
/// (the states first), the sections are found by their <c>h2</c> headings (<c>Connections per state</c>, <c>Sockets held per program</c>, <c>Top remote addresses</c>,
/// <c>Listeners</c>), the tables by their captions, a cell is a <c>td</c>, and a hostile text is shown in an element with the class <c>escape</c>. A click or a drag in a
/// chart is the callback the browser reports to the events object of the chart.
/// </summary>
[TestClass]
public class NetworkPageTests
{
    #region Constants

    private const string FailureLog = "network view: reading failed";

    private const string StatesChart = "Connections per state";

    private const string ProgramsChart = "Sockets held per program";

    private const string AddressesHeading = "Top remote addresses";

    private const string ListenersHeading = "Listeners";

    private const string Agent = NetworkPageHarness.Agent;

    private const string Legacy = "legacy-monitor";

    private const string Markup = "<script>alert(1)</script>";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _momentAt = new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private static readonly string[] _controls = [char.ConvertFromUtf32(0x202e), char.ConvertFromUtf32(0x200b), char.ConvertFromUtf32(0x2066)];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The page has the four sections in order, the heading of each chart is its accessible name, and the program chart has the note about the sockets counted once per
    /// holding process between its heading and its chart.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task NetworkPageShowsProgramNote()
    {
        // Arrange
        await using var harness = new NetworkPageHarness(string.Empty);

        // Act
        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Assert
        var text = harness.Text();
        var notes = harness.Renderer.Elements(NetworkPageHarness.RootId).Where(element => element.Tag == "p" && element.Classes.Contains("note") && element.Text == NetworkView.ProgramNoteText).ToArray();

        Assert.AreSequenceEqual([StatesChart, ProgramsChart, AddressesHeading, ListenersHeading], harness.Headings().ToArray(), "the headings of the four sections");
        Assert.AreSequenceEqual([StatesChart, ProgramsChart], harness.WithClass("history-chart").Select(host => host.Attributes["aria-label"]).ToArray(), "the names of the charts are their headings");
        Assert.HasCount(1, notes, "the note about the sockets per holding process is shown once");
        Assert.IsLessThan(text.IndexOf(NetworkView.ProgramNoteText, StringComparison.Ordinal), text.IndexOf(ProgramsChart, StringComparison.Ordinal), "the note below the heading of the program chart");
        Assert.IsLessThan(text.IndexOf(AddressesHeading, StringComparison.Ordinal), text.IndexOf(NetworkView.ProgramNoteText, StringComparison.Ordinal), "the note above the table of the addresses");
        Assert.HasCount(2, harness.Drawn, "both charts are drawn");
    }

    /// <summary>
    /// A click in either chart writes the moment, floored to the second, keeps the source and the preset, replaces the entry of the history and reads neither the charts
    /// nor the table again; the charts mark the moment.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <param name="chart">The place of the clicked chart in the order of the draws</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow("", 0)]
    [DataRow("?source=agent&range=7d", 1)]
    public async Task NetworkPageClickReplacesEntryWithMoment(string query, int chart)
    {
        // Arrange
        var at = NetworkPageHarness.Now.AddHours(-2);
        var expected = new NetworkAddress
                       {
                           Source = query.Length == 0 ? null : Agent,
                           Preset = query.Length == 0 ? null : HistoryPreset.SevenDays,
                           At = at
                       };

        await using var harness = new NetworkPageHarness(query);

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        var events = harness.Events(chart);

        // Act
        await harness.OnDispatcherAsync(() => events.Pick(at.ToUnixTimeSeconds() + 0.7, 8));
        await harness.WaitForAsync(() => harness.Marked.Count >= 2);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(NetworkView.NetworkHref(expected), harness.Navigation.Navigations[0].Uri, "the address has the moment, floored to the second");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.HasCount(2, harness.Rollups.Queries, "the charts and the table are not read again");
        Assert.HasCount(2, harness.Drawn, "the charts are not drawn again");
        Assert.IsTrue(harness.Marked.All(call => call.Arguments[0] is double seconds && (long)Math.Round(seconds) == at.ToUnixTimeSeconds()), "the charts mark the moment");
    }

    /// <summary>
    /// A drag selection in either chart writes its range as a fixed range in a new entry of the history and keeps the source and the moment.
    /// </summary>
    /// <param name="chart">The place of the dragged chart in the order of the draws</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(0)]
    [DataRow(1)]
    public async Task NetworkPageSelectionWritesRange(int chart)
    {
        // Arrange
        var from = NetworkPageHarness.Now.AddHours(-5);
        var to = NetworkPageHarness.Now.AddHours(-3);
        var expected = new NetworkAddress
                       {
                           Source = Agent,
                           From = from,
                           To = to,
                           At = _momentAt
                       };

        await using var harness = new NetworkPageHarness("?source=agent&at=2026-10-13T11:00");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        var events = harness.Events(chart);

        // Act
        await harness.OnDispatcherAsync(() => events.Select(from.ToUnixTimeSeconds() + 0.25, to.ToUnixTimeSeconds() - 0.25));

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(NetworkView.NetworkHref(expected), harness.Navigation.Navigations[0].Uri, "the address has the range, the source and the moment");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// The table of the addresses is read only after the charts ended, for the source, the range and the step of the charts, shows <c>Loading…</c> until its read ends and
    /// then its rows.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task NetworkPageReadsAddressesAfterCharts()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var addressHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new NetworkPageHarness(string.Empty);

        harness.Rollups.Hold = hold.Task;
        harness.HoldAddresses(addressHold.Task);

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(1);

        var whileHeld = harness.Rollups.Sequence.ToArray();

        // Act
        hold.SetResult();

        await harness.WaitForRollupCallsAsync(3);
        await harness.WaitForAsync(() => Section(harness, AddressesHeading, ListenersHeading).Contains(NetworkView.LoadingText, StringComparison.Ordinal));

        var whileLoading = TableRows(harness, AddressesHeading);

        addressHold.SetResult();

        await harness.WaitForTextAsync(NetworkPageHarness.FirstAddress);
        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var chart = harness.Rollups.Queries[0];
        var address = harness.Rollups.Queries[1];

        Assert.AreSequenceEqual(["Sources"], whileHeld, "the table is not read while the charts are read");
        Assert.AreSequenceEqual(["Sources", "Rollups", "Addresses"], harness.Rollups.Sequence.ToArray(), "the sources, the rollups and then the addresses");
        Assert.AreEqual(Agent, address.Source, "the source of the charts");
        Assert.AreEqual(chart.From, address.From, "the start of the charts");
        Assert.AreEqual(chart.To, address.To, "the end of the charts");
        Assert.AreEqual(chart.Step, address.Step, "the step of the charts");
        Assert.AreNotEqual(TimeSpan.Zero, address.Step, "the step is the one of the charts");
        Assert.IsEmpty(whileLoading, "no row while the table is read");
        Assert.AreSequenceEqual([NetworkPageHarness.FirstAddress + "|4.5|9"], TableRows(harness, AddressesHeading).ToArray(), "the row of the address with its average and its highest count");
        Assert.Contains(NetworkView.AddressNoteText, harness.Text(), "the note of the table");
    }

    /// <summary>
    /// The panel shows the snapshot of the source of the charts: without a source in the address it waits (<c>Loading…</c>, no read) until the charts chose one, then reads
    /// the latest snapshot of the range for it, and a change of the moment reads the panel again but not the charts; a source in the address is read at once.
    /// </summary>
    /// <param name="listed">Whether the address names the source</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NetworkPagePanelFollowsChartSource(bool listed)
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = listed ? Legacy : Agent;
        var captured = NetworkPageHarness.Now.AddMinutes(listed ? -30 : -5);

        await using var harness = new NetworkPageHarness(listed ? "?source=legacy-monitor" : string.Empty);

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, NetworkPageHarness.Now.AddMinutes(-5)), (Legacy, NetworkPageHarness.Now.AddHours(-1)));
        harness.AddConnections(NetworkSamples.Connections(2, Legacy, NetworkPageHarness.Now.AddMinutes(-30), "all", NetworkSamples.Tcp(80)));

        if (listed == false)
        {
            harness.Rollups.Hold = hold.Task;
        }

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(1);

        var whileHeld = Section(harness, ListenersHeading, null);
        var readsWhileHeld = listed ? -1 : harness.Reader.Calls.Count;

        // Act
        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await harness.WaitForTextAsync($"Listeners of {LogsView.ShowSeconds(captured)} UTC ({expected})");

        var latest = harness.LatestQueries[^1];
        var chart = harness.Rollups.Queries[0];

        // Assert
        Assert.AreEqual(RecordKind.ConnectionSnapshot, latest.Kind, "the kind of the read");
        Assert.AreEqual(expected, latest.Source, "the panel reads the source of the charts");
        Assert.AreEqual(chart.From, latest.From, "the start of the range");
        Assert.AreEqual(chart.To, latest.To, "the end of the range");
        Assert.AreEqual(expected, chart.Source, "the charts show the same source");

        if (listed == false)
        {
            Assert.Contains(NetworkView.LoadingText, whileHeld, "the panel waits for the charts to choose a source");
            Assert.AreEqual(0, readsWhileHeld, "no snapshot is read before a source is known");

            await harness.GoToAsync("?at=2026-10-13T11:56");
            await harness.WaitForAsync(() => harness.StampQueries.Count >= 1);

            Assert.IsTrue(harness.StampQueries.All(query => query.Source == Agent && query.Kind == RecordKind.ConnectionSnapshot), "the snapshot near the moment is read for the source of the charts");
            Assert.HasCount(3, harness.Rollups.Calls, "a new moment reads neither the charts nor the table again");
        }
    }

    /// <summary>
    /// Another source writes an address without the moment, which belongs to the other source, keeps the range and writes a new entry of the history.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task NetworkPageSourceChoiceDropsMoment()
    {
        // Arrange
        await using var harness = new NetworkPageHarness("?source=agent&range=7d&at=2026-10-13T11:00");

        harness.Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, NetworkPageHarness.Now.AddMinutes(-5)), (Legacy, NetworkPageHarness.Now.AddHours(-3)));

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Act
        await harness.ChooseSourceAsync(Legacy);

        // Assert
        var expected = new NetworkAddress
                       {
                           Source = Legacy,
                           Preset = HistoryPreset.SevenDays
                       };

        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(NetworkView.NetworkHref(expected), harness.Navigation.Navigations[0].Uri, "the address has the new source and the range only");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// A read of the charts and a read of the panel that run past ten seconds are stopped with the text of the deadline.
    /// </summary>
    /// <param name="chart">Whether the read of the charts is held, else the read of the panel</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NetworkPageTimeoutShowsText(bool chart)
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new NetworkPageHarness("?source=agent");

        if (chart)
        {
            harness.Rollups.Hold = hold.Task;
        }
        else
        {
            harness.Reader.Hold = hold.Task;
        }

        var add = harness.AddAsync();

        if (chart)
        {
            await harness.WaitForRollupCallsAsync(1);
        }
        else
        {
            await harness.WaitForTextAsync(NetworkPageHarness.FirstAddress);
            await harness.WaitForReaderCallsAsync(1);
        }

        // Act
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        await harness.WaitForTextAsync(NetworkView.DeadlineText);

        // Assert
        var held = chart ? harness.Rollups.Calls[0].Token : harness.Reader.Calls[0].Token;

        Assert.IsTrue(held.IsCancellationRequested, "the held read was cancelled at the deadline");
        Assert.DoesNotContain(NetworkView.FailedText, harness.Text(), "the deadline is not a failure");

        if (chart == false)
        {
            Assert.Contains(NetworkView.DeadlineText, Section(harness, ListenersHeading, null), "the text replaces the panel");
        }

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A read of the table that runs past ten seconds shows the text of the deadline in place of the table and keeps the charts and the panel.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task NetworkPageAddressTimeoutKeepsCharts()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new NetworkPageHarness(string.Empty);

        harness.HoldAddresses(hold.Task);

        var add = harness.AddAsync();

        await harness.WaitForRollupCallsAsync(3);
        await harness.WaitForTextAsync("Listeners of ");
        await harness.WaitForDrawsAsync(2);

        // Act
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        await harness.WaitForAsync(() => Section(harness, AddressesHeading, ListenersHeading).Contains(NetworkView.DeadlineText, StringComparison.Ordinal));

        // Assert
        var text = harness.Text();

        Assert.IsTrue(harness.AddressReads[0].Token.IsCancellationRequested, "the held read of the addresses was cancelled at the deadline");
        Assert.HasCount(1, harness.AddressReads, "the table is read once");
        Assert.DoesNotContain(NetworkView.FailedText, text, "the deadline is not a failure");
        Assert.AreEqual(1, CountOf(text, NetworkView.DeadlineText), "one text of the deadline, in place of the table");
        Assert.AreSequenceEqual([StatesChart, ProgramsChart, AddressesHeading, ListenersHeading], harness.Headings().ToArray(), "the charts and the panel are kept");
        Assert.HasCount(2, harness.Drawn, "the charts are drawn");
        Assert.Contains(NetworkView.ProgramNoteText, text, "the note of the program chart is kept");
        Assert.Contains($"Listeners of {LogsView.ShowSeconds(NetworkPageHarness.Now.AddMinutes(-5))} UTC (agent)", text, "the panel is kept");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A failure of the store shows the fixed text without its details and is logged with its exception at level error, event 81, and without any value of the address,
    /// for each of the three reads.
    /// </summary>
    /// <param name="read">The failing read: <c>chart</c>, <c>address</c> or <c>panel</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow("chart")]
    [DataRow("address")]
    [DataRow("panel")]
    public async Task NetworkPageFailureLogsWithoutText(string read)
    {
        // Arrange
        var failure = new StoreException("secret detail of the store");

        await using var harness = new NetworkPageHarness("?source=secretname&at=2026-10-13T11:00");

        if (read == "chart")
        {
            harness.Rollups.Failure = failure;
        }
        else if (read == "address")
        {
            harness.FailAddresses(failure);
        }
        else
        {
            harness.Reader.Failure = failure;
        }

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.Logger.Exceptions.Contains(failure));
        await harness.WaitForTextAsync(NetworkView.FailedText);

        // Assert
        Assert.DoesNotContain("secret detail", harness.Html(), "no detail of a failure");
        Assert.Contains(FailureLog, harness.Logger.Messages, "the message of the log method");
        Assert.Contains(exception => ReferenceEquals(exception, failure), harness.Logger.Exceptions, "the exception is logged");
        Assert.Contains(entry => entry.EventId.Id == 81, harness.Logger.Entries, "event 81");
        Assert.IsTrue(harness.Logger.Entries.Where(entry => entry.EventId.Id == 81).All(entry => entry.Level == LogLevel.Error), "the entries of event 81 are errors");
        Assert.DoesNotContain(message => message.Contains("secretname", StringComparison.Ordinal) || message.Contains("2026", StringComparison.Ordinal), harness.Logger.Messages, "no value of the address in the log");
        Assert.Contains(NetworkView.FailedText, FailedSection(harness, read), "the text is shown in place of what failed");
    }

    /// <summary>
    /// Every external text of the tables and of the panel heading is shown as external text: escaped (control, format and direction characters), capped at 256 characters,
    /// without markup. The fields are the remote address, the protocol, the command and the user name of a listener, and the source of the panel.
    /// </summary>
    /// <param name="source">Whether the source of the panel is the hostile text, else the fields of the tables are</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NetworkPageShowsHostileTextEscaped(bool source)
    {
        // Arrange
        var now = NetworkPageHarness.Now;
        var hostileSource = Hostile("src");
        var hostile = new[] { Hostile("addr"), Hostile("proto"), Hostile("cmd"), Hostile("usr") };

        await using var harness = new NetworkPageHarness(string.Empty);

        if (source)
        {
            harness.Rollups.Sources = ProcessesSamples.SourceList(false, (hostileSource, now.AddMinutes(-5)));
            harness.AddConnections(NetworkSamples.Connections(2, hostileSource, now.AddMinutes(-1), "all", NetworkSamples.Tcp(22)));
        }
        else
        {
            harness.Rollups.Addresses = NetworkSamples.Addresses(now.AddHours(-2), NetworkSamples.Address(hostile[0], 2, 3));
            harness.AddConnections(NetworkSamples.Connections(2, Agent, now.AddMinutes(-1), "all", NetworkSamples.Listener(hostile[1], "0.0.0.0:22", 4242, hostile[2], 1000, hostile[3])));
        }

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);
        await harness.WaitForTextAsync("Listeners of ");

        // Assert
        var elements = harness.Renderer.Elements(NetworkPageHarness.RootId);

        Assert.IsTrue(NetworkSamples.AnyEscaped(LogText.Runs(hostile[0], HistoryView.TextChars, HistoryView.TextRuns)), "the hostile text needs escapes");
        Assert.DoesNotContain("<script>", harness.Html(), "no markup from external text");
        Assert.IsTrue(elements.All(element => element.Tag != "script"), "no script element");

        if (source)
        {
            var heading = elements.First(element => element.Text.StartsWith("Listeners of ", StringComparison.Ordinal));

            Assert.Contains($"UTC ({Expected(hostileSource)})", heading.Text, "the source is shown as the display of its text");
            Assert.Contains(descendant => descendant.Classes.Contains("escape"), heading.Descendants().ToArray(), "the source holds escapes");
            Assert.IsFalse(HoldsControl(heading.Text), "no direction or format character in the heading");

            return;
        }

        AssertCell(elements, Expected(hostile[0]), "the remote address");
        AssertCell(elements, Expected(hostile[1]), "the protocol");
        AssertCell(elements, "4242 " + Expected(hostile[2]), "the PID and the command");
        AssertCell(elements, "1000 " + Expected(hostile[3]), "the UID and the user name");
        Assert.IsFalse(HoldsControl(harness.Html()), "no direction or format character in the page");
    }

    /// <summary>
    /// A program name in a chart label is escaped and capped at 64 characters before it reaches the script; no direction character reaches the browser.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task NetworkPageEscapesProgramLabels()
    {
        // Arrange
        var evil = Hostile("prog");

        await using var harness = new NetworkPageHarness(string.Empty);

        harness.Rollups.Rollups = NetworkSamples.Rollups(NetworkPageHarness.Now.AddHours(-2), [NetworkSamples.Line("ESTABLISHED", 3, 4)], [NetworkSamples.Line(evil, 2, 3)]);

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(2);

        // Assert
        var labels = harness.Drawn.SelectMany(LabelsOf).ToArray();
        var expected = LogText.Escape(evil, HistoryView.LabelChars);

        Assert.AreSequenceEqual(new[] { "ESTABLISHED", expected, NetworkView.OtherPrograms }.Order(StringComparer.Ordinal).ToArray(), labels.Order(StringComparer.Ordinal).ToArray(), "the state, the escaped program and the other programs");
        Assert.DoesNotContain(HoldsControl, labels, "no direction or format character in a label");
        Assert.DoesNotContain(new string('x', 100), expected, "the label is capped");
    }

    /// <summary>
    /// Builds a hostile text: a prefix, a direction, a format and an isolate character, markup and more than 256 characters.
    /// </summary>
    /// <param name="prefix">The start of the text</param>
    /// <returns>The text</returns>
    private static string Hostile(string prefix)
    {
        return $"{prefix}{string.Concat(_controls)}{Markup}{new string('x', 300)}";
    }

    /// <summary>
    /// Tells whether a text holds one of the direction or format characters of the hostile texts.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text holds one</returns>
    private static bool HoldsControl(string text)
    {
        return _controls.Any(control => text.Contains(control, StringComparison.Ordinal));
    }

    /// <summary>
    /// Gives the text of a cell for an external text: the runs of its display, joined.
    /// </summary>
    /// <param name="text">The external text</param>
    /// <returns>The text of the cell</returns>
    private static string Expected(string text)
    {
        return string.Concat(LogText.Runs(text, HistoryView.TextChars, HistoryView.TextRuns).Select(run => run.Text));
    }

    /// <summary>
    /// Checks that a cell of a table has a text and holds an escape.
    /// </summary>
    /// <param name="elements">The elements of the page</param>
    /// <param name="text">The text of the cell</param>
    /// <param name="what">What the cell shows</param>
    private static void AssertCell(IReadOnlyList<RenderedElement> elements, string text, string what)
    {
        var cell = elements.FirstOrDefault(element => element.Tag == "td" && element.Text == text);

        Assert.IsNotNull(cell, $"{what}: a cell shows the display of the text");
        Assert.Contains(descendant => descendant.Classes.Contains("escape"), cell.Descendants().ToArray(), $"{what}: the cell holds escapes");
    }

    /// <summary>
    /// Gets the text of the part of the page whose read failed.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="read">The failed read: <c>chart</c>, <c>address</c> or <c>panel</c></param>
    /// <returns>The text of the page, of the table section or of the panel section</returns>
    private static string FailedSection(NetworkPageHarness harness, string read)
    {
        return read switch
               {
                   "address" => Section(harness, AddressesHeading, ListenersHeading),
                   "panel" => Section(harness, ListenersHeading, null),
                   _ => harness.Text()
               };
    }

    /// <summary>
    /// Gets the text of the page from a heading up to the next one.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="heading">The heading of the section</param>
    /// <param name="next">The heading of the next section, or <see langword="null"/> for the rest of the page</param>
    /// <returns>The text, empty when the page has no such heading</returns>
    private static string Section(NetworkPageHarness harness, string heading, string? next)
    {
        var text = harness.Text();
        var start = text.IndexOf(heading, StringComparison.Ordinal);

        if (start < 0)
        {
            return string.Empty;
        }

        var end = next is null ? -1 : text.IndexOf(next, start + heading.Length, StringComparison.Ordinal);

        return end < 0 ? text[start..] : text[start..end];
    }

    /// <summary>
    /// Gets the rows of the table with a caption, each as its cells joined with a bar.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="caption">The caption</param>
    /// <returns>The rows; none when the page has no such table</returns>
    private static IReadOnlyList<string> TableRows(NetworkPageHarness harness, string caption)
    {
        var table = harness.Renderer.Elements(NetworkPageHarness.RootId).FirstOrDefault(element => element.Tag == "table" && element.Descendants().Any(descendant => descendant.Tag == "caption" && descendant.Text == caption));

        if (table is null)
        {
            return [];
        }

        return [.. table.Descendants().Where(row => row.Tag == "tr" && row.Descendants().Any(cell => cell.Tag == "td")).Select(row => string.Join('|', row.Descendants().Where(cell => cell.Tag == "td").Select(cell => cell.Text)))];
    }

    /// <summary>
    /// Counts the occurrences of a text.
    /// </summary>
    /// <param name="text">The text to search</param>
    /// <param name="part">The text to count</param>
    /// <returns>The number of occurrences</returns>
    private static int CountOf(string text, string part)
    {
        var count = 0;

        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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

    #endregion // Methods
}