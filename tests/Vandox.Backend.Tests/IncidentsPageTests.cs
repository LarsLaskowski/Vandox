using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Storage;

using static Vandox.Backend.Tests.LogViewSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentsPage"/>. The tests read the page the way a browser shows it: links by their address, buttons by their text.
/// </summary>
[TestClass]
public class IncidentsPageTests
{
    #region Constants

    private const string Address = "http://localhost/incidents";

    #endregion // Constants

    #region Fields

    private static readonly long[] _shown = [9L, 8L, 6L];

    private static readonly long[] _secondPage = [20L, 19L, 18L];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The page shows the incidents in the order of the store with the number as a link, the time of the first kill as a link to the logs of that moment, the duration, the kills, the victim as text and the hypothesis, and marks an incident with a note.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsPageShowsRowsNewestFirstWithTimeVictimDurationHypothesis()
    {
        // Arrange
        await using var harness = new IncidentPageHarness(Address);
        await using var withNote = new IncidentPageHarness(Address);
        await using var withoutNote = new IncidentPageHarness(Address);

        var newest = IncidentViewSamples.Summary(9);
        var markup = IncidentViewSamples.Summary(8, "<b>x</b>");
        var unknown = IncidentViewSamples.Summary(6);

        unknown.Hypothesis.GrowthKnown = false;
        unknown.Hypothesis.Growth.Clear();
        unknown.Hypothesis.BackupRunning = null;
        harness.Browser.Summaries = [newest, markup, unknown];

        var noted = IncidentViewSamples.Summary(5);
        var plain = IncidentViewSamples.Summary(5);

        noted.HasNotes = true;
        withNote.Browser.Summaries = [noted];
        withoutNote.Browser.Summaries = [plain];

        // Act
        await harness.AddAsync<IncidentsPage>();
        await withNote.AddAsync<IncidentsPage>();
        await withoutNote.AddAsync<IncidentsPage>();

        var html = harness.Html();
        var elements = harness.Elements();

        // Assert
        var query = harness.Browser.Queries<IncidentListQuery>(FakeIncidentBrowser.IncidentSummaries).Single();
        var order = _shown.Select(id => elements.ToList().FindIndex(element => element.Tag == "a" && element.Attributes.GetValueOrDefault("href") == IncidentsView.IncidentHref(id))).ToArray();

        Assert.AreEqual(IncidentsView.PageIncidents, query.Limit, "the page asks for 20 incidents");
        Assert.IsNull(query.Before, "the first page has no position");
        Assert.IsTrue(order.All(index => index >= 0), "every incident number is a link to its page");
        Assert.AreSequenceEqual(order.Order().ToArray(), order, "the rows are in the order of the store");

        foreach (var summary in new[] { newest, markup, unknown })
        {
            var link = harness.Links(IncidentsView.LogsHref(summary.FirstKillAt));

            Assert.HasCount(1, link, $"the time of the first kill of incident {summary.Id} links to the logs of that moment");
            Assert.Contains(LogsView.ShowSeconds(summary.FirstKillAt), link[0].Text, $"the link of incident {summary.Id} shows the time");
        }

        Assert.Contains("0:02:05", html, "the duration from the first to the last kill");
        Assert.Contains("mariadbd", html, "the victim");
        Assert.Contains(Enc("<b>x</b>"), html, "markup in a victim is text");
        Assert.DoesNotContain("<b>x</b>", html, "no markup from a victim");
        Assert.Contains("pzstd", html, "the program that grew");
        Assert.Contains(Enc(IncidentsView.Growth(512UL * 1048576)), html, "its growth");
        Assert.Contains(IncidentsView.BackupNotRunning, html, "no backup");
        Assert.Contains(IncidentsView.Unknown, html, "an unknown hypothesis");
        Assert.AreNotEqual(withoutNote.Html(), withNote.Html(), "an incident with a note is marked");
    }

    /// <summary>
    /// Older incidents asks for the next 20 after the last row, a short page offers no further page, and Newest incidents goes back to the start.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsPagePagesTwentyAtATime()
    {
        // Arrange
        await using var harness = new IncidentPageHarness(Address);
        await using var empty = new IncidentPageHarness(Address);

        var first = Enumerable.Range(21, 20).Reverse().Select(id => IncidentViewSamples.Summary(id)).ToArray();
        var second = _secondPage.Select(id => IncidentViewSamples.Summary(id)).ToArray();

        harness.Browser.SummaryPages.Enqueue(first);
        harness.Browser.SummaryPages.Enqueue(second);
        harness.Browser.Summaries = first;
        empty.Browser.SummaryPages.Enqueue(first);
        empty.Browser.SummaryPages.Enqueue([]);

        await harness.AddAsync<IncidentsPage>();
        await empty.AddAsync<IncidentsPage>();

        var onFirst = harness.Html();
        var olderOnFirst = harness.Buttons(IncidentsView.OlderIncidents);

        // Act
        await harness.Renderer.ClickAsync(olderOnFirst.Single());

        var onSecond = harness.Html();
        var olderOnSecond = harness.Buttons(IncidentsView.OlderIncidents);
        var newestOnSecond = harness.Buttons(IncidentsView.NewestIncidents);

        await harness.Renderer.ClickAsync(newestOnSecond.Single());

        await empty.Renderer.ClickAsync(empty.Buttons(IncidentsView.OlderIncidents).Single());

        var emptyHtml = empty.Html();

        // Assert
        var queries = harness.Browser.Queries<IncidentListQuery>(FakeIncidentBrowser.IncidentSummaries);
        var position = queries.Count > 1 ? queries[1].Before : null;

        Assert.HasCount(3, queries, "the first page, the next page and the first page again");
        Assert.IsTrue(queries.All(query => query.Limit == IncidentsView.PageIncidents), "every query asks for 20 incidents");
        Assert.IsNull(queries[0].Before, "the first page has no position");
        Assert.IsNotNull(position, "the next page continues after a position");
        Assert.AreEqual(first[^1].Start, position.StartedAt, "the position is the start of the last row");
        Assert.AreEqual(first[^1].Id, position.Id, "the position is the number of the last row");
        Assert.IsNull(queries[2].Before, "Newest incidents asks for the first page again");
        Assert.HasCount(1, olderOnFirst, "a full page offers the next page");
        Assert.IsEmpty(harness.Buttons(IncidentsView.NewestIncidents), "the first page offers no way back");
        Assert.Contains(IncidentsView.IncidentHref(40), onFirst, "the first page holds the newest incident");
        Assert.Contains(IncidentsView.IncidentHref(20), onSecond, "the second page holds the older incidents");
        Assert.DoesNotContain($"href=\"{IncidentsView.IncidentHref(40)}\"", onSecond, "the second page does not repeat the first one");
        Assert.IsEmpty(olderOnSecond, "a page of fewer than 20 offers no next page");
        Assert.HasCount(1, newestOnSecond, "a later page offers the way back");
        Assert.Contains(Enc(IncidentsView.NoOlderIncidents), emptyHtml, "an empty next page says so");
        Assert.DoesNotContain(Enc(IncidentsView.EmptyList), emptyHtml, "it is not the text of an empty store");
    }

    /// <summary>
    /// Rendered statically, as the prerender does, the page shows its title and the waiting text and calls no store method.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsPageQueriesNothingWhilePrerendered()
    {
        // Arrange
        await using var harness = new IncidentPageHarness(Address);

        harness.Browser.Summaries = [IncidentViewSamples.Summary(3)];

        // Act
        var html = await harness.RenderStaticAsync<IncidentsPage>();

        // Assert
        Assert.Contains($"<h1>{IncidentsView.ListTitle}</h1>", html, "the title");
        Assert.Contains(Enc(IncidentsView.Loading), html, "the waiting text");
        Assert.IsEmpty(harness.Browser.Calls, "no store call while prerendering");
        Assert.IsEmpty(harness.Records.Queries, "no record read while prerendering");
    }

    /// <summary>
    /// An empty store, a read that runs into the deadline and a failed read show their fixed texts, and a failure is logged without its details on the page.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("empty")]
    [DataRow("deadline")]
    [DataRow("failure")]
    public async Task IncidentsPageShowsEmptyDeadlineAndFailureTexts(string scenario)
    {
        // Arrange
        await using var harness = new IncidentPageHarness(Address);

        var failure = new StoreException("store: reading incidents: secret detail of the store");

        if (scenario == "deadline")
        {
            harness.Browser.Before = (_, token) => Task.Delay(Timeout.Infinite, token);
        }

        if (scenario == "failure")
        {
            harness.Browser.Failures[FakeIncidentBrowser.IncidentSummaries] = failure;
        }

        // Act
        var add = harness.AddAsync<IncidentsPage>();

        if (scenario == "deadline")
        {
            await harness.Browser.WaitForCallsAsync(FakeIncidentBrowser.IncidentSummaries, 1);

            harness.Clock.Advance(LogsView.Deadline);
        }

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var html = harness.Html();

        Assert.AreEqual(scenario == "empty", html.Contains(Enc(IncidentsView.EmptyList), StringComparison.Ordinal), "the text of an empty store is shown only for it");
        Assert.AreEqual(scenario == "deadline", html.Contains(Enc(IncidentsView.ListDeadline), StringComparison.Ordinal), "the deadline text is shown only for the deadline");
        Assert.AreEqual(scenario == "failure", html.Contains(Enc(IncidentsView.ListFailure), StringComparison.Ordinal), "the failure text is shown only for a failure");
        Assert.DoesNotContain("secret detail", html, "no detail of the exception");

        if (scenario == "deadline")
        {
            Assert.IsTrue(harness.Browser.Calls.Single().CancellationToken.IsCancellationRequested, "the read was cancelled at the deadline");
        }

        if (scenario == "failure")
        {
            Assert.Contains("incidents view: reading failed", harness.Logger.Messages, "the message of the log method");
            Assert.Contains(exception => ReferenceEquals(exception, failure), harness.Logger.Exceptions, "the exception is logged");
            Assert.Contains(entry => entry.Level == LogLevel.Error, harness.Logger.Entries, "at level error");
        }
    }

    #endregion // Methods
}