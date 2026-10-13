using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

using static Vandox.Backend.Tests.LogViewSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogsPage"/>. The tests read the page the way a browser shows it: the form has the fields q, source, program, severity, from and to (by
/// <c>name</c>), the submit handler is on the form, and the buttons Next page, First page, Continue and Context are found by their text.
/// </summary>
[TestClass]
public partial class LogsPageTests
{
    #region Constants

    private const string Address = "http://localhost/logs";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The page passes the filters of the address to the store, without the fragment, with the page size and scan bound and the storable range when no time is given.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPagePassesTheAddressFiltersToTheStore()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?source=syslog&program=sshd&severity=warning&template=5&from=2026-10-01T10:00&to=2026-10-02T11:30:15&q=disk%20full#frag");
        await using var open = new LogPageHarness($"{Address}?q=x");

        harness.Explorer.TemplateResult = Template(5, "user failed", 3);

        // Act
        await harness.AddAsync<LogsPage>();
        await open.AddAsync<LogsPage>();

        // Assert
        var filter = harness.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs).Single();
        var unbounded = open.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs).Single();

        Assert.AreEqual("disk full", filter.Text, "text without the fragment");
        Assert.DoesNotContain("#", filter.Text, "no fragment in the text");
        Assert.AreEqual("syslog", filter.Source, "source");
        Assert.AreEqual("sshd", filter.Program, "program");
        Assert.AreEqual((byte)4, filter.MaxPriority, "the severity warning is priority 4");
        Assert.AreEqual(5L, filter.TemplateId, "template");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), filter.From, "from");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), filter.To, "to");
        Assert.AreEqual(LogsView.PageLines, filter.Limit, "page size");
        Assert.AreEqual(LogsView.ScanRows, filter.ScanLimit, "scan bound");
        Assert.IsNull(filter.After, "the first page has no position");
        Assert.AreEqual(StorableTime.Min, unbounded.From, "the storable start when no time is given");
        Assert.AreEqual(StorableTime.Max, unbounded.To, "the storable end when no time is given");
        Assert.AreEqual(100, unbounded.Limit, "the page size is 100");
    }

    /// <summary>
    /// The page shows each line with its fields and shows external text as text, with invisible characters as visible escapes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageRendersLinesWithEscapedText()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);

        await using var harness = new LogPageHarness($"{Address}?q=x");

        harness.Explorer.FindPage = Page(null, false, Line(1, Time, "<b>x</b>"), Line(2, Time.AddSeconds(1), $"a{bidi}b", null));

        // Act
        await harness.AddAsync<LogsPage>();

        var html = harness.Html();

        // Assert
        Assert.AreEqual(0x202e, char.ConvertToUtf32(bidi, 0), "the input holds U+202E");
        Assert.Contains(Shown(Time), html, "time with milliseconds in UTC");
        Assert.Contains("syslog", html, "source");
        Assert.Contains("web-1", html, "host");
        Assert.Contains("sshd", html, "program");
        Assert.Contains("1234", html, "process ID");
        Assert.Contains(Enc("err"), html, "severity name of priority 3");
        Assert.IsTrue(DashCell().IsMatch(html), "a dash for a line without priority");
        Assert.Contains(Enc("<b>x</b>"), html, "markup in a message appears as text");
        Assert.DoesNotContain("<b>x</b>", html, "no markup from a message");
        Assert.IsTrue(EscapeElement().IsMatch(html), "U+202E appears as the visible escape inside an element of class escape");
        Assert.DoesNotContain(bidi, html, "the direction character itself is not in the page");
    }

    /// <summary>
    /// An invalid address shows the error of the address and calls no store method.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageShowsAddressErrorWithoutQuerying()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?severity=bogus");

        // Act
        await harness.AddAsync<LogsPage>();

        // Assert
        Assert.Contains(Enc(Format(LogsView.AddressInvalidValueFormat, "severity")), harness.Html(), "the error names the parameter");
        Assert.IsEmpty(harness.Explorer.Calls, "no store call");
    }

    /// <summary>
    /// Rendered statically, as the prerender does, the page shows the filters and the waiting text and calls no store method.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageDoesNotQueryWhilePrerendering()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?q=x");

        // Act
        var html = await harness.RenderStaticAsync<LogsPage>();

        // Assert
        Assert.Contains("name=\"q\"", html, "the text filter");
        Assert.Contains("name=\"program\"", html, "the program filter");
        Assert.Contains(Enc(LogsView.Searching), html, "the waiting text");
        Assert.IsEmpty(harness.Explorer.Calls, "no store call while prerendering");
    }

    /// <summary>
    /// A refused search shows the rule of the store without its prefix.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageShowsTheRuleOfARefusedSearch()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?q=x");

        harness.Explorer.Failures[FakeLogExplorer.FindLogs] = new InvalidQueryException($"{RulePrefix}search text has no terms");

        // Act
        await harness.AddAsync<LogsPage>();

        var html = harness.Html();

        // Assert
        Assert.Contains(Enc(Format(LogsView.SearchRefusedFormat, "search text has no terms")), html, "the rule");
        Assert.DoesNotContain("store: invalid query", html, "no prefix of the store");
    }

    /// <summary>
    /// A search that is still running after the deadline is cancelled and the page says so.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageStopsASearchAtTheDeadline()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?q=x");

        harness.Explorer.Before = (method, token) => method == FakeLogExplorer.FindLogs ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;

        var add = harness.AddAsync<LogsPage>();

        await harness.Explorer.WaitForCallsAsync(FakeLogExplorer.FindLogs, 1);

        // Act
        harness.Clock.Advance(LogsView.Deadline);

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(harness.Explorer.Calls.Single(call => call.Method == FakeLogExplorer.FindLogs).CancellationToken.IsCancellationRequested, "the search token was cancelled");
        Assert.Contains(Enc(LogsView.SearchDeadline), harness.Html(), "the deadline text");
    }

    /// <summary>
    /// Any other failure of the store shows a fixed text without details and is logged with its exception.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageShowsAFailureWithoutDetails()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?q=x");

        var failure = new InvalidOperationException("secret detail of the store");

        harness.Explorer.Failures[FakeLogExplorer.FindLogs] = failure;

        // Act
        await harness.AddAsync<LogsPage>();

        var html = harness.Html();

        // Assert
        Assert.Contains(Enc(LogsView.LogsReadFailed), html, "the fixed text");
        Assert.DoesNotContain("secret detail", html, "no detail of the exception");
        Assert.Contains("logs view: reading failed", harness.Logger.Messages, "the message of the log method");
        Assert.Contains(exception => ReferenceEquals(exception, failure), harness.Logger.Exceptions, "the exception is logged");
        Assert.Contains(entry => entry.Level == LogLevel.Error, harness.Logger.Entries, "at level error");
    }

    /// <summary>
    /// Submitting the form navigates to the address of the entered values, and the page then searches what that address says.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageSearchNavigatesToTheAddress()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?q=old");

        var id = await harness.AddAsync<LogsPage>();

        var expected = new LogsAddress
                       {
                           Text = "new text",
                           Source = "syslog",
                           Program = "sshd",
                           Severity = 4,
                           From = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
                           To = new DateTimeOffset(2026, 10, 2, 11, 30, 0, TimeSpan.Zero)
                       };

        // Act
        await harness.EnterAsync(("q", "new text"), ("source", "syslog"), ("program", "sshd"), ("severity", "warning"), ("from", "2026-10-01T10:00"), ("to", "2026-10-02T11:30"));
        await harness.Renderer.SubmitAsync(harness.Form());

        var targets = harness.Navigation.Targets.ToArray();

        await harness.UpdateAsync();

        // Assert
        var filters = harness.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs);

        Assert.AreSequenceEqual([$"logs{expected.ToQuery()}"], targets, "one navigation to logs with the query of the entered values");
        Assert.HasCount(2, filters, "the first search and the search of the new address, and no search from the handler itself");
        Assert.AreEqual("new text", filters[1].Text, "text of the new address");
        Assert.AreEqual("syslog", filters[1].Source, "source of the new address");
        Assert.AreEqual("sshd", filters[1].Program, "program of the new address");
        Assert.AreEqual((byte)4, filters[1].MaxPriority, "severity of the new address");
        Assert.AreEqual(expected.From, filters[1].From, "from of the new address");
        Assert.AreEqual(expected.To, filters[1].To, "to of the new address");
        Assert.AreEqual(LogPageHarness.RootId, id, "the page is the root component");
    }

    /// <summary>
    /// Entered values that are not valid show the error and navigate nowhere.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageSubmitWithInvalidValuesShowsTheError()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?q=old");

        await harness.AddAsync<LogsPage>();

        const string withNul = "a\u0000b";

        // Act
        await harness.EnterAsync(("q", withNul));
        await harness.Renderer.SubmitAsync(harness.Form());

        // Assert
        Assert.AreEqual(0, withNul[1], "the entered text holds the control character");
        Assert.Contains(Enc(Format(LogsView.AddressInvalidValueFormat, "q")), harness.Html(), "the error of the address");
        Assert.IsEmpty(harness.Navigation.Targets, "no navigation");
        Assert.HasCount(1, harness.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs), "no further search");
    }

    /// <summary>
    /// A full page offers the next page, a page ended by the scan bound offers to continue, and the position is page state that adds no navigation.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageOffersNextPageAndContinue()
    {
        // Arrange
        var first = Position(Time.AddSeconds(1), 12);
        var second = Position(Time.AddMinutes(30), 90);

        await using var harness = new LogPageHarness($"{Address}?q=x");

        harness.Explorer.FindPages.Enqueue(Page(first, false, Line(11, Time, "one"), Line(12, Time.AddSeconds(1), "two")));
        harness.Explorer.FindPages.Enqueue(Page(second, true));
        harness.Explorer.FindPages.Enqueue(Page(null, false, Line(91, Time.AddHours(1), "three")));

        // Act
        await harness.AddAsync<LogsPage>();

        var onFirst = (Next: harness.Buttons("Next page").Count, Continue: harness.Buttons("Continue").Count);

        await harness.Renderer.ClickAsync(harness.Buttons("Next page")[0]);

        var onSecond = (Next: harness.Buttons("Next page").Count, Continue: harness.Buttons("Continue").Count, First: harness.Buttons("First page").Count, Html: harness.Html());

        await harness.Renderer.ClickAsync(harness.Buttons("Continue")[0]);

        var onThird = (Next: harness.Buttons("Next page").Count, Continue: harness.Buttons("Continue").Count);

        await harness.Renderer.ClickAsync(harness.Buttons("First page")[0]);

        // Assert
        var filters = harness.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs);

        Assert.AreEqual(1, onFirst.Next, "a full page offers Next page");
        Assert.AreEqual(0, onFirst.Continue, "a full page does not offer Continue");
        Assert.AreEqual(0, onSecond.Next, "a page ended by the scan bound does not offer Next page");
        Assert.AreEqual(1, onSecond.Continue, "a page ended by the scan bound offers Continue");
        Assert.AreEqual(1, onSecond.First, "the later page offers First page");
        Assert.Contains(Enc(Format(LogsView.SearchedUpToFormat, Shown(second.CapturedAt))), onSecond.Html, "the time up to which the search went");
        Assert.AreEqual(0, onThird.Next + onThird.Continue, "the last page offers neither");
        Assert.HasCount(4, filters, "four searches");
        Assert.IsNull(filters[0].After, "the first search starts at the beginning");
        Assert.AreEqual(first.CapturedAt, filters[1].After!.CapturedAt, "Next page continues at the time of Next");
        Assert.AreEqual(first.Id, filters[1].After!.Id, "Next page continues at the ID of Next");
        Assert.AreEqual(second.CapturedAt, filters[2].After!.CapturedAt, "Continue continues at the time of Next");
        Assert.AreEqual(second.Id, filters[2].After!.Id, "Continue continues at the ID of Next");
        Assert.IsNull(filters[3].After, "First page starts at the beginning");
        Assert.IsEmpty(harness.Navigation.Targets, "the position is page state: no navigation");
    }

    /// <summary>
    /// Expanding a hit shows the lines around it, and expanding another hit replaces them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageShowsTheContextOfOneHit()
    {
        // Arrange
        var firstHit = Line(11, Time, "hit one");
        var secondHit = Line(12, Time.AddSeconds(5), "hit two");

        await using var harness = new LogPageHarness($"{Address}?q=hit");

        harness.Explorer.FindPage = Page(null, false, firstHit, secondHit);
        harness.Explorer.ContextResult = new LogContext
                                         {
                                             Line = firstHit,
                                             Before = [Line(9, Time.AddSeconds(-2), "before-a"), Line(10, Time.AddSeconds(-1), "before-b")],
                                             After = [Line(20, Time.AddSeconds(1), "after-a")]
                                         };

        await harness.AddAsync<LogsPage>();

        // Act
        await harness.Renderer.ClickAsync(harness.Buttons("Context")[0]);

        var afterFirst = harness.Html();

        harness.Explorer.ContextResult = new LogContext
                                         {
                                             Line = secondHit,
                                             Before = [Line(30, Time.AddSeconds(4), "before-x")],
                                             After = [Line(31, Time.AddSeconds(6), "after-x")]
                                         };

        await harness.Renderer.ClickAsync(harness.Buttons("Context")[^1]);

        var afterSecond = harness.Html();

        // Assert
        var queries = harness.Explorer.Queries<LogContextQuery>(FakeLogExplorer.LogContext);

        Assert.HasCount(2, queries, "one context query per expansion");
        Assert.AreEqual(11L, queries[0].RecordId, "the first hit");
        Assert.AreEqual(5, queries[0].Before, "five lines before");
        Assert.AreEqual(5, queries[0].After, "five lines after");
        Assert.AreEqual(12L, queries[1].RecordId, "the second hit");
        Assert.Contains("before-a", afterFirst, "the line before the first hit");
        Assert.Contains("before-b", afterFirst, "the second line before the first hit");
        Assert.Contains("after-a", afterFirst, "the line after the first hit");
        Assert.Contains("before-x", afterSecond, "the line before the second hit");
        Assert.Contains("after-x", afterSecond, "the line after the second hit");
        Assert.DoesNotContain("before-a", afterSecond, "the context of the first hit is replaced");
    }

    /// <summary>
    /// With a template the page shows it above the lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageWithTemplateShowsTheTemplate()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?template=7");

        harness.Explorer.TemplateResult = Template(7, "user <ip> failed", 42);

        // Act
        await harness.AddAsync<LogsPage>();

        var html = harness.Html();

        // Assert
        Assert.Contains(Enc("user <ip> failed"), html, "the template text");
        Assert.Contains("syslog", html, "source");
        Assert.Contains("sshd", html, "program");
        Assert.Contains("2026-09-20", html, "first seen");
        Assert.Contains("2026-10-12", html, "last seen");
        Assert.Contains("42", html, "count");
        Assert.AreEqual(7L, harness.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs).Single().TemplateId, "the lines are searched by the template");
    }

    /// <summary>
    /// An unknown template says so and searches nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageWithUnknownTemplateSaysSo()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?template=7");

        harness.Explorer.TemplateResult = null;

        // Act
        await harness.AddAsync<LogsPage>();

        // Assert
        Assert.Contains(Enc(LogsView.TemplateUnknown), harness.Html(), "the text of an unknown template");
        Assert.IsEmpty(harness.Explorer.Queries<LogFilter>(FakeLogExplorer.FindLogs), "no search");
    }

    /// <summary>
    /// A new address while a search runs cancels the running search, and disposing the page cancels it too.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageCancelsTheRunningSearch()
    {
        // Arrange
        await using var changing = new LogPageHarness($"{Address}?q=first");
        await using var disposing = new LogPageHarness($"{Address}?q=first");

        changing.Explorer.Before = BlockFirstSearch(changing.Explorer);
        disposing.Explorer.Before = BlockFirstSearch(disposing.Explorer);

        var firstAdd = changing.AddAsync<LogsPage>();

        await changing.Explorer.WaitForCallsAsync(FakeLogExplorer.FindLogs, 1);

        // Act
        changing.Navigation.Move($"{Address}?q=second");

        await changing.UpdateAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await firstAdd.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        _ = disposing.AddAsync<LogsPage>();

        await disposing.Explorer.WaitForCallsAsync(FakeLogExplorer.FindLogs, 1);
        await disposing.DisposeAsync();

        // Assert
        var calls = changing.Explorer.Calls.Where(call => call.Method == FakeLogExplorer.FindLogs).ToArray();

        Assert.HasCount(2, calls, "the first search and the search of the new address");
        Assert.IsTrue(calls[0].CancellationToken.IsCancellationRequested, "the new address cancelled the running search");
        Assert.AreEqual("second", ((LogFilter)calls[1].Argument!).Text, "the second search is for the new address");
        Assert.IsTrue(disposing.Explorer.Calls.Single(call => call.Method == FakeLogExplorer.FindLogs).CancellationToken.IsCancellationRequested, "disposing the page cancelled the running search");
    }

    /// <summary>
    /// The page runs one store call at a time, in the order sources, template, lines, and a new action waits for the end of the cancelled call before it starts its own.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogsPageRunsOneStoreCallAtATime()
    {
        // Arrange
        await using var sequential = new LogPageHarness($"{Address}?template=3&q=x");
        await using var waiting = new LogPageHarness($"{Address}?q=first");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sequential.Explorer.TemplateResult = Template(3, "user failed", 5);
        sequential.Explorer.Before = async (_, _) => await Task.Yield();
        waiting.Explorer.Before = (method, _) => method == FakeLogExplorer.FindLogs && waiting.Explorer.Calls.Count(call => call.Method == FakeLogExplorer.FindLogs) == 1 ? release.Task : Task.CompletedTask;

        // Act
        await sequential.AddAsync<LogsPage>();

        var firstAdd = waiting.AddAsync<LogsPage>();

        await waiting.Explorer.WaitForCallsAsync(FakeLogExplorer.FindLogs, 1);

        waiting.Navigation.Move($"{Address}?q=second");

        var update = waiting.UpdateAsync();
        var cancelled = waiting.Explorer.Calls.Single(call => call.Method == FakeLogExplorer.FindLogs).CancellationToken.IsCancellationRequested;
        var startedWhileRunning = waiting.Explorer.Calls.Count(call => call.Method == FakeLogExplorer.FindLogs);

        release.SetResult();

        await Task.WhenAll(firstAdd, update).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([FakeLogExplorer.LogSources, FakeLogExplorer.Template, FakeLogExplorer.FindLogs], sequential.Explorer.Calls.Select(call => call.Method).ToArray(), "sources, template and lines one after another");
        Assert.AreEqual(1, sequential.Explorer.MaxInFlight, "never more than one call in flight");
        Assert.IsTrue(cancelled, "the new action cancelled the running call");
        Assert.AreEqual(1, startedWhileRunning, "the next call waits for the end of the cancelled one");
        Assert.AreEqual(1, waiting.Explorer.MaxInFlight, "never more than one call in flight, also across actions");
        Assert.AreEqual(2, waiting.Explorer.Calls.Count(call => call.Method == FakeLogExplorer.FindLogs), "the search of the new address ran after the first ended");
    }

    /// <summary>
    /// Creates a hook that blocks the first search until its token ends.
    /// </summary>
    /// <param name="explorer">The explorer that runs the hook</param>
    /// <returns>The hook</returns>
    private static Func<string, CancellationToken, Task> BlockFirstSearch(FakeLogExplorer explorer)
    {
        return (method, token) => method == FakeLogExplorer.FindLogs && explorer.Calls.Count(call => call.Method == FakeLogExplorer.FindLogs) == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex(">\\s*-\\s*<")]
    private static partial Regex DashCell();

    [GeneratedRegex("class=\"[^\"]*\\bescape\\b[^\"]*\">\\\\u202e<")]
    private static partial Regex EscapeElement();

    #endregion // Patterns
}