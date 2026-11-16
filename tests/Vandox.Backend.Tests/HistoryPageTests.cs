using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.History;
using Vandox.Backend.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryPage"/>. The tests read the page the way a browser shows it: the presets, <c>Apply</c>, <c>Add</c> and <c>Remove</c> are buttons found
/// by their text, the range fields are inputs named <c>from</c> and <c>to</c>, the metric choice is a select named <c>metric</c> whose option values are the indexes
/// into the offered list, the current preset has the class <c>active</c>, the chart is the component with the class <c>history-chart</c>, the time of an event is
/// a button with the text <c>yyyy-MM-dd HH:mm:ss</c>, the logs link of a moment is a link with the text <c>Logs of this moment</c>, and the snapshot before a moment
/// is offered by a button whose text starts with <c>Snapshot before this moment</c>. A button that has no click handler submits the form that holds it.
/// </summary>
[TestClass]
public partial class HistoryPageTests
{
    #region Constants

    private const string OneMetric = "?metric=system,cpu_percent";

    private const string TwoMetrics = "?metric=system,mem_available_bytes&metric=system,cpu_percent";

    private const string FailureLog = "history view: reading failed";

    private const string ProcessKind = "process_snapshot";

    private const string ConnectionKind = "connection_snapshot";

    private const string SnapshotBefore = "Snapshot before this moment";

    private const string LogsOfMoment = "Logs of this moment";

    private const string MomentHeading = "Moment 2026-10-13 11:00:00 UTC";

    private const string MetricField = "metric";

    private const string KillText = "mysqld (PID 42)";

    #endregion // Constants

    #region Fields

    private static readonly string[] _presets = ["1 h", "24 h", "7 d", "30 d", "1 y"];

    private static readonly SeriesKey _cpu = new("system", "cpu_percent");

    private static readonly SeriesKey _memory = new("system", "mem_available_bytes");

    private static readonly DateTimeOffset _killAt = new(2026, 10, 13, 11, 30, 15, TimeSpan.Zero);

    private static readonly DateTimeOffset _momentAt = new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _laterMomentAt = new(2026, 10, 13, 11, 10, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An address with an error shows the text of the error and the controls, draws no chart and calls no store method.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <param name="text">The text of the error</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("?range=2h", "The address has an invalid value for the parameter range.")]
    [DataRow("?metric=system", "The address has an invalid value for the parameter metric.")]
    [DataRow("?metric=system,cpu_percent&from=2026-10-13T10:00", "The address needs both a start and an end time.")]
    [DataRow("?range=24h&range=7d", "The address gives the parameter range more than once.")]
    public async Task HistoryPageWithAddressErrorCallsNoStore(string query, string text)
    {
        // Arrange
        await using var harness = new HistoryPageHarness(query);

        // Act
        await harness.AddAsync();

        // Assert
        Assert.Contains(Enc(text), harness.Html(), "the error of the address");
        Assert.AreEqual(5, _presets.Count(label => harness.Buttons(label).Count == 1), "the five presets are shown");
        Assert.IsEmpty(harness.Reader.Calls, "no call of the history reader, the metric list included");
        Assert.IsEmpty(harness.Series.Calls, "no call of the time-series reader");
        Assert.IsEmpty(harness.WithClass("history-chart"), "no chart");
        Assert.IsEmpty(harness.Runtime.Calls, "no call into the browser");
    }

    /// <summary>
    /// An address that is read again by another user shows the same moment and reads the same snapshots, whole seconds in every query.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task HistoryPageReloadShowsTheSameSnapshots()
    {
        // Arrange
        const string query = OneMetric + "&at=2026-10-13T11:00:30";

        await using var first = new HistoryPageHarness(query);
        await using var second = new HistoryPageHarness(query);

        foreach (var harness in new[] { first, second })
        {
            harness.Reader.Stamps.Add(HistorySamples.Stamp(1, ProcessKind, _momentAt.AddSeconds(29).AddMilliseconds(500)));
            harness.Reader.Stamps.Add(HistorySamples.Stamp(2, ProcessKind, _momentAt.AddSeconds(31).AddMilliseconds(200)));
            harness.Reader.Records[1] = HistorySamples.ProcessRecord(1, "web-1", _momentAt.AddSeconds(29).AddMilliseconds(500));
            harness.Reader.Records[2] = HistorySamples.ProcessRecord(2, "web-2", _momentAt.AddSeconds(31).AddMilliseconds(200));
        }

        // Act
        await first.AddAsync();
        await second.AddAsync();
        await first.WaitForHtmlAsync(html => html.Contains("web-1", StringComparison.Ordinal));
        await second.WaitForHtmlAsync(html => html.Contains("web-1", StringComparison.Ordinal));

        // Assert
        var queries = first.StampReads.Select(call => (SnapshotQuery)call.Argument!).ToArray();

        Assert.AreSequenceEqual(MomentCalls(first), MomentCalls(second), "the same calls in the same order");
        Assert.AreEqual(Collapsed(first.Text()), Collapsed(second.Text()), "the same page");
        Assert.IsNotEmpty(queries, "the snapshots were read");
        Assert.IsTrue(queries.All(snapshot => snapshot.At == _momentAt.AddSeconds(30)), "every query is made at the moment of the address, a whole second");
        Assert.Contains("Moment 2026-10-13 11:00:30 UTC", Collapsed(first.Text()), "the moment of the address");
    }

    /// <summary>
    /// A preset replaces the range of the address, with the metrics kept, as a new history entry.
    /// </summary>
    /// <param name="label">The text of the preset</param>
    /// <param name="preset">The preset</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow("1 h", HistoryPreset.OneHour)]
    [DataRow("7 d", HistoryPreset.SevenDays)]
    [DataRow("30 d", HistoryPreset.ThirtyDays)]
    [DataRow("1 y", HistoryPreset.OneYear)]
    public async Task HistoryPagePresetWritesNewEntry(string label, HistoryPreset preset)
    {
        // Arrange
        await using var harness = Started(OneMetric + "&from=2026-10-13T08:00&to=2026-10-13T10:00");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        // Act
        await harness.PressAsync(label);
        await harness.WaitForRawReadsAsync(2);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], preset), harness.Navigation.Navigations[0].Uri, "the address has the preset and the metric, and no times");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// The current preset is marked: the default one without a range, the one of the address, none for a fixed range.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <param name="marked">The text of the marked preset, or an empty text for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow("", "24 h")]
    [DataRow("?range=7d", "7 d")]
    [DataRow("?range=1y", "1 y")]
    [DataRow("?from=2026-10-13T08:00&to=2026-10-13T10:00", "")]
    public async Task HistoryPageMarksTheCurrentPreset(string query, string marked)
    {
        // Arrange
        await using var harness = new HistoryPageHarness(query);

        // Act
        await harness.AddAsync();

        // Assert
        var active = _presets.Where(label => harness.Buttons(label).Single().Classes.Contains("active")).ToArray();

        string[] expected = marked.Length == 0 ? [] : [marked];

        Assert.AreSequenceEqual(expected, active, "the marked preset");
    }

    /// <summary>
    /// <c>From</c> and <c>To</c> as applied write a fixed range as a new history entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task HistoryPageFromToWritesFixedRange()
    {
        // Arrange
        var from = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 13, 10, 30, 15, TimeSpan.Zero);

        await using var harness = Started(OneMetric + "&range=7d");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        // Act
        await harness.EnterAsync(("from", "2026-10-13T08:00"), ("to", "2026-10-13T10:30:15"));
        await harness.PressAsync("Apply");
        await harness.WaitForRawReadsAsync(2);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], null, from, to), harness.Navigation.Navigations[0].Uri, "the address has the fixed range and no preset");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
        Assert.AreEqual(from, harness.Series.RawQueries[1].From, "the chart is read from the start");
        Assert.AreEqual(to, harness.Series.RawQueries[1].To, "the chart is read up to the end");
    }

    /// <summary>
    /// A field that is not valid shows the text of the address and writes no address.
    /// </summary>
    /// <param name="from">The text of the start</param>
    /// <param name="to">The text of the end</param>
    /// <param name="text">The text that is shown</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow("x", "2026-10-13T10:00", "The address has an invalid value for the parameter from.")]
    [DataRow("2026-10-13T08:00", "y", "The address has an invalid value for the parameter to.")]
    [DataRow("2026-10-13T08:00", "", "The address needs both a start and an end time.")]
    [DataRow("2026-10-13T10:00", "2026-10-13T08:00", "The address has a start time that is not before its end time.")]
    public async Task HistoryPageInvalidFieldShowsTextAndStays(string from, string to, string text)
    {
        // Arrange
        await using var harness = Started(OneMetric);

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        // Act
        await harness.EnterAsync(("from", from), ("to", to));
        await harness.PressAsync("Apply");

        // Assert
        Assert.Contains(Enc(text), harness.Html(), "the text of the address");
        Assert.IsEmpty(harness.Navigation.Navigations, "nothing is navigated to");
        Assert.HasCount(1, harness.Series.RawQueries, "the chart is not read again");
    }

    /// <summary>
    /// A drag selection writes its range, widened to whole seconds, as a fixed range in a new history entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task HistoryPageSelectionWritesFixedRangeAsNewEntry()
    {
        // Arrange
        var from = HistoryPageHarness.Now.AddHours(-5);
        var to = HistoryPageHarness.Now.AddHours(-3);

        await using var harness = Started(OneMetric + "&range=7d");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        var events = harness.Events();

        // Act
        await harness.OnDispatcherAsync(() => events.Select(from.ToUnixTimeSeconds() + 0.25, to.ToUnixTimeSeconds() - 0.25));
        await harness.WaitForRawReadsAsync(2);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], null, from, to), harness.Navigation.Navigations[0].Uri, "the widened range is fixed in the address");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history, so that back returns to the range before");
        Assert.AreEqual(from, harness.Series.RawQueries[1].From, "the chart is read for the selection");
    }

    /// <summary>
    /// The chart is queried with its width in CSS pixels as the target, at most 1,000 and at least 1: the raw read asks for one time more.
    /// </summary>
    /// <param name="width">The width the browser reports</param>
    /// <param name="maxTimes">The most times the raw read asks for</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(640.7, 641)]
    [DataRow(1500.0, 1001)]
    [DataRow(2.9, 3)]
    public async Task HistoryPageQueriesWithTheChartWidth(double width, int maxTimes)
    {
        // Arrange
        await using var harness = Started(TwoMetrics);

        harness.Runtime.Answer("module", "width", width);

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        // Assert
        Assert.IsNotEmpty(harness.WithClass("history-chart"), "the chart host is shown");
        Assert.HasCount(2, harness.Series.RawQueries, "one raw read per metric");
        Assert.IsTrue(harness.Series.RawQueries.All(query => query.MaxTimes == maxTimes), "the target is the width of the chart");
        Assert.AreEqual(HistoryPageHarness.Now.AddDays(-1), harness.Series.RawQueries[0].From, "the last day is read");
        Assert.AreEqual(HistoryPageHarness.Now, harness.Series.RawQueries[0].To, "up to now");
        Assert.AreSequenceEqual(new[] { _memory, _cpu }.Select(Name).ToArray(), harness.Series.RawQueries.Select(query => $"{query.Source} {query.Name}").ToArray(), "the metrics in the order of the address");
    }

    /// <summary>
    /// Removing a metric writes the address without it, as a new history entry, and keeps the rest of the address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task HistoryPageRemovingMetricWritesAddressWithoutIt()
    {
        // Arrange
        await using var harness = Started(TwoMetrics + "&range=7d");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        var removable = harness.Buttons("Remove").Count;

        // Act
        await harness.PressAsync(harness.Buttons("Remove")[0]);
        await harness.WaitForRawReadsAsync(3);

        // Assert
        Assert.AreEqual(2, removable, "one Remove button per chosen metric");
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], HistoryPreset.SevenDays), harness.Navigation.Navigations[0].Uri, "the first metric is gone, the range stays");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
        Assert.ContainsSingle(harness.Buttons("Remove"), "one metric is left");
    }

    /// <summary>
    /// Adding the metric of an offered index writes the address with it appended, as a new history entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task HistoryPageAddWritesTheChosenMetric()
    {
        // Arrange
        await using var harness = Started(OneMetric);

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = [_cpu, _memory]
                                 };

        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.OptionCount(MetricField) == 2);

        // Act
        await harness.EnterAsync((MetricField, "1"));
        await harness.PressAsync("Add");
        await harness.WaitForRawReadsAsync(3);

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu, _memory]), harness.Navigation.Navigations[0].Uri, "the chosen metric is appended");
        Assert.IsFalse(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "a new entry of the history");
    }

    /// <summary>
    /// Without a metric the page reads the metric list once and nothing else: it offers the list by index and the text that asks for a metric, and it shows neither a
    /// chart nor an event list.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task HistoryPageWithoutMetricReadsTheList()
    {
        // Arrange
        await using var harness = new HistoryPageHarness(string.Empty);

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = [_cpu, _memory]
                                 };

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.OptionCount(MetricField) == 2);

        // Assert
        var options = harness.Options(MetricField);

        Assert.AreSequenceEqual(["StoredMetrics"], harness.Reader.Sequence, "the metric list is the only read");
        Assert.AreEqual(StorageLimits.MaxStoredMetrics, harness.Reader.Calls[0].Argument, "the list is limited to 256 metrics");
        Assert.AreSequenceEqual(["0", "1"], options.Select(option => option.Value).ToArray(), "the values are the indexes of the list");
        Assert.Contains("cpu_percent", options[0].Text, "the first option is the first metric of the list");
        Assert.Contains("mem_available_bytes", options[1].Text, "the second option is the second metric of the list");
        Assert.Contains(Enc(HistoryView.ChooseMetricsText), harness.Html(), "the text that asks for a metric");
        Assert.AreEqual(5, _presets.Count(label => harness.Buttons(label).Count == 1), "the five presets are shown");
        Assert.IsEmpty(harness.WithClass("history-chart"), "no chart");
        Assert.DoesNotContain("Events", harness.Text(), "no event list");
        Assert.IsEmpty(harness.Series.Calls, "no time-series read");
        Assert.IsEmpty(harness.Runtime.Calls, "nothing is called in the browser");
    }

    /// <summary>
    /// Without a metric no query runs, whatever the range says.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task HistoryPageWithoutMetricRunsNoQuery()
    {
        // Arrange
        await using var harness = new HistoryPageHarness("?range=7d");

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = [_cpu]
                                 };

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.OptionCount(MetricField) == 1);

        // Assert
        Assert.IsEmpty(harness.Series.Calls, "no time-series read");
        Assert.DoesNotContain(call => call != "StoredMetrics", harness.Reader.Sequence, "no snapshot read");
        Assert.Contains("active", harness.Buttons("7 d").Single().Classes, "the range of the address is still marked");
    }

    /// <summary>
    /// A list that the store cut says so, and a complete list does not.
    /// </summary>
    /// <param name="more">Whether the store has more metrics</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task HistoryPageShowsListCutText(bool more)
    {
        // Arrange
        await using var harness = new HistoryPageHarness(string.Empty);

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = [_cpu, _memory],
                                     More = more
                                 };

        // Act
        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.OptionCount(MetricField) == 2);

        // Assert
        Assert.AreEqual(more, harness.Html().Contains(Enc(HistoryView.MetricListCutText), StringComparison.Ordinal), "the text of the cut list is shown exactly when the list is cut");
    }

    /// <summary>
    /// A metric of the address that is not in the list stays chosen and is drawn.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task HistoryPageKeepsAddressMetricOutsideTheList()
    {
        // Arrange
        await using var harness = new HistoryPageHarness("?metric=other,thing");

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = [_cpu, _memory]
                                 };
        harness.Series.AddLine("other", "thing", string.Empty, ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-1), 5));

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        // Assert
        Assert.HasCount(1, harness.Series.RawQueries, "one metric is read");
        Assert.AreEqual("other", harness.Series.RawQueries[0].Source, "the source of the metric of the address");
        Assert.AreEqual("thing", harness.Series.RawQueries[0].Name, "the name of the metric of the address");
        Assert.ContainsSingle(harness.Buttons("Remove"), "the metric stays chosen");
        Assert.Contains("thing", harness.Text(), "the metric is shown");
        Assert.AreEqual(2, harness.OptionCount(MetricField), "the list is the stored one");
    }

    /// <summary>
    /// A ninth metric is refused with the text of the limit and writes no address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task HistoryPageRefusesNinthMetric()
    {
        // Arrange
        var chosen = Enumerable.Range(1, 8).Select(number => new SeriesKey("system", $"m{number}")).ToArray();
        var all = chosen.Append(new SeriesKey("system", "m9")).ToArray();

        await using var harness = new HistoryPageHarness("?" + string.Join('&', chosen.Select(metric => $"metric={metric.Source},{metric.Name}")));

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = all
                                 };

        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.OptionCount(MetricField) == 9);

        // Act
        await harness.EnterAsync((MetricField, "8"));
        await harness.PressAsync("Add");

        // Assert
        Assert.Contains(Enc("At most 8 metrics can be drawn at once."), harness.Html(), "the text of the limit");
        Assert.IsEmpty(harness.Navigation.Navigations, "nothing is navigated to");
    }

    /// <summary>
    /// A choice that is not an index of the offered list adds nothing: no address is written and no query runs.
    /// </summary>
    /// <param name="choice">The forged value of the select</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("2")]
    [DataRow("-1")]
    [DataRow("99999999999999999999")]
    [DataRow("abc")]
    [DataRow("")]
    [DataRow("1.5")]
    [DataRow("system,cpu_percent")]
    [DataRow("0,1")]
    [DataRow("../1")]
    [DataRow("0;1")]
    public async Task HistoryPageForgedMetricChoiceAddsNothing(string choice)
    {
        // Arrange
        await using var harness = Started(OneMetric);

        harness.Reader.Metrics = new StoredMetricList
                                 {
                                     Metrics = [_cpu, _memory]
                                 };

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);
        await harness.WaitForAsync(() => harness.OptionCount(MetricField) == 2);

        // Act
        await harness.EnterAsync((MetricField, choice));
        await harness.PressAsync("Add");

        // Assert
        Assert.IsEmpty(harness.Navigation.Navigations, "no address is written");
        Assert.HasCount(1, harness.Series.RawQueries, "no further query");
        Assert.ContainsSingle(harness.Buttons("Remove"), "no metric was added");
        Assert.IsEmpty(harness.Logger.Exceptions.Where(exception => exception is not null), "nothing failed");
    }

    /// <summary>
    /// A range that begins at or after now shows its text and reads nothing from the series store.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("2026-10-13T12:00", "2026-10-13T18:00")]
    [DataRow("2026-10-13T18:00", "2026-10-14T00:00")]
    public async Task HistoryPageFutureRangeShowsTextAndReadsNothing(string from, string to)
    {
        // Arrange
        await using var harness = new HistoryPageHarness($"{OneMetric}&from={from}&to={to}");

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.FutureRangeText), StringComparison.Ordinal));

        // Assert
        Assert.IsEmpty(harness.Series.Calls, "no time-series read");
        Assert.IsEmpty(harness.Drawn, "nothing is drawn");
    }

    /// <summary>
    /// The notes of the view are shown: a chosen metric without values says so.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task HistoryPageShowsTheNotesOfTheView()
    {
        // Arrange
        await using var harness = new HistoryPageHarness(OneMetric + "&metric=system,mem_available_bytes");

        harness.Series.AddLine("system", "cpu_percent", string.Empty, ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-1), 5));

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains("has no values in this range", StringComparison.Ordinal));

        // Assert
        Assert.Contains("system mem_available_bytes has no values in this range.", Collapsed(harness.Text()), "the note of the metric without values");
        Assert.DoesNotContain("system cpu_percent has no values", Collapsed(harness.Text()), "no note for the metric with values");
    }

    /// <summary>
    /// The event list shows each event with its time as a button, its kind and its text; the time sets the moment as a replacing history entry and the chart is not
    /// read again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task HistoryPageEventTimeSetsTheMoment()
    {
        // Arrange
        await using var harness = Started(OneMetric);

        harness.Series.Kernel.Add(ScriptedTimeSeriesReader.Kill(1, _killAt, "mysqld", 42));

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        var text = Collapsed(harness.Text());

        // Act
        await harness.PressAsync(harness.Buttons("2026-10-13 11:30:15").Single());
        await harness.WaitForStampReadsAsync(4);

        // Assert
        Assert.Contains("OOM kill", text, "the kind of the event");
        Assert.Contains(KillText, text, "the text of the event");
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], null, null, null, _killAt), harness.Navigation.Navigations[0].Uri, "the address has the moment");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.HasCount(1, harness.Series.RawQueries, "the chart is not read again");
        Assert.HasCount(1, harness.Drawn, "the chart is not drawn again");
    }

    /// <summary>
    /// A click replaces the entry of the history with the moment and reads only the snapshots: the chart is neither read nor drawn again, and it marks the moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task HistoryPageClickReplacesEntryAndReadsOnlySnapshots()
    {
        // Arrange
        var at = HistoryPageHarness.Now.AddHours(-2);

        await using var harness = Started(OneMetric);

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        var events = harness.Events();

        // Act
        await harness.OnDispatcherAsync(() => events.Pick(at.ToUnixTimeSeconds() + 0.7, 8));
        await harness.WaitForStampReadsAsync(4);
        await harness.WaitForAsync(() => LastMark(harness) == at.ToUnixTimeSeconds());

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], null, null, null, at), harness.Navigation.Navigations[0].Uri, "the address has the moment, floored to the second");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.HasCount(1, harness.Series.RawQueries, "the chart is not read again");
        Assert.HasCount(1, harness.Drawn, "the chart is not drawn again");
        Assert.AreEqual(at.ToUnixTimeSeconds(), LastMark(harness), "the chart marks the moment");
        Assert.ContainsSingle(call => call == "StoredMetrics", harness.Reader.Sequence, "the metric list is not read again");
    }

    /// <summary>
    /// A click takes the time of the nearest marker within the tolerance, else the time under the pointer floored to the second.
    /// </summary>
    /// <param name="offset">The distance of the click from the kill in seconds</param>
    /// <param name="expected">The distance of the moment from the kill in whole seconds</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(4.4, 0)]
    [DataRow(-7.9, 0)]
    [DataRow(30.9, 30)]
    [DataRow(-20.2, -21)]
    public async Task HistoryPageClickSnapsToTheNearestMarker(double offset, int expected)
    {
        // Arrange
        await using var harness = Started(OneMetric);

        harness.Series.Kernel.Add(ScriptedTimeSeriesReader.Kill(1, _killAt, "mysqld", 42));

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);

        var events = harness.Events();

        // Act
        await harness.OnDispatcherAsync(() => events.Pick(_killAt.ToUnixTimeSeconds() + offset, 8));

        // Assert
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], null, null, null, _killAt.AddSeconds(expected)), harness.Navigation.Navigations[0].Uri, "the moment");
    }

    /// <summary>
    /// A moment inside the range is marked in the chart, one outside keeps its panel and is not marked.
    /// </summary>
    /// <param name="at">The moment in the address</param>
    /// <param name="heading">The heading of the panel</param>
    /// <param name="marked">Whether the chart marks it</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow("2026-10-13T11:20", "Moment 2026-10-13 11:20:00 UTC", true)]
    [DataRow("2026-10-13T09:00", "Moment 2026-10-13 09:00:00 UTC", false)]
    public async Task HistoryPageMomentOutsideTheRangeKeepsPanelUnmarked(string at, string heading, bool marked)
    {
        // Arrange
        await using var harness = Started($"{OneMetric}&range=1h&at={at}");

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);
        await harness.WaitForStampReadsAsync(4);

        // Assert
        var setup = JsonSerializer.SerializeToElement(harness.Drawn[0].Arguments[1], JsonSerializerOptions.Web);
        var moment = setup.TryGetProperty("moment", out var found) ? found.ValueKind : JsonValueKind.Null;

        Assert.AreEqual(marked ? JsonValueKind.Number : JsonValueKind.Null, moment, "the chart is drawn with the moment only inside the range");
        Assert.Contains(heading, Collapsed(harness.Text()), "the panel of the moment is shown");
    }

    /// <summary>
    /// A change of the moment alone reads only the snapshots: the metric list and the chart are not read again, the running chart is not drawn again and the chart
    /// marks the new moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task HistoryPageMomentChangeReadsOnlySnapshots()
    {
        // Arrange
        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);
        await harness.WaitForStampReadsAsync(4);

        // Act
        await harness.GoToAsync($"{OneMetric}&at=2026-10-13T11:10");
        await harness.WaitForStampReadsAsync(8);
        await harness.WaitForAsync(() => LastMark(harness) == _laterMomentAt.ToUnixTimeSeconds());

        // Assert
        Assert.HasCount(1, harness.Series.RawQueries, "the chart is not read again");
        Assert.HasCount(1, harness.Drawn, "the chart is not drawn again");
        Assert.ContainsSingle(call => call == "StoredMetrics", harness.Reader.Sequence, "the metric list is not read again");
        Assert.AreEqual(_laterMomentAt.ToUnixTimeSeconds(), LastMark(harness), "the chart marks the new moment");
    }

    /// <summary>
    /// A change of the chart and of the moment in one address starts both reads.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task HistoryPageChartAndMomentChangeStartBoth()
    {
        // Arrange
        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);
        await harness.WaitForStampReadsAsync(4);

        // Act
        await harness.GoToAsync($"{OneMetric}&range=7d&at=2026-10-13T11:10");
        await harness.WaitForRawReadsAsync(2);
        await harness.WaitForStampReadsAsync(8);

        // Assert
        Assert.AreEqual(HistoryPageHarness.Now.AddDays(-7), harness.Series.RawQueries[1].From, "the chart is read for the new range");
        Assert.AreEqual(_laterMomentAt, ((SnapshotQuery)harness.StampReads[^1].Argument!).At, "the snapshots are read for the new moment");
    }

    /// <summary>
    /// The panel of a moment shows the moment and the link to the logs around it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task HistoryPageMomentShowsTheLogsLink()
    {
        // Arrange
        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        // Act
        await harness.AddAsync();
        await harness.WaitForDrawsAsync(1);
        await harness.WaitForStampReadsAsync(4);

        // Assert
        var links = harness.Links(LogsOfMoment);

        Assert.Contains(MomentHeading, Collapsed(harness.Text()), "the moment in UTC");
        Assert.HasCount(1, links, "one link to the logs");
        Assert.AreEqual(IncidentsView.LogsHref(_momentAt), links[0].Attributes["href"], "the logs from 30 seconds before to 5 minutes after the moment");
    }

    /// <summary>
    /// A moment without a stored snapshot within ten minutes shows the two texts and offers no earlier snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [TestCategory("AC10")]
    public async Task HistoryPageShowsNoSnapshotTexts()
    {
        // Arrange
        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        // Act
        await harness.AddAsync();
        await harness.WaitForStampReadsAsync(4);
        await harness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.NoProcessSnapshotText), StringComparison.Ordinal));

        // Assert
        Assert.Contains(Enc(HistoryView.NoConnectionSnapshotText), harness.Html(), "no connection snapshot");
        Assert.IsEmpty(harness.ButtonsStartingWith(SnapshotBefore), "no snapshot before");
    }

    /// <summary>
    /// The panel shows the process snapshot and the connection snapshot closest to the moment, reading one decoded snapshot at a time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [TestCategory("AC10")]
    public async Task HistoryPageShowsTheSnapshotsOfTheMoment()
    {
        // Arrange
        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        harness.Reader.Stamps.Add(HistorySamples.Stamp(1, ProcessKind, _momentAt.AddSeconds(-29.6)));
        harness.Reader.Stamps.Add(HistorySamples.Stamp(2, ConnectionKind, _momentAt));
        harness.Reader.Records[1] = HistorySamples.ProcessRecord(1, "web-1", _momentAt.AddSeconds(-29.6));
        harness.Reader.Records[2] = HistorySamples.ConnectionRecord(2, "web-2", _momentAt);

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains("ESTABLISHED", StringComparison.Ordinal));

        // Assert
        var text = Collapsed(harness.Text());

        Assert.Contains("Process snapshot of 2026-10-13 10:59:30 UTC", text, "the capture time of the process snapshot");
        Assert.Contains("web-1", text, "its source");
        Assert.Contains("nginx", text, "its program");
        Assert.Contains("Connection snapshot of 2026-10-13 11:00:00 UTC", text, "the capture time of the connection snapshot");
        Assert.Contains("web-2", text, "its source");
        Assert.IsEmpty(harness.ButtonsStartingWith(SnapshotBefore), "the process snapshot shown is not later than the moment");
        Assert.AreSequenceEqual(MomentCalls(harness),
                                ["Stamp process_snapshot AtOrBefore", "Stamp process_snapshot After", "Snapshot 1", "Stamp connection_snapshot AtOrBefore", "Stamp connection_snapshot After", "Snapshot 2"],
                                "the snapshots are read one kind after the other, one record each");
    }

    /// <summary>
    /// When the snapshot shown is later than the moment, <c>Snapshot before this moment</c> moves the moment to the next whole second after the earlier snapshot, which
    /// has sub-second capture times, replaces the entry of the history and shows that snapshot; the button is not offered again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task HistoryPageSnapshotBeforeShowsThatSnapshot()
    {
        // Arrange
        var earlier = _momentAt.AddSeconds(-29.6);
        var later = _momentAt.AddMilliseconds(300);

        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        harness.Reader.Stamps.Add(HistorySamples.Stamp(1, ProcessKind, earlier));
        harness.Reader.Stamps.Add(HistorySamples.Stamp(2, ProcessKind, later));
        harness.Reader.Records[1] = HistorySamples.ProcessRecord(1, "web-1", earlier);
        harness.Reader.Records[2] = HistorySamples.ProcessRecord(2, "web-2", later);

        await harness.AddAsync();
        await harness.WaitForAsync(() => harness.ButtonsStartingWith(SnapshotBefore).Count == 1);

        var shownFirst = Collapsed(harness.Text());

        // Act
        await harness.PressAsync(harness.ButtonsStartingWith(SnapshotBefore)[0]);
        await harness.WaitForStampReadsAsync(8);
        await harness.WaitForHtmlAsync(html => html.Contains("web-1", StringComparison.Ordinal));

        // Assert
        var calls = MomentCalls(harness);

        Assert.Contains("web-2", shownFirst, "the snapshot after the moment is shown first");
        Assert.HasCount(1, harness.Navigation.Navigations, "one navigation");
        Assert.AreEqual(Href([_cpu], null, null, null, HistoryView.RoundUpToSecond(earlier)), harness.Navigation.Navigations[0].Uri, "the moment is the whole second after the earlier snapshot");
        Assert.IsTrue(harness.Navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry of the history is replaced");
        Assert.AreSequenceEqual(["Stamp process_snapshot AtOrBefore", "Stamp process_snapshot After", "Snapshot 1"], calls.Skip(5).Take(3).ToArray(), "the snapshot before is found by the read at or before the moment");
        Assert.DoesNotContain("web-2", Collapsed(harness.Text()), "the later snapshot is gone");
        Assert.Contains("Process snapshot of 2026-10-13 10:59:30 UTC", Collapsed(harness.Text()), "the earlier snapshot is shown");
        Assert.IsEmpty(harness.ButtonsStartingWith(SnapshotBefore), "the button is not offered again");
    }

    /// <summary>
    /// Rendered statically, as the prerender does, the page shows the waiting text and reads nothing and calls nothing in the browser.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPagePrerenderedReadsNothing()
    {
        // Arrange
        await using var harness = new HistoryPageHarness($"{OneMetric}&at=2026-10-13T11:00");

        // Act
        var html = await harness.RenderStaticAsync();

        // Assert
        Assert.Contains(Enc(HistoryView.LoadingText), html, "the waiting text");
        Assert.IsEmpty(harness.Reader.Calls, "no call of the history reader");
        Assert.IsEmpty(harness.Series.Calls, "no call of the time-series reader");
        Assert.IsEmpty(harness.Runtime.Calls, "no call into the browser");
    }

    /// <summary>
    /// A new range cancels the running chart read, and only the new read is drawn.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageRangeChangeCancelsTheChartRead()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = Started(OneMetric);

        harness.Series.Hold = hold.Task;

        var add = harness.AddAsync();

        await harness.WaitForRawReadsAsync(1);

        var first = harness.Series.Calls.First(call => call.Name == nameof(ITimeSeriesReader.RawValuesAsync)).Token;

        // Act
        await harness.GoToAsync(OneMetric + "&range=7d");
        await harness.WaitForAsync(() => first.IsCancellationRequested);
        await harness.WaitForRawReadsAsync(2);

        var second = harness.Series.Calls.Last(call => call.Name == nameof(ITimeSeriesReader.RawValuesAsync)).Token;

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await harness.WaitForDrawsAsync(1);

        // Assert
        Assert.IsTrue(first.IsCancellationRequested, "the running chart read was cancelled");
        Assert.IsFalse(second.IsCancellationRequested, "the new chart read runs on");
        Assert.HasCount(1, harness.Drawn, "only the new read is drawn");
        Assert.DoesNotContain(Enc(HistoryView.ChartFailureText), harness.Html(), "a cancelled read shows no failure");
        Assert.DoesNotContain(Enc(HistoryView.ChartTimeoutText), harness.Html(), "a cancelled read shows no timeout");
    }

    /// <summary>
    /// A new moment does not cancel the running chart read, and the chart is read once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageMomentChangeKeepsTheChartRead()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = Started(OneMetric);

        harness.Series.Hold = hold.Task;

        var add = harness.AddAsync();

        await harness.WaitForRawReadsAsync(1);

        var read = harness.Series.Calls.First(call => call.Name == nameof(ITimeSeriesReader.RawValuesAsync)).Token;

        // Act
        await harness.GoToAsync($"{OneMetric}&at=2026-10-13T11:00");
        await harness.WaitForStampReadsAsync(4);

        var cancelled = read.IsCancellationRequested;

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await harness.WaitForDrawsAsync(1);

        // Assert
        Assert.IsFalse(cancelled, "the running chart read was not cancelled by the new moment");
        Assert.HasCount(1, harness.Series.RawQueries, "the chart was read once");
        Assert.HasCount(1, harness.Drawn, "the chart is drawn");
    }

    /// <summary>
    /// A new moment cancels the running snapshot read, and the page shows the new moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageNewMomentCancelsTheSnapshotRead()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = Started($"{OneMetric}&at=2026-10-13T11:00");

        harness.Reader.Hold = hold.Task;

        var add = harness.AddAsync();

        await harness.WaitForStampReadsAsync(1);

        var first = harness.StampReads[0].Token;

        // Act
        await harness.GoToAsync($"{OneMetric}&at=2026-10-13T11:05");
        await harness.WaitForAsync(() => first.IsCancellationRequested);

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await harness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.NoProcessSnapshotText), StringComparison.Ordinal));

        // Assert
        Assert.IsTrue(first.IsCancellationRequested, "the running snapshot read was cancelled");
        Assert.Contains("Moment 2026-10-13 11:05:00 UTC", Collapsed(harness.Text()), "the new moment is shown");
        Assert.DoesNotContain(Enc(HistoryView.MomentFailureText), harness.Html(), "a cancelled read shows no failure");
    }

    /// <summary>
    /// Leaving the page cancels the running chart read and the running snapshot read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageLeavingCancelsBothReads()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = Started(OneMetric);

        harness.Series.Hold = hold.Task;

        var add = harness.AddAsync();

        await harness.WaitForRawReadsAsync(1);

        harness.Reader.Hold = hold.Task;

        await harness.GoToAsync($"{OneMetric}&at=2026-10-13T11:00");
        await harness.WaitForStampReadsAsync(1);

        var chart = harness.Series.Calls.First(call => call.Name == nameof(ITimeSeriesReader.RawValuesAsync)).Token;
        var moment = harness.StampReads[0].Token;

        // Act
        await harness.DisposePageAsync();

        // Assert
        Assert.IsTrue(chart.IsCancellationRequested, "the chart read was cancelled");
        Assert.IsTrue(moment.IsCancellationRequested, "the snapshot read was cancelled");

        hold.SetResult();

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    /// <summary>
    /// A read that runs past ten seconds is stopped with the text of its part of the page.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageDeadlineShowsTexts()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var chartHarness = Started(OneMetric);
        await using var momentHarness = Started(OneMetric);

        chartHarness.Series.Hold = hold.Task;

        var add = chartHarness.AddAsync();

        await chartHarness.WaitForRawReadsAsync(1);
        await momentHarness.AddAsync();
        await momentHarness.WaitForDrawsAsync(1);

        momentHarness.Reader.Hold = hold.Task;

        await momentHarness.GoToAsync($"{OneMetric}&at=2026-10-13T11:00");
        await momentHarness.WaitForStampReadsAsync(1);

        // Act
        chartHarness.Clock.Advance(TimeSpan.FromSeconds(10));
        momentHarness.Clock.Advance(TimeSpan.FromSeconds(10));

        await chartHarness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.ChartTimeoutText), StringComparison.Ordinal));
        await momentHarness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.MomentTimeoutText), StringComparison.Ordinal));

        // Assert
        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        Assert.IsTrue(chartHarness.Series.Calls.First(call => call.Name == nameof(ITimeSeriesReader.RawValuesAsync)).Token.IsCancellationRequested, "the chart read was cancelled at the deadline");
        Assert.DoesNotContain(Enc(HistoryView.MomentTimeoutText), chartHarness.Html(), "the chart deadline does not show the text of the snapshots");
        Assert.DoesNotContain(Enc(HistoryView.ChartTimeoutText), momentHarness.Html(), "the snapshot deadline does not show the text of the chart");

        hold.SetResult();
    }

    /// <summary>
    /// A store failure shows the fixed text without its details and is logged with its exception and without any value of the address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageFailureShowsTextAndLogsNoAddress()
    {
        // Arrange
        var chartFailure = new InvalidOperationException("secret detail of the store");
        var momentFailure = new StoreException("secret detail of the snapshots");

        await using var chartHarness = Started($"{OneMetric}&metric=other,secretname");
        await using var momentHarness = Started($"{OneMetric}&metric=other,secretname");

        chartHarness.Series.Failure = chartFailure;

        await momentHarness.AddAsync();
        await momentHarness.WaitForDrawsAsync(1);

        momentHarness.Reader.Failure = momentFailure;

        // Act
        await chartHarness.AddAsync();
        await momentHarness.GoToAsync($"{OneMetric}&metric=other,secretname&at=2026-10-13T11:00");
        await chartHarness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.ChartFailureText), StringComparison.Ordinal));
        await momentHarness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.MomentFailureText), StringComparison.Ordinal));

        // Assert
        Assert.DoesNotContain("secret detail", chartHarness.Html(), "no detail of the chart failure");
        Assert.DoesNotContain("secret detail", momentHarness.Html(), "no detail of the snapshot failure");
        Assert.Contains(FailureLog, chartHarness.Logger.Messages, "the message of the log method for the chart");
        Assert.Contains(FailureLog, momentHarness.Logger.Messages, "the message of the log method for the snapshots");
        Assert.Contains(exception => ReferenceEquals(exception, chartFailure), chartHarness.Logger.Exceptions, "the exception of the chart is logged");
        Assert.Contains(exception => ReferenceEquals(exception, momentFailure), momentHarness.Logger.Exceptions, "the exception of the snapshots is logged");
        Assert.Contains(entry => entry.Level == LogLevel.Error && entry.EventId.Id == 75, chartHarness.Logger.Entries, "at level error with event 75");
        Assert.DoesNotContain(message => message.Contains("secretname", StringComparison.Ordinal) || message.Contains("cpu_percent", StringComparison.Ordinal), chartHarness.Logger.Messages, "no value of the address in the log");
    }

    /// <summary>
    /// A refusal of the store, which the logs view would show as a refused search, shows the failure text of this page and is logged as a failure.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryPageRefusedQueryShowsFailureText()
    {
        // Arrange
        const string refusal = "store: invalid query: limit must be 1 to 256";

        await using var chartHarness = Started(OneMetric);
        await using var momentHarness = Started(OneMetric);

        chartHarness.Series.Failure = new InvalidQueryException(refusal);

        await momentHarness.AddAsync();
        await momentHarness.WaitForDrawsAsync(1);

        momentHarness.Reader.Failure = new InvalidQueryException(refusal);

        // Act
        await chartHarness.AddAsync();
        await momentHarness.GoToAsync($"{OneMetric}&at=2026-10-13T11:00");
        await chartHarness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.ChartFailureText), StringComparison.Ordinal));
        await momentHarness.WaitForHtmlAsync(html => html.Contains(Enc(HistoryView.MomentFailureText), StringComparison.Ordinal));

        // Assert
        foreach (var harness in new[] { chartHarness, momentHarness })
        {
            Assert.DoesNotContain("The search cannot run", harness.Html(), "the text of a refused search is not shown");
            Assert.DoesNotContain("limit must be", harness.Html(), "no rule of the store is shown");
            Assert.Contains(FailureLog, harness.Logger.Messages, "the refusal is logged as a failure");
            Assert.DoesNotContain(exception => exception is InvalidQueryException, harness.Logger.Exceptions, "the refusal is wrapped");
            Assert.Contains(exception => exception is InvalidOperationException, harness.Logger.Exceptions, "the wrapper is logged");
        }
    }

    /// <summary>
    /// Creates a harness whose series store has a line for the metrics of the tests.
    /// </summary>
    /// <param name="query">The query of the address</param>
    /// <returns>The harness</returns>
    private static HistoryPageHarness Started(string query)
    {
        var harness = new HistoryPageHarness(query);

        harness.Series.AddLine(_cpu.Source, _cpu.Name, string.Empty, ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-3), 10), ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-1), 30));
        harness.Series.AddLine(_memory.Source, _memory.Name, string.Empty, ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-3), 1000), ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-1), 2000));
        harness.Series.AddLine("other", "secretname", string.Empty, ScriptedTimeSeriesReader.Point(HistoryPageHarness.Now.AddHours(-2), 7));

        return harness;
    }

    /// <summary>
    /// Writes the address that the page writes for a state, as the page does.
    /// </summary>
    /// <param name="metrics">The metrics</param>
    /// <param name="preset">The preset, or <see langword="null"/> for none</param>
    /// <param name="from">The start of a fixed range, or <see langword="null"/></param>
    /// <param name="to">The end of a fixed range, or <see langword="null"/></param>
    /// <param name="at">The moment, or <see langword="null"/></param>
    /// <returns>The relative address</returns>
    private static string Href(IReadOnlyList<SeriesKey> metrics, HistoryPreset? preset = null, DateTimeOffset? from = null, DateTimeOffset? to = null, DateTimeOffset? at = null)
    {
        return HistoryView.HistoryHref(new HistoryAddress
                                       {
                                           Metrics = metrics,
                                           Preset = preset,
                                           From = from,
                                           To = to,
                                           At = at
                                       });
    }

    /// <summary>
    /// Gets the moment of the last call that marked a moment in the chart, in Unix seconds.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <returns>The whole seconds, or <see langword="null"/> when no moment is marked</returns>
    private static long? LastMark(HistoryPageHarness harness)
    {
        return harness.Marked.Count == 0 || harness.Marked[^1].Arguments[0] is not double seconds ? null : (long)Math.Round(seconds);
    }

    /// <summary>
    /// Gets the calls of the moment reads other than the metric list.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <returns>The calls in order</returns>
    private static IReadOnlyList<string> MomentCalls(HistoryPageHarness harness)
    {
        return [.. harness.Reader.Sequence.Where(call => call != "StoredMetrics")];
    }

    /// <summary>
    /// Writes the name of a metric as the legend does.
    /// </summary>
    /// <param name="metric">The metric</param>
    /// <returns>The source and the name</returns>
    private static string Name(SeriesKey metric)
    {
        return $"{metric.Source} {metric.Name}";
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