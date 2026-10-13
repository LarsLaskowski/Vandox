using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.Logs;
using Vandox.Storage;

using static Vandox.Backend.Tests.LogViewSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogTemplatesPage"/>. The tests read the page the way a browser shows it: the form has the fields source, program, from and to (by
/// <c>name</c>), and the link to the lines of the selected template has the text Show lines.
/// </summary>
[TestClass]
public partial class LogTemplatesPageTests
{
    #region Constants

    private const string Address = "http://localhost/logs/templates";

    private const string Range = "from=2026-10-01T10:00&to=2026-10-02T11:30";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _from = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _to = new(2026, 10, 2, 11, 30, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The page lists the templates of the address with their texts, sources, programs, first and last seen and counts, and asks the store with the filters of the address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageListsTemplatesWithFirstSeenAndCounts()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);

        await using var harness = new LogPageHarness($"{Address}?source=syslog&program=sshd&{Range}");

        harness.Explorer.TemplateList = [Counted(Template(1, "<b>user</b> failed", 987), 321), Counted(Template(2, $"cron{bidi} started", 654), 210)];

        // Act
        await harness.AddAsync<LogTemplatesPage>();

        var html = harness.Html();

        // Assert
        var query = harness.Explorer.Queries<TemplateListQuery>(FakeLogExplorer.Templates).Single();

        Assert.AreEqual("syslog", query.Source, "source");
        Assert.AreEqual("sshd", query.Program, "program");
        Assert.AreEqual(_from, query.From, "from");
        Assert.AreEqual(_to, query.To, "to");
        Assert.AreEqual(LogsView.TemplateRows, query.Limit, "limit");
        Assert.AreEqual(200, query.Limit, "limit is 200");
        Assert.Contains(Enc("<b>user</b> failed"), html, "the first template text appears as text");
        Assert.DoesNotContain("<b>user</b>", html, "no markup from a template text");
        Assert.IsTrue(EscapeElement().IsMatch(html), "U+202E in a template text is a visible escape");
        Assert.DoesNotContain(bidi, html, "the direction character itself is not in the page");
        Assert.Contains("2026-09-20", html, "first seen");
        Assert.Contains("2026-10-12", html, "last seen");
        Assert.Contains("987", html, "count of the first template");
        Assert.Contains("321", html, "count in range of the first template");
        Assert.Contains("654", html, "count of the second template");
        Assert.Contains("210", html, "count in range of the second template");
        Assert.IsEmpty(harness.Renderer.Elements(LogPageHarness.RootId).Where(element => element.Classes.Contains("chart")).ToArray(), "no chart without a selected template");
    }

    /// <summary>
    /// The link of the selected template leads to its lines, with the times of the address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageLinksATemplateToItsLines()
    {
        // Arrange
        var template = Template(7, "user failed", 987);

        await using var harness = new LogPageHarness($"{Address}?template=7&source=syslog&{Range}");

        harness.Explorer.TemplateResult = template;
        harness.Explorer.TemplateList = [Counted(template, 321)];

        // Act
        await harness.AddAsync<LogTemplatesPage>();

        var links = harness.Links("Show lines");

        // Assert
        var address = new LogsAddress
                      {
                          TemplateId = 7,
                          From = _from,
                          To = _to
                      };
        var expected = $"logs{address.ToQuery()}";

        Assert.HasCount(1, links, "one link to the lines");
        Assert.AreEqual(expected, links[0].Attributes["href"], "logs?template= plus the times of the address");
    }

    /// <summary>
    /// The page charts the selected template with the fixed title and series label, and no external text goes into the chart.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageChartsTheSelectedTemplate()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var template = Template(7, $"secretname{bidi} failed", 987);
        var hour = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        await using var harness = new LogPageHarness($"{Address}?template=7&source=syslog&{Range}");

        harness.Explorer.TemplateResult = template;
        harness.Explorer.TemplateList = [Counted(template, 321)];
        harness.Explorer.Hours = [hour, hour.AddHours(1), hour.AddHours(2)];
        harness.Explorer.TemplateHourList = [HourCount(hour, 3), HourCount(hour.AddHours(2), 5)];

        // Act
        await harness.AddAsync<LogTemplatesPage>();

        var html = harness.Html();
        var chart = harness.Renderer.Elements(LogPageHarness.RootId).Where(element => element.Classes.Contains("chart")).ToArray();
        var create = harness.Runtime.Calls.Where(call => call.Identifier == "create").ToArray();

        // Assert
        Assert.AreEqual(0x202e, char.ConvertToUtf32(bidi, 0), "the template text holds U+202E");
        Assert.IsTrue(EscapeElement().IsMatch(html), "the template text above the chart shows the escape");
        Assert.HasCount(1, chart, "one chart");
        Assert.AreEqual("Lines per hour", chart[0].Attributes["aria-label"], "the chart is labelled with the fixed title only");
        Assert.HasCount(1, create, "the chart was created");
        Assert.AreEqual(LogsView.ChartTitle, create[0].Arguments[1], "the title passed to the chart");
        Assert.AreSequenceEqual([LogsView.ChartSeries], (string[])create[0].Arguments[2]!, "the one series label");
        Assert.DoesNotContain(call => call.Arguments.Any(argument => argument is string text && text.Contains("secretname", StringComparison.Ordinal)), harness.Runtime.Calls, "no template text reaches the chart script");
    }

    /// <summary>
    /// For a range longer than 2,160 hours, the hourly queries ask for the last hours only, with one hour more than the chart shows.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageQueriesTheLastHoursOfALongRange()
    {
        // Arrange
        var template = Template(7, "user failed", 987);
        var to = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var from = to.AddHours(-5000);

        await using var longRange = new LogPageHarness($"{Address}?template=7&source=syslog&from={Uri.EscapeDataString(from.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture))}");
        await using var shortRange = new LogPageHarness($"{Address}?template=7&source=syslog&{Range}");

        foreach (var harness in new[] { longRange, shortRange })
        {
            harness.Explorer.TemplateResult = template;
            harness.Explorer.TemplateList = [Counted(template, 321)];
        }

        // Act
        await longRange.AddAsync<LogTemplatesPage>();
        await shortRange.AddAsync<LogTemplatesPage>();

        // Assert
        var hours = longRange.Explorer.Queries<TemplateHourQuery>(FakeLogExplorer.TemplateHours).Single();
        var sourceHours = longRange.Explorer.Queries<SourceHourQuery>(FakeLogExplorer.SourceHours).Single();
        var shortHours = shortRange.Explorer.Queries<TemplateHourQuery>(FakeLogExplorer.TemplateHours).Single();
        var window = to.AddHours(-TemplateHourSeries.MaxHours);

        Assert.AreEqual(7L, hours.TemplateId, "the template");
        Assert.AreEqual(window, hours.From, "from is the end minus 2160 hours");
        Assert.AreEqual(to, hours.To, "to is the end of the address");
        Assert.AreEqual(TemplateHourSeries.MaxHours + 1, hours.Limit, "limit is 2161");
        Assert.AreEqual(2161, hours.Limit, "limit is 2161 hours");
        Assert.AreEqual(window, sourceHours.From, "source hours from");
        Assert.AreEqual(to, sourceHours.To, "source hours to");
        Assert.AreEqual(2161, sourceHours.Limit, "source hours limit");
        Assert.AreEqual(_from, shortHours.From, "a short range keeps the from of the address");
        Assert.AreEqual(_to, shortHours.To, "a short range keeps the to of the address");
    }

    /// <summary>
    /// An invalid address, a refused query, a deadline and a failure each show their fixed text, and only the last one is logged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageShowsErrors()
    {
        // Arrange
        await using var invalid = new LogPageHarness($"{Address}?from=nonsense");
        await using var refused = new LogPageHarness($"{Address}?{Range}");
        await using var slow = new LogPageHarness($"{Address}?{Range}");
        await using var failing = new LogPageHarness($"{Address}?{Range}");

        refused.Explorer.Failures[FakeLogExplorer.Templates] = new InvalidQueryException($"{RulePrefix}from must be before to");
        slow.Explorer.Before = (method, token) => method == FakeLogExplorer.Templates ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;

        var failure = new InvalidOperationException("secret detail of the store");

        failing.Explorer.Failures[FakeLogExplorer.Templates] = failure;

        // Act
        await invalid.AddAsync<LogTemplatesPage>();
        await refused.AddAsync<LogTemplatesPage>();

        var running = slow.AddAsync<LogTemplatesPage>();

        await slow.Explorer.WaitForCallsAsync(FakeLogExplorer.Templates, 1);

        slow.Clock.Advance(LogsView.Deadline);

        await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await failing.AddAsync<LogTemplatesPage>();

        // Assert
        Assert.Contains(Enc(Format(LogsView.AddressInvalidValueFormat, "from")), invalid.Html(), "the address error");
        Assert.IsEmpty(invalid.Explorer.Calls, "an invalid address calls no store method");
        Assert.Contains(Enc(Format(LogsView.SearchRefusedFormat, "from must be before to")), refused.Html(), "the rule of a refused query");
        Assert.DoesNotContain("store: invalid query", refused.Html(), "no prefix of the store");
        Assert.Contains(Enc(LogsView.QueryDeadline), slow.Html(), "the deadline text");
        Assert.Contains(Enc(LogsView.TemplatesReadFailed), failing.Html(), "the fixed failure text");
        Assert.DoesNotContain("secret detail", failing.Html(), "no detail of the exception");
        Assert.Contains(exception => ReferenceEquals(exception, failure), failing.Logger.Exceptions, "the exception is logged");
        Assert.Contains(entry => entry.Level == LogLevel.Error, failing.Logger.Entries, "at level error");
        Assert.Contains("logs view: reading failed", failing.Logger.Messages, "the message of the log method");
    }

    /// <summary>
    /// Rendered statically, as the prerender does, the page shows its filters and calls no store method.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageDoesNotQueryWhilePrerendering()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?source=syslog");

        // Act
        var html = await harness.RenderStaticAsync<LogTemplatesPage>();

        // Assert
        Assert.Contains("name=\"source\"", html, "the source filter");
        Assert.Contains("name=\"program\"", html, "the program filter");
        Assert.IsEmpty(harness.Explorer.Calls, "no store call while prerendering");
    }

    /// <summary>
    /// The page runs one store call at a time, also across actions.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageRunsOneStoreCallAtATime()
    {
        // Arrange
        var template = Template(7, "user failed", 987);

        await using var sequential = new LogPageHarness($"{Address}?template=7&source=syslog&{Range}");
        await using var waiting = new LogPageHarness($"{Address}?{Range}");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sequential.Explorer.TemplateResult = template;
        sequential.Explorer.TemplateList = [Counted(template, 321)];
        sequential.Explorer.Hours = [_from];
        sequential.Explorer.Before = async (_, _) => await Task.Yield();
        waiting.Explorer.Before = (method, _) => method == FakeLogExplorer.Templates && waiting.Explorer.Calls.Count(call => call.Method == FakeLogExplorer.Templates) == 1 ? release.Task : Task.CompletedTask;

        // Act
        await sequential.AddAsync<LogTemplatesPage>();

        var firstAdd = waiting.AddAsync<LogTemplatesPage>();

        await waiting.Explorer.WaitForCallsAsync(FakeLogExplorer.Templates, 1);

        waiting.Navigation.Move($"{Address}?source=syslog&{Range}");

        var update = waiting.UpdateAsync();
        var cancelled = waiting.Explorer.Calls.Single(call => call.Method == FakeLogExplorer.Templates).CancellationToken.IsCancellationRequested;
        var startedWhileRunning = waiting.Explorer.Calls.Count(call => call.Method == FakeLogExplorer.Templates);

        release.SetResult();

        await Task.WhenAll(firstAdd, update).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsGreaterThanOrEqualTo(3, sequential.Explorer.Calls.Count, "the list, the template and its hours were read");
        Assert.Contains(call => call.Method == FakeLogExplorer.TemplateHours, sequential.Explorer.Calls, "the hourly counts of the template were read, in the same sequence");
        Assert.AreEqual(1, sequential.Explorer.MaxInFlight, "never more than one call in flight");
        Assert.IsTrue(cancelled, "the new action cancelled the running call");
        Assert.AreEqual(1, startedWhileRunning, "the next call waits for the end of the cancelled one");
        Assert.AreEqual(1, waiting.Explorer.MaxInFlight, "never more than one call in flight, also across actions");
        Assert.AreEqual(2, waiting.Explorer.Calls.Count(call => call.Method == FakeLogExplorer.Templates), "the query of the new address ran after the first ended");
    }

    /// <summary>
    /// The column of the count in the range says that whole hours are counted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageLabelsTheRangeCountAsWholeHours()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?{Range}");

        harness.Explorer.TemplateList = [Counted(Template(1, "user failed", 987), 321)];

        // Act
        await harness.AddAsync<LogTemplatesPage>();

        // Assert
        Assert.Contains("Lines in range (whole hours)", harness.Html(), "the header text of the plan");
        Assert.Contains(Enc(LogsView.CountInRangeHeader), harness.Html(), "the column header");
    }

    /// <summary>
    /// Submitting the form navigates to the address of the entered values, and the page then lists what that address says.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageSubmitNavigatesToTheAddress()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?program=old");

        await harness.AddAsync<LogTemplatesPage>();

        var expected = new LogsAddress
                       {
                           Source = "syslog",
                           Program = "sshd",
                           From = _from,
                           To = _to
                       };

        // Act
        await harness.EnterAsync(("source", "syslog"), ("program", "sshd"), ("from", "2026-10-01T10:00"), ("to", "2026-10-02T11:30"));
        await harness.Renderer.SubmitAsync(harness.Form());

        var targets = harness.Navigation.Targets.ToArray();

        await harness.UpdateAsync();

        // Assert
        var queries = harness.Explorer.Queries<TemplateListQuery>(FakeLogExplorer.Templates);

        Assert.AreSequenceEqual([$"logs/templates{expected.ToQuery()}"], targets, "one navigation to the templates with the query of the entered values");
        Assert.HasCount(2, queries, "the first listing and the listing of the new address");
        Assert.AreEqual("syslog", queries[1].Source, "source of the new address");
        Assert.AreEqual("sshd", queries[1].Program, "program of the new address");
        Assert.AreEqual(_from, queries[1].From, "from of the new address");
        Assert.AreEqual(_to, queries[1].To, "to of the new address");
    }

    /// <summary>
    /// Entered values that are not valid show the error and navigate nowhere.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageSubmitWithInvalidValuesShowsTheError()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?program=old");

        await harness.AddAsync<LogTemplatesPage>();

        // Act
        await harness.EnterAsync(("from", "nonsense"));
        await harness.Renderer.SubmitAsync(harness.Form());

        // Assert
        Assert.Contains(Enc(Format(LogsView.AddressInvalidValueFormat, "from")), harness.Html(), "the error of the address");
        Assert.IsEmpty(harness.Navigation.Targets, "no navigation");
        Assert.HasCount(1, harness.Explorer.Queries<TemplateListQuery>(FakeLogExplorer.Templates), "no further listing");
    }

    /// <summary>
    /// A navigation to another page is not listed, and once the page is disposed the navigation manager no longer reaches it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageDetachesTheLocationHandlerOnDispose()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?program=one");

        await harness.AddAsync<LogTemplatesPage>();

        // Act
        harness.Navigation.NavigateTo("logs/templates-more?program=other");
        harness.Navigation.NavigateTo("logs?program=other");

        var otherPages = harness.Explorer.Queries<TemplateListQuery>(FakeLogExplorer.Templates).Count;

        harness.Navigation.NavigateTo("logs/templates?program=two#top");

        var thisPage = harness.Explorer.Queries<TemplateListQuery>(FakeLogExplorer.Templates).Count;

        await harness.Renderer.DisposeAllAsync();

        harness.Navigation.NavigateTo("logs/templates?program=three");

        var afterDispose = harness.Explorer.Queries<TemplateListQuery>(FakeLogExplorer.Templates).Count;

        // Assert
        Assert.AreEqual(1, otherPages, "other pages, also one that starts with the path, are not listed");
        Assert.AreEqual(2, thisPage, "the address of this page is listed");
        Assert.AreEqual(2, afterDispose, "after the dispose no handler reaches the page");
    }

    /// <summary>
    /// An unknown template says so and reads no hourly counts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageWithUnknownTemplateSaysSo()
    {
        // Arrange
        await using var harness = new LogPageHarness($"{Address}?template=7&{Range}");

        harness.Explorer.TemplateResult = null;

        // Act
        await harness.AddAsync<LogTemplatesPage>();

        // Assert
        Assert.Contains(Enc(LogsView.TemplateUnknown), harness.Html(), "the text of an unknown template");
        Assert.IsEmpty(harness.Explorer.Queries<TemplateHourQuery>(FakeLogExplorer.TemplateHours), "no hourly counts of an unknown template");
    }

    /// <summary>
    /// A range that starts after its end reads no hourly counts and shows the template without a chart.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesPageWithEmptyHourRangeShowsNoChart()
    {
        // Arrange
        var template = Template(7, "user failed", 987);

        await using var harness = new LogPageHarness($"{Address}?template=7&from=2030-01-01T00:00");

        harness.Explorer.TemplateResult = template;
        harness.Explorer.TemplateList = [Counted(template, 0)];

        // Act
        await harness.AddAsync<LogTemplatesPage>();

        // Assert
        Assert.Contains("Template 7", harness.Html(), "the template is shown");
        Assert.IsEmpty(harness.Explorer.Queries<TemplateHourQuery>(FakeLogExplorer.TemplateHours), "no hourly counts for an empty range");
        Assert.IsEmpty(harness.Renderer.Elements(LogPageHarness.RootId).Where(element => element.Classes.Contains("chart")).ToArray(), "no chart");
    }

    /// <summary>
    /// Creates the count of a template in an hour.
    /// </summary>
    /// <param name="hour">The start of the hour</param>
    /// <param name="count">The number of lines</param>
    /// <returns>The template hour</returns>
    private static TemplateHour HourCount(DateTimeOffset hour, long count)
    {
        return new TemplateHour
               {
                   Hour = hour,
                   Count = count
               };
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("class=\"[^\"]*\\bescape\\b[^\"]*\">\\\\u202e<")]
    private static partial Regex EscapeElement();

    #endregion // Patterns
}