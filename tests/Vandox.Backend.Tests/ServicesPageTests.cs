using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ServicesPage"/>. The tests read the page the way a browser shows it: the presets, <c>Apply</c> and the range fields are found as on the network page, the
/// sections are found by their <c>h2</c> headings (<c>Services</c>, <c>MariaDB</c>, <c>Long-running queries</c>), the five charts are the components with the class <c>history-chart</c>
/// (availability, memory budget, connections, queries per second, buffer pool), the tables by their captions, a cell is a <c>td</c>, the time of a snapshot in the list is a button, and
/// a hostile text is shown in an element with the class <c>escape</c>. A click or a drag in a chart is the callback the browser reports to the events object of the chart.
/// </summary>
[TestClass]
public class ServicesPageTests
{
    #region Constants

    private const string FailureLog = "services view: reading failed";

    private const string Markup = "<script>alert(1)</script>";

    private const string ServicesTitle = "Services";

    private const string MariaDbTitle = "MariaDB";

    private const string ThreadsTitle = "Long-running queries";

    private const string ServicesSection = "services";

    private const string MariaDbSection = "mariadb";

    private const string ThreadsSection = "threads";

    #endregion // Constants

    #region Fields

    private static readonly string[] _controls = [char.ConvertFromUtf32(0x202e), char.ConvertFromUtf32(0x200b), char.ConvertFromUtf32(0x2066)];

    private static readonly string[] _chartNames = ["Availability", "Memory budget", "Connections", "Queries per second", "InnoDB buffer pool"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A page that is not rendered interactively, as while prerendering, shows the heading, the range controls and <c>Loading…</c>, and reads nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ServicesPagePrerendersHeadingControlsAndLoading()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty, false);

        // Act
        await harness.AddAsync();

        // Assert
        var heading = harness.Renderer.Elements(ServicesPageHarness.RootId).Single(element => element.Tag == "h1");

        Assert.AreEqual("Services and MariaDB", heading.Text.Trim(), "the heading of the page");
        Assert.HasCount(1, harness.Buttons("30 d"), "the presets are shown");
        Assert.HasCount(1, harness.Buttons("Apply"), "the apply button is shown");
        Assert.AreEqual("Loading…", ServicesView.LoadingText, "the text of the constant");
        Assert.Contains("Loading…", harness.Text(), "the loading text");
        Assert.AreEqual(0, harness.StoreCalls, "no call of any reader");
        Assert.IsEmpty(harness.Runtime.Calls, "no call into the browser");
        Assert.IsEmpty(harness.Drawn, "nothing is drawn");
    }

    /// <summary>
    /// A page without a range marks <c>30 d</c>, one with a preset marks that preset, and one with a fixed range marks none.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <param name="marked">The text of the marked preset, or an empty text for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("", "30 d")]
    [DataRow("?at=2026-10-13T11:00", "30 d")]
    [DataRow("?range=7d", "7 d")]
    [DataRow("?range=24h", "24 h")]
    [DataRow("?range=1y", "1 y")]
    [DataRow("?from=2026-10-01T00:00&to=2026-10-02T00:00", "")]
    public async Task ServicesPageMarksThirtyDaysByDefault(string query, string marked)
    {
        // Arrange
        await using var harness = new ServicesPageHarness(query, false);

        // Act
        await harness.AddAsync();

        // Assert
        var active = harness.Renderer.Elements(ServicesPageHarness.RootId).Where(element => element.Tag == "button" && element.Classes.Contains("active")).Select(element => element.Text.Trim()).ToArray();

        string[] expected = marked.Length == 0 ? [] : [marked];

        Assert.AreSequenceEqual(expected, active, "the marked preset");
    }

    /// <summary>
    /// An address with an error shows the heading, the controls and the text of the error, and reads nothing.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <param name="text">The text of the error</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("?range=2h", "The address has an invalid value for the parameter range.")]
    [DataRow("?range=1h&range=7d", "The address gives the parameter range more than once.")]
    [DataRow("?from=2026-10-13T08:00", "The address needs both a start and an end time.")]
    [DataRow("?range=7d&from=2026-10-13T08:00&to=2026-10-13T09:00", "The address gives both a preset range and a start or end time.")]
    [DataRow("?from=2026-10-13T09:00&to=2026-10-13T08:00", "The address has a start time that is not before its end time.")]
    public async Task ServicesPageShowsAddressErrorAndReadsNothing(string query, string text)
    {
        // Arrange
        await using var harness = new ServicesPageHarness(query);

        // Act
        await harness.AddAsync();

        // Assert
        Assert.Contains(text, harness.Text(), "the error of the address");
        Assert.Contains("Services and MariaDB", harness.Text(), "the heading");
        Assert.HasCount(1, harness.Buttons("7 d"), "the presets are shown");
        Assert.IsEmpty(harness.WithClass("history-chart"), "no chart");
        Assert.IsEmpty(harness.Headings(), "no section");
        Assert.AreEqual(0, harness.StoreCalls, "no call of any reader");
        Assert.IsEmpty(harness.Runtime.Calls, "no call into the browser");
    }

    /// <summary>
    /// A range that begins after now says so and reads nothing, with or without a moment.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("?from=2026-10-14T00:00&to=2026-10-15T00:00")]
    [DataRow("?from=2026-10-14T00:00&to=2026-10-15T00:00&at=2026-10-14T01:00")]
    public async Task ServicesPageShowsFutureRangeAndReadsNothing(string query)
    {
        // Arrange
        await using var harness = new ServicesPageHarness(query);

        // Act
        await harness.AddAsync();

        // Assert
        Assert.Contains("This range lies in the future.", harness.Text(), "the text of a future range");
        Assert.AreEqual(0, harness.StoreCalls, "no call of any reader");
        Assert.IsEmpty(harness.Drawn, "nothing is drawn");
    }

    /// <summary>
    /// A loaded page has the three sections in order with their subsections, the five charts named by their headings, the table of the services with one row, the list of the restarts, the
    /// summary and the budget of MariaDB, the note of the agent, the list of the snapshots and the panel of the latest one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [TestCategory("AC2")]
    [TestCategory("AC8")]
    public async Task ServicesPageShowsSectionsInOrder()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);

        // Act
        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Assert
        var restart = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var rows = harness.TableRows(ServicesTitle);

        Assert.AreSequenceEqual([ServicesTitle, MariaDbTitle, ThreadsTitle], harness.Headings().ToArray(), "the three sections");
        Assert.AreSequenceEqual(["Service restarts", .. _chartNames, "Snapshots", "Threads of 2026-10-13 07:00:00 UTC"], harness.SubHeadings().ToArray(), "the subsections");
        Assert.AreSequenceEqual(_chartNames, harness.WithClass("history-chart").Select(host => host.Attributes["aria-label"]).ToArray(), "the names of the charts are their headings");
        Assert.HasCount(1, rows, "one unit");
        Assert.StartsWith($"{ServicesSamples.Nginx}|99.7 %|2 h 0 min|0 s|1|{LogsView.ShowSeconds(restart)}|", rows[0], "the cells of the unit");
        Assert.AreSequenceEqual([$"{LogsView.ShowSeconds(restart)}|{ServicesSamples.Nginx}"], harness.TableRows("Service restarts").ToArray(), "the list of the restarts");
        Assert.Contains("Up 100.0 %, down 0.0 %, not answering 0.0 %, unknown 0.0 % of this range.", harness.Section(MariaDbTitle, ThreadsTitle), "the summary line");
        Assert.Contains("In use 1.2 GiB of a theoretical maximum of unknown; RAM unknown.", harness.Section(MariaDbTitle, ThreadsTitle), "the budget line");
        Assert.Contains(ServicesView.AgentNoteText, harness.Section(ThreadsTitle, null), "the note of the agent");
        Assert.AreSequenceEqual(["2026-10-13 07:00:00|40|1|2 min"], harness.TableRows("Snapshots").ToArray(), "the list of the snapshots");
        Assert.Contains("SELECT sleep(125)", harness.Section(ThreadsTitle, null), "the statement of the thread");
        Assert.Contains("39 sleeping connections recorded, not listed.", harness.Section(ThreadsTitle, null), "the sleeping connections");
        Assert.HasCount(1, harness.Services.Calls, "the services are read once");
        Assert.HasCount(1, harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.MariaDbStatusAsync)), "the status is read once");
    }

    /// <summary>
    /// Without any stored data each section says so in place of its content: no service state, no MariaDB data, no thread snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC5")]
    [TestCategory("AC8")]
    public async Task ServicesPageShowsTheTextsOfMissingData()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);

        harness.Services.Result = new ServiceAvailabilityResult();
        harness.MariaDb.AnswerStatus = query => ServicesSamples.StatusAnswer(query, null, 0);
        harness.MariaDb.Stamps.Clear();

        // Act
        await harness.AddAsync();
        await harness.WaitForTextAsync(ServicesView.NoServiceStateText);
        await harness.WaitForTextAsync(ServicesView.NoMariaDbDataText);
        await harness.WaitForTextAsync(ServicesView.NoSnapshotInRangeText);

        // Assert
        Assert.Contains(ServicesView.NoServiceStateText, harness.Section(ServicesTitle, MariaDbTitle), "no service state in its section");
        Assert.Contains(ServicesView.NoMariaDbDataText, harness.Section(MariaDbTitle, ThreadsTitle), "no MariaDB data in its section");
        Assert.Contains(ServicesView.NoSnapshotInRangeText, harness.Section(ThreadsTitle, null), "no thread snapshot in its section");
        Assert.IsEmpty(harness.Drawn, "no chart is drawn");
        Assert.IsEmpty(harness.TableRows(ServicesTitle), "no table of the services");
    }

    /// <summary>
    /// The notes of the reads appear in their sections: the gaps of the systemd collector with the services, the gaps not read of the status with MariaDB.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public async Task ServicesPageShowsTheNotesInTheirSections()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);
        var gaps = ServicesPageHarness.Now.AddDays(-10);

        harness.Services.Result = new ServiceAvailabilityResult
                                  {
                                      Units = [ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(20), TimeSpan.Zero, TimeSpan.FromDays(10))],
                                      UnitCount = 1,
                                      SystemdGaps = true
                                  };
        harness.MariaDb.AnswerStatus = query => ServicesSamples.StatusAnswer(query, gapsCompleteUntil: gaps);

        // Act
        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Assert
        Assert.Contains(ServicesView.SystemdGapsText, harness.Section(ServicesTitle, MariaDbTitle), "the gaps of the systemd collector with the services");
        Assert.Contains($"Gaps after {LogsView.ShowSeconds(gaps)} UTC are not read; the time after it counts as unknown.", harness.Section(MariaDbTitle, ThreadsTitle), "the gaps not read with MariaDB");
        Assert.DoesNotContain(ServicesView.SystemdGapsText, harness.Section(MariaDbTitle, null), "the gaps of the systemd collector are no MariaDB note");
    }

    /// <summary>
    /// A drag selection in any chart writes its range as a fixed range in a new entry of the history and keeps the moment.
    /// </summary>
    /// <param name="chart">The place of the dragged chart in the order of the draws</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow(0)]
    [DataRow(4)]
    public async Task ServicesPageSelectionWritesRange(int chart)
    {
        // Arrange
        var from = ServicesPageHarness.Now.AddHours(-6);
        var to = ServicesPageHarness.Now.AddHours(-4);
        var expected = Href(null, from, to, ServicesPageHarness.Now.AddHours(-1));

        await using var harness = new ServicesPageHarness("?at=2026-10-13T11:00");

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        var events = harness.Events(chart);

        // Act
        await harness.OnDispatcherAsync(() => events.Select(from.ToUnixTimeSeconds() + 0.25, to.ToUnixTimeSeconds() - 0.25));

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(expected, harness.Navigation.Navigations[0].Uri, "the address has the range and the moment");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// A click in a chart writes the moment, floored to the second, replaces the entry of the history, reads neither the services nor the charts again but only the threads, and the
    /// charts mark the moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ServicesPageClickWritesAtAndReadsOnlyThreads()
    {
        // Arrange
        var at = ServicesPageHarness.Now.AddHours(-2);

        await using var harness = new ServicesPageHarness(string.Empty);

        harness.MariaDb.Closest = ServicesSamples.Stamp(ServicesPageHarness.SnapshotId, ServicesPageHarness.SnapshotAt, 40, 1, 125);

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        var seriesCalls = harness.Series.Calls.Count;
        var events = harness.Events(0);

        // Act
        await harness.OnDispatcherAsync(() => events.Pick(at.ToUnixTimeSeconds() + 0.7, 8));
        await harness.WaitForMariaDbCallAsync(nameof(IMariaDbHistoryReader.ClosestThreadSnapshotAsync));
        await harness.WaitForAsync(() => harness.Marked.Count >= 1);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href(null, null, null, at), harness.Navigation.Navigations[0].Uri, "the address has the moment, floored to the second");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.HasCount(1, harness.Services.Calls, "the services are not read again");
        Assert.HasCount(1, harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.MariaDbStatusAsync)), "the status is not read again");
        Assert.HasCount(seriesCalls, harness.Series.Calls, "the series are not read again");
        Assert.HasCount(1, harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.ClosestThreadSnapshotAsync)), "the threads are read for the moment");
        Assert.HasCount(ServicesPageHarness.ChartCount, harness.Drawn, "the charts are not drawn again");
        Assert.IsTrue(harness.Marked.All(call => call.Arguments[0] is double seconds && (long)Math.Round(seconds) == at.ToUnixTimeSeconds()), "the charts mark the moment");
    }

    /// <summary>
    /// The time of a snapshot in the list writes it as the moment, replacing the entry of the history.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ServicesPageSnapshotButtonWritesAt()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Act
        await harness.PressAsync("2026-10-13 07:00:00");

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href(null, null, null, ServicesPageHarness.SnapshotAt), harness.Navigation.Navigations[0].Uri, "the address has the capture time of the snapshot");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
    }

    /// <summary>
    /// A preset writes the new range as a new entry of the history and keeps the moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ServicesPagePresetWritesANewEntry()
    {
        // Arrange
        await using var harness = new ServicesPageHarness("?at=2026-10-13T11:00");

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Act
        await harness.PressAsync("7 d");

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href(HistoryPreset.SevenDays, null, null, ServicesPageHarness.Now.AddHours(-1)), harness.Navigation.Navigations[0].Uri, "the address has the preset and the moment");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// A change of the range reads the services, the charts and the threads again for the new range; the page marks the new preset.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ServicesPageChangeOfRangeReadsAllThreeActions()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Act
        await harness.GoToAsync("?range=7d");
        await harness.WaitForAsync(() => harness.Services.Calls.Count >= 2 && harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.MariaDbStatusAsync)).Count >= 2 && harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync)).Count >= 2);

        // Assert
        var query = harness.Services.Queries[1];
        var status = (MariaDbStatusQuery)harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.MariaDbStatusAsync))[1].Argument!;
        var stamps = (ThreadSnapshotQuery)harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync))[1].Argument!;
        var expected = ServicesPageHarness.Now.AddDays(-7);

        Assert.AreEqual(expected, query.From, "the services for the new range");
        Assert.AreEqual(expected, status.From, "the status for the new range");
        Assert.AreEqual(expected, stamps.From, "the snapshots for the new range");
        Assert.AreSequenceEqual(["7 d"], harness.Renderer.Elements(ServicesPageHarness.RootId).Where(element => element.Tag == "button" && element.Classes.Contains("active")).Select(element => element.Text.Trim()).ToArray(), "the new preset is marked");
    }

    /// <summary>
    /// A read that runs past ten seconds is stopped with the text of its deadline, and a read that fails shows its fixed text without details, each in the section of the read only: the other
    /// sections show their content.
    /// </summary>
    /// <param name="section">The section whose read is held or fails: <c>services</c>, <c>mariadb</c> or <c>threads</c></param>
    /// <param name="timeout">Whether the read is held until the deadline, else it fails</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(ServicesSection, true)]
    [DataRow(ServicesSection, false)]
    [DataRow(MariaDbSection, true)]
    [DataRow(MariaDbSection, false)]
    [DataRow(ThreadsSection, true)]
    [DataRow(ThreadsSection, false)]
    public async Task ServicesPageSectionsFailAlone(string section, bool timeout)
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new StoreException("secret detail of the store");

        await using var harness = new ServicesPageHarness(string.Empty);

        Break(harness, section, timeout ? hold.Task : null, timeout ? null : failure);

        var add = harness.AddAsync();

        await WaitForOthersAsync(harness, section);

        if (timeout)
        {
            await WaitForEntryAsync(harness, section);

            // Act
            harness.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        var expected = timeout ? DeadlineText(section) : FailedText(section);

        await harness.WaitForTextAsync(expected);

        // Assert
        var all = new[] { ServicesView.ServicesDeadlineText, ServicesView.MariaDbDeadlineText, ServicesView.ThreadsDeadlineText, ServicesView.ServicesFailedText, ServicesView.MariaDbFailedText, ServicesView.ThreadsFailedText };
        var text = harness.Text();

        Assert.Contains(expected, SectionText(harness, section), "the text is in the section of the read");
        Assert.ContainsSingle(other => text.Contains(other, StringComparison.Ordinal), all, "no other section shows a deadline or a failure");
        Assert.DoesNotContain("secret detail", harness.Html(), "no detail of a failure");

        if (section != ServicesSection)
        {
            Assert.HasCount(1, harness.TableRows(ServicesTitle), "the services are shown");
        }

        if (section != ThreadsSection)
        {
            Assert.Contains("Threads of ", text, "the thread panel is shown");
        }

        if (section != MariaDbSection)
        {
            Assert.HasCount(ServicesPageHarness.ChartCount, harness.Drawn, "the charts are drawn");
        }

        if (timeout)
        {
            Assert.IsTrue(HeldToken(harness, section).IsCancellationRequested, "the held read was cancelled at the deadline");
        }

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A failure of the store is logged with its exception at level error, event 82, with the fixed message and without any value of the address, for each section; an invalid query
    /// shows the same text as a store failure.
    /// </summary>
    /// <param name="section">The section whose read fails: <c>services</c>, <c>mariadb</c> or <c>threads</c></param>
    /// <param name="invalid">Whether the failure is an invalid query, else a store failure</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(ServicesSection, false)]
    [DataRow(MariaDbSection, false)]
    [DataRow(ThreadsSection, false)]
    [DataRow(ServicesSection, true)]
    [DataRow(MariaDbSection, true)]
    [DataRow(ThreadsSection, true)]
    public async Task ServicesPageLogsOnlyException(string section, bool invalid)
    {
        // Arrange
        StoreException failure = invalid ? new InvalidQueryException("secret detail of the query") : new StoreException("secret detail of the store");

        await using var harness = new ServicesPageHarness("?at=2026-10-13T11:00");

        Break(harness, section, null, failure);

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.Logger.Exceptions.Contains(failure));
        await harness.WaitForTextAsync(FailedText(section));

        // Assert
        Assert.DoesNotContain("secret detail", harness.Html(), "no detail of a failure");
        Assert.Contains(FailureLog, harness.Logger.Messages, "the message of the log method");
        Assert.Contains(exception => ReferenceEquals(exception, failure), harness.Logger.Exceptions, "the exception is logged");
        Assert.Contains(entry => entry.EventId.Id == 82, harness.Logger.Entries, "event 82");
        Assert.IsTrue(harness.Logger.Entries.Where(entry => entry.EventId.Id == 82).All(entry => entry.Level == LogLevel.Error), "the entries of event 82 are errors");
        Assert.DoesNotContain(message => message.Contains("secret", StringComparison.Ordinal) || message.Contains("2026", StringComparison.Ordinal) || message.Contains("nginx", StringComparison.Ordinal), harness.Logger.Messages, "no value of the address or the data in the log");
        Assert.Contains(FailedText(section), SectionText(harness, section), "the text is shown in place of what failed");
    }

    /// <summary>
    /// Leaving the page cancels the read of the services, the read of MariaDB's data and the read of the threads.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ServicesPageLeavingCancelsActions()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new ServicesPageHarness(string.Empty);

        Break(harness, ServicesSection, hold.Task, null);
        Break(harness, MariaDbSection, hold.Task, null);
        Break(harness, ThreadsSection, hold.Task, null);

        var add = harness.AddAsync();

        await WaitForEntryAsync(harness, ServicesSection);
        await WaitForEntryAsync(harness, MariaDbSection);
        await WaitForEntryAsync(harness, ThreadsSection);

        var tokens = new[] { HeldToken(harness, ServicesSection), HeldToken(harness, MariaDbSection), HeldToken(harness, ThreadsSection) };

        // Act
        await harness.DisposePageAsync();

        // Assert
        Assert.IsTrue(tokens.All(token => token.IsCancellationRequested), "every read was cancelled");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A unit name is external text: it is shown in the table and in the list of the restarts as the display of its text, escaped, capped and without markup.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task ServicesPageShowsHostileUnitEscaped()
    {
        // Arrange
        var evil = $"evil{string.Concat(_controls)}{Markup}{new string('x', 300)}.service";

        await using var harness = new ServicesPageHarness(string.Empty);

        harness.Services.Result = ServicesSamples.Availability(ServicesSamples.Unit(evil, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));
        harness.Series.Services.Clear();
        harness.Series.Services.Add(ServicesSamples.RestartRecord(ServicesPageHarness.Now.AddDays(-3), evil, 2));

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.Text().Contains("evil", StringComparison.Ordinal));

        // Assert
        var elements = harness.Renderer.Elements(ServicesPageHarness.RootId);
        var expected = string.Concat(LogText.Runs(evil, HistoryView.TextChars, HistoryView.TextRuns).Select(run => run.Text));
        var cells = elements.Where(element => element.Tag == "td" && element.Text == expected).ToArray();

        Assert.IsGreaterThanOrEqualTo(2, cells.Length, "the name is shown in the table and in the list of the restarts");
        Assert.IsTrue(cells.All(cell => cell.Descendants().Any(descendant => descendant.Classes.Contains("escape"))), "the cells hold escapes");
        Assert.DoesNotContain("<script>", harness.Html(), "no markup from external text");
        Assert.IsTrue(elements.All(element => element.Tag != "script"), "no script element");
        Assert.DoesNotContain(control => harness.Html().Contains(control, StringComparison.Ordinal), _controls, "no direction or format character in the page");
    }

    /// <summary>
    /// <c>Apply</c> writes the range of the fields as a fixed range in a new entry of the history; a field that is not a time shows the text of the history view and writes nothing.
    /// </summary>
    /// <param name="from">The text of the start field</param>
    /// <param name="to">The text of the end field</param>
    /// <param name="error">The expected error text, empty for a valid range</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("2026-10-13T08:00", "2026-10-13T09:30", "")]
    [DataRow("x", "2026-10-13T09:30", "The address has an invalid value for the parameter from.")]
    [DataRow("2026-10-13T08:00", "", "The address needs both a start and an end time.")]
    public async Task ServicesPageApplyWritesFieldsOrShowsError(string from, string to, string error)
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);

        await harness.AddAsync();
        await harness.WaitForLoadedAsync();
        await harness.EnterAsync(("from", from), ("to", to));

        // Act
        await harness.PressAsync("Apply");

        // Assert
        if (error.Length == 0)
        {
            Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
            Assert.AreEqual(Href(null, new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero), null), harness.Navigation.Navigations[0].Uri, "the address has the fixed range");
            Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
        }
        else
        {
            Assert.IsEmpty(harness.Navigation.Navigations, "nothing is written");
            Assert.Contains(error, harness.Text(), "the text of the error");
        }
    }

    /// <summary>
    /// The list of the restarts and its heading appear only when a restart was found.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task ServicesPageShowsRestartListOnlyWithRestarts()
    {
        // Arrange
        await using var harness = new ServicesPageHarness(string.Empty);

        harness.Series.Services.Clear();

        // Act
        await harness.AddAsync();
        await harness.WaitForLoadedAsync();

        // Assert
        Assert.DoesNotContain("Service restarts", harness.SubHeadings().ToArray(), "no restart, no list");
    }

    /// <summary>
    /// Writes the relative address the page writes for a state, as the page does.
    /// </summary>
    /// <param name="preset">The preset, or <see langword="null"/> for none</param>
    /// <param name="from">The start of a fixed range, or <see langword="null"/></param>
    /// <param name="to">The end of a fixed range, or <see langword="null"/></param>
    /// <param name="at">The moment, or <see langword="null"/></param>
    /// <returns>The relative address</returns>
    private static string Href(HistoryPreset? preset, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? at)
    {
        var address = new ServicesAddress();

        address.Times.Preset = preset;
        address.Times.From = from;
        address.Times.To = to;
        address.Times.At = at;

        return "services" + address.ToQuery();
    }

    /// <summary>
    /// Makes the reads of a section wait for a task or fail.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="section">The section</param>
    /// <param name="hold">The task to wait for, or <see langword="null"/> for none</param>
    /// <param name="failure">The exception to throw, or <see langword="null"/> for none</param>
    private static void Break(ServicesPageHarness harness, string section, Task? hold, Exception? failure)
    {
        switch (section)
        {
            case ServicesSection:
                {
                    harness.Services.Hold = hold;
                    harness.Services.Failure = failure;
                }
                break;

            default:
                {
                    var member = section == MariaDbSection ? nameof(IMariaDbHistoryReader.MariaDbStatusAsync) : nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync);

                    if (hold is not null)
                    {
                        harness.MariaDb.HoldFor(member, hold);
                    }

                    if (failure is not null)
                    {
                        harness.MariaDb.FailFor(member, failure);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Waits until the other sections than the broken one show their content.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="section">The broken section</param>
    /// <returns>A task that completes when the other sections are loaded</returns>
    private static async Task WaitForOthersAsync(ServicesPageHarness harness, string section)
    {
        if (section != ServicesSection)
        {
            await harness.WaitForTextAsync(ServicesSamples.Nginx);
        }

        if (section != MariaDbSection)
        {
            await harness.WaitForDrawsAsync(ServicesPageHarness.ChartCount);
        }

        if (section != ThreadsSection)
        {
            await harness.WaitForTextAsync(ServicesView.SnapshotsHeading);
        }
    }

    /// <summary>
    /// Waits until the read of a section reached the store.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="section">The section</param>
    /// <returns>A task that completes when the read has entered</returns>
    private static Task WaitForEntryAsync(ServicesPageHarness harness, string section)
    {
        return section switch
               {
                   ServicesSection => harness.WaitForAsync(() => harness.Services.Calls.Count > 0),
                   MariaDbSection => harness.WaitForMariaDbCallAsync(nameof(IMariaDbHistoryReader.MariaDbStatusAsync)),
                   _ => harness.WaitForMariaDbCallAsync(nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync))
               };
    }

    /// <summary>
    /// Gets the token of the first read of a section that reached the store.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="section">The section</param>
    /// <returns>The token</returns>
    private static CancellationToken HeldToken(ServicesPageHarness harness, string section)
    {
        return section switch
               {
                   ServicesSection => harness.Services.Calls[0].Token,
                   MariaDbSection => harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.MariaDbStatusAsync))[0].Token,
                   _ => harness.MariaDb.CallsOf(nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync))[0].Token
               };
    }

    /// <summary>
    /// Gets the text of the section of the page.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <param name="section">The section</param>
    /// <returns>The text</returns>
    private static string SectionText(ServicesPageHarness harness, string section)
    {
        return section switch
               {
                   ServicesSection => harness.Section(ServicesTitle, MariaDbTitle),
                   MariaDbSection => harness.Section(MariaDbTitle, ThreadsTitle),
                   _ => harness.Section(ThreadsTitle, null)
               };
    }

    /// <summary>
    /// Gets the text of the deadline of a section.
    /// </summary>
    /// <param name="section">The section</param>
    /// <returns>The text</returns>
    private static string DeadlineText(string section)
    {
        return section switch
               {
                   ServicesSection => ServicesView.ServicesDeadlineText,
                   MariaDbSection => ServicesView.MariaDbDeadlineText,
                   _ => ServicesView.ThreadsDeadlineText
               };
    }

    /// <summary>
    /// Gets the text of the failure of a section.
    /// </summary>
    /// <param name="section">The section</param>
    /// <returns>The text</returns>
    private static string FailedText(string section)
    {
        return section switch
               {
                   ServicesSection => ServicesView.ServicesFailedText,
                   MariaDbSection => ServicesView.MariaDbFailedText,
                   _ => ServicesView.ThreadsFailedText
               };
    }

    #endregion // Methods
}