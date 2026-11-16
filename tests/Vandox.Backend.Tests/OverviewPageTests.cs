using System.Text;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="Components.Pages.OverviewPage"/>
/// </summary>
[TestClass]
public class OverviewPageTests
{
    #region Constants

    /// <summary>
    /// The event id of the log entry of a failed read.
    /// </summary>
    private const int ReadFailedEventId = 74;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Gets the start time of the clock.
    /// </summary>
    private static DateTimeOffset Now => new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Prerendered, the page shows its heading and the waiting text, makes no store call and takes no pause of the session.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task OverviewPagePrerenderedShowsLoadingAndReadsNothing()
    {
        // Arrange
        await using var harness = Harness(new TimerSignalingClock(Now));

        // Act
        var html = await harness.RenderStaticAsync();

        // Assert
        Assert.Contains("<h1>Overview</h1>", html, "the heading");
        Assert.Contains($"<p>{LogViewSamples.Enc("Loading…")}</p>", html, "the waiting text");
        Assert.IsEmpty(harness.Reader.Calls, "no overview read while prerendering");
        Assert.IsEmpty(harness.Browser.Calls, "no incident read while prerendering");
        Assert.IsFalse(harness.Activity.IsPaused, "the prerendered page takes no pause");
    }

    /// <summary>
    /// One refresh makes exactly five store calls in turn with the bounds of the view, and the next refresh makes five more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task OverviewPageRefreshMakesFiveStoreCalls()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);

        await using var harness = Harness(clock);

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));

        var firstCalls = harness.Reader.Calls;
        var firstSummaries = harness.Browser.Queries<IncidentListQuery>(FakeIncidentBrowser.IncidentSummaries);

        await WaitForTimerAsync(clock, OverviewView.RefreshInterval);
        clock.Advance(OverviewView.RefreshInterval);
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now.Add(OverviewView.RefreshInterval)), StringComparison.Ordinal));

        // Assert
        var until = Now.AddSeconds(90);

        Assert.AreEqual("FreshnessAsync, LatestValuesAsync, LatestMariaDbStatusAsync, LatestServiceStatesAsync", string.Join(", ", firstCalls.Select(call => call.Name)), "the four overview reads of one refresh in turn");
        Assert.AreEqual((OverviewView.LiveScanLimit, until), firstCalls[0].Argument, "the freshness read with the scan limit and now plus 90 seconds");
        Assert.AreEqual(until, firstCalls[2].Argument, "the MariaDB read with now plus 90 seconds");
        AssertValueQuery((LatestValueQuery)firstCalls[1].Argument!, until);
        AssertServiceQuery((LatestServiceQuery)firstCalls[3].Argument!, until);
        Assert.HasCount(1, firstSummaries, "one incident read per refresh");
        Assert.AreEqual(OverviewView.IncidentSummaries, firstSummaries[0].Limit, "the incident read with its limit");
        Assert.HasCount(8, harness.Reader.Calls, "four overview reads per refresh, two refreshes");
        Assert.HasCount(2, harness.Browser.Queries<IncidentListQuery>(FakeIncidentBrowser.IncidentSummaries), "one incident read per refresh, two refreshes");
    }

    /// <summary>
    /// The page refreshes every 30 seconds without a new page: the same component shows the new values and the new update time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task OverviewPageShowsUpdatedTimeAfterRefresh()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);

        await using var harness = Harness(clock);

        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));

        var first = harness.Html();

        harness.Reader.Freshness = Freshness(Now.AddSeconds(20));
        await WaitForTimerAsync(clock, OverviewView.RefreshInterval);

        // Act
        clock.Advance(OverviewView.RefreshInterval);
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now.Add(OverviewView.RefreshInterval)), StringComparison.Ordinal));

        // Assert
        var second = harness.Html();

        Assert.Contains("2026-10-01 09:59:30 (30 s ago)", first, "the live data of the first refresh");
        Assert.Contains("2026-10-01 10:00:20 (10 s ago)", second, "the live data of the second refresh, aged at its time");
        Assert.DoesNotContain(Updated(Now), second, "the old update time is replaced");
        Assert.IsEmpty(harness.Renderer.Exceptions, "the refresh renders through the dispatcher without an exception");
    }

    /// <summary>
    /// The store calls of a refresh, from the start and from the timer, run off the dispatcher, and the result is rendered through it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task OverviewPageReadsOffTheDispatcherAndRendersThroughIt()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);
        var onDispatcher = new List<bool>();

        await using var harness = Harness(clock);

        harness.Reader.Entered = _ =>
                                 {
                                     lock (onDispatcher)
                                     {
                                         onDispatcher.Add(harness.Renderer.Dispatcher.CheckAccess());
                                     }
                                 };

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));
        await WaitForTimerAsync(clock, OverviewView.RefreshInterval);
        clock.Advance(OverviewView.RefreshInterval);
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now.Add(OverviewView.RefreshInterval)), StringComparison.Ordinal));

        // Assert
        lock (onDispatcher)
        {
            Assert.HasCount(8, onDispatcher, "eight overview reads in two refreshes");
            Assert.DoesNotContain(true, onDispatcher, "no read runs on the dispatcher");
        }

        Assert.IsEmpty(harness.Renderer.Exceptions, "the model is applied through the dispatcher without an exception");
    }

    /// <summary>
    /// A refresh that fails keeps the values it shows, adds the fixed failure line and logs the failure; the next successful refresh removes the line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task OverviewPageKeepsValuesAndShowsFailureLine()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);
        var failure = "<p class=\"failure\">Refresh failed at 2026-10-01 10:00:30; the values are from 2026-10-01 10:00:00.</p>";

        await using var harness = Harness(clock);

        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));
        await WaitForTimerAsync(clock, OverviewView.RefreshInterval);
        harness.Reader.Failure = new StoreException("store: reading: disk I/O error");

        // Act
        clock.Advance(OverviewView.RefreshInterval);
        await harness.WaitForHtmlAsync(html => html.Contains(failure, StringComparison.Ordinal));

        var failed = harness.Html();

        harness.Reader.Failure = null;
        clock.Advance(OverviewView.RefreshInterval);
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now.AddSeconds(60)), StringComparison.Ordinal));

        // Assert
        Assert.Contains(Updated(Now), failed, "the update time of the shown values stays");
        Assert.Contains("2026-10-01 09:59:30 (30 s ago)", failed, "the shown values stay");
        Assert.DoesNotContain("disk I/O error", failed, "no exception text reaches the page");
        Assert.HasCount(1, harness.Logger.Entries.Where(entry => entry.EventId.Id == ReadFailedEventId && entry.Level == LogLevel.Warning).ToList(), "the failed read is logged once");
        Assert.DoesNotContain("Refresh failed", harness.Html(), "the next successful refresh removes the line");
    }

    /// <summary>
    /// A first refresh that passes its 10-second deadline shows the failure line without values and is not logged as a failed read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task OverviewPageShowsFailureLineBeforeAnyValues()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = Harness(clock);

        harness.Reader.Hold = hold.Task;
        await harness.AddAsync();
        await WaitForTimerAsync(clock, OverviewView.RefreshDeadline);

        // Act
        clock.Advance(OverviewView.RefreshDeadline);
        await harness.WaitForHtmlAsync(html => html.Contains("<p class=\"failure\">Refresh failed at 2026-10-01 10:00:10; no values have been read yet.</p>", StringComparison.Ordinal));

        // Assert
        Assert.IsTrue(harness.Reader.Calls[0].Token.IsCancellationRequested, "the read is cancelled at the deadline");
        Assert.IsEmpty(harness.Logger.Entries.Where(entry => entry.EventId.Id == ReadFailedEventId).ToList(), "the deadline is not logged as a failed read");
    }

    /// <summary>
    /// While the connection is lost the timer makes no store call; when it is back the page refreshes at once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task OverviewPageMakesNoStoreCallWhileDisconnected()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);

        await using var harness = Harness(clock);

        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));
        await WaitForTimerAsync(clock, OverviewView.RefreshInterval);
        await harness.Connection.OnConnectionDownAsync(null!, TestContext.CancellationToken);

        // Act
        clock.Advance(OverviewView.RefreshInterval);

        var callsWhileDown = harness.Reader.Calls.Count;

        await harness.Connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now.Add(OverviewView.RefreshInterval)), StringComparison.Ordinal));

        // Assert
        Assert.AreEqual(4, callsWhileDown, "no store call on the tick while the connection is down");
        Assert.HasCount(8, harness.Reader.Calls, "the refresh at the start and the one when the connection is back, none for the tick");
        Assert.HasCount(2, harness.Browser.Queries<IncidentListQuery>(FakeIncidentBrowser.IncidentSummaries), "no incident read on the tick while the connection is down");
    }

    /// <summary>
    /// Leaving the page cancels the running read, releases the pause, and neither the timer nor a restored connection starts another read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    [TestCategory("AC9")]
    public async Task OverviewPageStopsRefreshingWhenLeft()
    {
        // Arrange
        var clock = new TimerSignalingClock(Now);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = Harness(clock);

        harness.Reader.Hold = hold.Task;
        await harness.AddAsync();

        var entered = await harness.Reader.WaitForEntryAsync(TestContext.CancellationToken);

        // Act
        var disposal = await harness.DisposePageAsync();

        await harness.Connection.OnConnectionDownAsync(null!, TestContext.CancellationToken);
        await harness.Connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);
        clock.Advance(OverviewView.RefreshInterval);

        // Assert
        Assert.IsTrue(entered, "the first read started");
        Assert.IsNull(disposal, "leaving the page throws nothing");
        Assert.IsTrue(harness.Reader.Calls[0].Token.IsCancellationRequested, "the running read is cancelled");
        Assert.IsFalse(harness.Activity.IsPaused, "the pause is released");
        Assert.HasCount(1, harness.Reader.Calls, "no read after the page was left");
    }

    /// <summary>
    /// While the page is shown interactively its circuit's activity does not extend the session; leaving the page ends the pause.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task OverviewPagePausesSessionExtensionWhileShown()
    {
        // Arrange
        await using var harness = Harness(new TimerSignalingClock(Now));

        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));

        var pausedWhileShown = harness.Activity.IsPaused;

        // Act
        await harness.DisposePageAsync();

        // Assert
        Assert.IsTrue(pausedWhileShown, "the shown page pauses the session extension");
        Assert.IsFalse(harness.Activity.IsPaused, "leaving the page ends the pause");
    }

    /// <summary>
    /// The page releases its pause first when it is disposed, so a disposal of the refresher that throws leaves no pause behind.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task OverviewPageReleasesPauseWhenRefresherDisposalFails()
    {
        // Arrange
        await using var harness = Harness(new DisposeFailingClock(Now));

        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));

        var pausedWhileShown = harness.Activity.IsPaused;

        // Act
        await harness.DisposePageAsync();

        // Assert
        Assert.IsTrue(pausedWhileShown, "the shown page pauses the session extension");
        Assert.IsFalse(harness.Activity.IsPaused, "the pause is released although the refresher's disposal failed");
    }

    /// <summary>
    /// A unit name is shown as external text, so a direction control in it is escaped, and the row of a failed unit is marked.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task OverviewPageShowsUnitNameAsExternalText()
    {
        // Arrange
        var control = char.ConvertFromUtf32(0x202E);
        var unit = $"evil{control}.service";

        await using var harness = Harness(new TimerSignalingClock(Now));

        harness.Reader.Services = new ServiceStatePage
                                  {
                                      States = [
                                                   new LatestServiceState
                                                   {
                                                       Unit = unit,
                                                       ActiveState = "failed",
                                                       SubState = "failed",
                                                       Restarts = 1,
                                                       CapturedAt = Now.AddSeconds(-30)
                                                   }
                                               ],
                                      Total = 1,
                                      Newest = Now.AddSeconds(-30)
                                  };

        // Act
        await harness.AddAsync();
        await harness.WaitForHtmlAsync(html => html.Contains(Updated(Now), StringComparison.Ordinal));

        // Assert
        var html = harness.Html();

        Assert.Contains(ExternalHtml(unit), html, "the unit name as external text");
        Assert.DoesNotContain(control, html, "the direction control never reaches the page as itself");
        Assert.Contains("<tr class=\"failed\">", html, "the row of the failed unit is marked");
    }

    /// <summary>
    /// Creates a harness whose reader answers with a fresh agent.
    /// </summary>
    /// <param name="clock">The clock of the page</param>
    /// <returns>The harness</returns>
    private static OverviewPageHarness Harness(TimeProvider clock)
    {
        var harness = new OverviewPageHarness(clock);

        harness.Reader.Freshness = Freshness(Now.AddSeconds(-30));

        return harness;
    }

    /// <summary>
    /// Creates the freshness read of an agent that received a batch 10 seconds before now.
    /// </summary>
    /// <param name="lastLiveAt">The capture time of the newest live record</param>
    /// <returns>The read</returns>
    private static AgentFreshness Freshness(DateTimeOffset lastLiveAt)
    {
        return new AgentFreshness
               {
                   AgentId = "agent-1",
                   ReceivedAt = Now.AddSeconds(-10),
                   LastLiveAt = lastLiveAt
               };
    }

    /// <summary>
    /// Gets the update line of a time.
    /// </summary>
    /// <param name="at">The time of the refresh</param>
    /// <returns>The markup of the line</returns>
    private static string Updated(DateTimeOffset at)
    {
        return $"<p>Updated {LogsView.ShowSeconds(at)}</p>";
    }

    /// <summary>
    /// Writes the markup the external text display gives a text.
    /// </summary>
    /// <param name="text">The external text</param>
    /// <returns>The markup</returns>
    private static string ExternalHtml(string text)
    {
        var builder = new StringBuilder("<span class=\"external\">");

        foreach (var run in LogText.Runs(text, LogText.MaxShortChars, IncidentsView.MaxTextRuns))
        {
            builder.Append(run.Escaped ? $"<span class=\"escape\">{LogViewSamples.Enc(run.Text)}</span>" : LogViewSamples.Enc(run.Text));
        }

        return builder.Append("</span>").ToString();
    }

    /// <summary>
    /// Checks the latest-value query of a refresh.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="until">The expected bound</param>
    private static void AssertValueQuery(LatestValueQuery query, DateTimeOffset until)
    {
        Assert.AreEqual(Describe(OverviewView.Series), Describe(query.Series), "the series of the view");
        Assert.AreEqual(OverviewView.MountWindow, query.Window, "the window of the mounts");
        Assert.AreEqual(OverviewView.MaxMounts, query.MaxLabelSets, "at most the cap of mounts per series");
        Assert.AreEqual(until, query.Until, "now plus 90 seconds");
    }

    /// <summary>
    /// Checks the service query of a refresh.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="until">The expected bound</param>
    private static void AssertServiceQuery(LatestServiceQuery query, DateTimeOffset until)
    {
        Assert.AreEqual(OverviewView.UnitRetirement, query.Retirement, "the retirement of units");
        Assert.AreEqual(OverviewView.MaxUnits, query.Limit, "the cap of units");
        Assert.AreEqual(until, query.Until, "now plus 90 seconds");
    }

    /// <summary>
    /// Describes series keys as source and name.
    /// </summary>
    /// <param name="series">The keys</param>
    /// <returns>The description</returns>
    private static string Describe(IEnumerable<SeriesKey> series)
    {
        return string.Join(", ", series.Select(key => $"{key.Source}/{key.Name}"));
    }

    /// <summary>
    /// Waits until the clock has a timer of a due time, at most 20 seconds.
    /// </summary>
    /// <param name="clock">The clock</param>
    /// <param name="dueTime">The due time</param>
    /// <returns>A task that completes when the timer exists</returns>
    private Task WaitForTimerAsync(TimerSignalingClock clock, TimeSpan dueTime)
    {
        return clock.WaitForTimersAsync(dueTime, 1, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
    }

    #endregion // Methods
}