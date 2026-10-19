using System.Net;
using System.Text.Encodings.Web;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.Reports;
using Vandox.Core.Signatures;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportPage"/>. The tests read the page the way a browser shows it: links by their address, buttons by their text, the report by its pieces.
/// </summary>
[TestClass]
public class ReportPageTests
{
    #region Constants

    private const string Address = "http://localhost/report";

    private const string InvalidLanguage = "The address has an invalid value for the parameter lang.";

    private const string RepeatedLanguage = "The address gives the parameter lang more than once.";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The page shows the report of the store: its pieces are those of the Markdown that the reporter writes for the same store, clock and language.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task ReportPageShowsTheReportOfTheStore(string language)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await IncidentSamples.WriteAsync(store,
                                         TestContext.CancellationToken,
                                         IncidentSamples.Line(IncidentSamples.Base.AddHours(-3), "cron", "job started"),
                                         IncidentSamples.Line(IncidentSamples.Base.AddHours(-2), "kernel", "Out of memory"),
                                         IncidentSamples.Boot(IncidentSamples.Base.AddHours(-1)),
                                         IncidentSamples.Processes(IncidentSamples.Base.AddMinutes(-30), ("mariadbd", 300 * Mib), ("pzstd", 100 * Mib)),
                                         IncidentSamples.Processes(IncidentSamples.Base.AddMinutes(-20), ("mariadbd", 310 * Mib)));
        await SaveOutagesAsync(store, 3);

        await using var harness = new ReportPageHarness($"{Address}?lang={language}", store, store);

        var text = ReportText.For(language);

        Assert.IsNotNull(text, "the language has a table");

        var markdown = new StringWriter();

        await new ForensicsReporter(store, store, store, BuiltInSignatures.Catalog, TimeSpan.FromHours(1), new FakeTimeProvider(harness.Clock.GetUtcNow())).RunAsync(new MarkdownReportWriter(markdown, text), TestContext.CancellationToken);

        // Act
        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var expected = ReportPieces.FromMarkdown(markdown.ToString());
        var actual = harness.Pieces();

        Assert.IsGreaterThan(20, expected.Count, "the Markdown has pieces");
        Assert.HasCount(expected.Count, actual, "the number of pieces");

        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"piece {index} of the report in {language}");
        }

        Assert.Contains($"<h1>{ReportsView.Title}</h1>", harness.Html(), "the title of the page");
    }

    /// <summary>
    /// An address that is refused shows its fixed text, no report and no status of a run, and reads nothing.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="error">The text that is shown</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("?lang=DE", InvalidLanguage)]
    [DataRow("?lang=de-DE", InvalidLanguage)]
    [DataRow("?lang=", InvalidLanguage)]
    [DataRow("?lang=en&lang=de", RepeatedLanguage)]
    [DataRow("?lang=en&LANG=en", RepeatedLanguage)]
    public async Task ReportPageShowsTheAddressErrorAndRunsNothing(string query, string error)
    {
        // Arrange
        await using var harness = new ReportPageHarness(Address + query);

        // Act
        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var html = harness.Html();

        Assert.Contains(Enc(error), html, "the text of the refusal");
        Assert.DoesNotContain(Enc(ReportsView.Running), html, "no run is announced");
        Assert.IsEmpty(harness.Pieces(), "no report is shown");
        Assert.IsEmpty(harness.Records.Queries, "nothing is read");
        Assert.IsEmpty(harness.Logger.Messages, "a refused address is not logged");
    }

    /// <summary>
    /// The language is read from the query and not from the fragment of the address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageReadsTheLanguageFromTheQueryAndNotFromTheFragment()
    {
        // Arrange
        await using var harness = new ReportPageHarness($"{Address}?lang=de#lang=fr");

        // Act
        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.Contains("Vandox-Forensikbericht", harness.Html(), "the German title of the report");
        Assert.DoesNotContain(Enc(InvalidLanguage), harness.Html(), "the fragment is not read");
    }

    /// <summary>
    /// A new language in the address runs the report again, in that language.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageRunsAgainWhenTheLanguageChanges()
    {
        // Arrange
        await using var harness = new ReportPageHarness(Address);

        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var englishReads = harness.LogReads();
        var englishHtml = harness.Html();

        // Act
        harness.Navigation.NavigateTo("report?lang=de");

        await harness.WaitForHtmlAsync(html => html.Contains("Vandox-Forensikbericht", StringComparison.Ordinal)).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, englishReads, "one run for the first address");
        Assert.Contains("Vandox forensics report", englishHtml, "English without the parameter");
        Assert.AreEqual(2, harness.LogReads(), "the second language runs again");
    }

    /// <summary>
    /// The run ends at the deadline, not before: the token of the store call ends, the fixed text is shown and no report is.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageStopsTheRunAtTheDeadline()
    {
        // Arrange
        await using var harness = new ReportPageHarness(Address);

        harness.Records.Before = (_, token) => Task.Delay(Timeout.Infinite, token);

        var add = harness.AddAsync<ReportPage>();

        await harness.Records.WaitForCallsAsync(1);

        // Act
        harness.Clock.Advance(ReportsView.Deadline - TimeSpan.FromMilliseconds(1));

        var justBefore = harness.Html();
        var tokenJustBefore = harness.Records.Tokens[0].IsCancellationRequested;

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));

        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.DoesNotContain(Enc(ReportsView.RunDeadline), justBefore, "no deadline text before the deadline");
        Assert.IsFalse(tokenJustBefore, "the store call is open before the deadline");
        Assert.IsTrue(harness.Records.Tokens[0].IsCancellationRequested, "the store call in flight is cancelled at the deadline");
        Assert.Contains(Enc(ReportsView.RunDeadline), harness.Html(), "the text of the deadline");
        Assert.IsEmpty(harness.Pieces(), "no report is shown");
        Assert.IsEmpty(harness.Logger.Messages, "the deadline is not logged");
    }

    /// <summary>
    /// A failure of the store shows the fixed text without its details and is logged with the exception only.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageShowsAFailureAndLogsIt()
    {
        // Arrange
        await using var harness = new ReportPageHarness(Address);

        var failure = new StoreException("store: reading records: secret detail of the store");

        harness.Records.Failure = failure;

        // Act
        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var html = harness.Html();
        var logged = harness.Logger.Entries.Select((entry, index) => (entry, index)).Where(item => item.entry.EventId.Id == 52).ToArray();

        Assert.Contains(Enc(ReportsView.RunFailure), html, "the fixed text");
        Assert.DoesNotContain("secret detail", html, "no detail of the store on the page");
        Assert.IsEmpty(harness.Pieces(), "no report is shown");
        Assert.HasCount(1, logged, "one entry with the event ID 52");
        Assert.AreEqual(LogLevel.Error, logged[0].entry.Level, "logged as an error");
        Assert.AreEqual("report page: making the report failed", harness.Logger.Messages[logged[0].index], "the message");
        Assert.AreSame(failure, harness.Logger.Exceptions[logged[0].index], "the exception only");
    }

    /// <summary>
    /// With more than 50 outages the page shows the first 50, the next outages run again and show the last one, and the first outages go back; the position is not in the address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageShowsTheNextOutagesAfterFifty()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveOutagesAsync(store, 51);

        await using var harness = new ReportPageHarness(Address, store, store);

        // Act
        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var first = harness.Pieces();
        var firstHtml = harness.Html();
        var nextOnFirst = harness.Actions(ReportsView.NextOutages);
        var firstOnFirst = harness.Actions(ReportsView.FirstOutages);

        await harness.Renderer.ClickAsync(nextOnFirst.Single());
        await harness.WaitForHtmlAsync(html => html.Contains(Enc("Outages 51 to 51 of 51."), StringComparison.Ordinal)).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var last = harness.Pieces();
        var nextOnLast = harness.Actions(ReportsView.NextOutages);
        var firstOnLast = harness.Actions(ReportsView.FirstOutages);

        await harness.Renderer.ClickAsync(firstOnLast.Single());
        await harness.WaitForHtmlAsync(html => html.Contains(Enc("Outages 1 to 50 of 51."), StringComparison.Ordinal)).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.Contains(Enc("Outages 1 to 50 of 51."), firstHtml, "the range of the first page");
        Assert.Contains(piece => piece.StartsWith("Outage #50 at", StringComparison.Ordinal), first, "outage 50 is on the first page");
        Assert.DoesNotContain(piece => piece.StartsWith("Outage #51 at", StringComparison.Ordinal), first, "outage 51 is not on the first page");
        Assert.HasCount(1, nextOnFirst, "the first page offers the next outages");
        Assert.IsEmpty(firstOnFirst, "the first page offers no way back");
        Assert.Contains(piece => piece.StartsWith("Outage #51 at", StringComparison.Ordinal), last, "outage 51 is on the next page");
        Assert.DoesNotContain(piece => piece.StartsWith("Outage #1 at", StringComparison.Ordinal), last, "outage 1 is not on the next page");
        Assert.IsEmpty(nextOnLast, "the last page offers no next outages");
        Assert.Contains(Enc("Outages 1 to 50 of 51."), harness.Html(), "the first outages are shown again");
        Assert.Contains(piece => piece.StartsWith("Outage #1 at", StringComparison.Ordinal), harness.Pieces(), "outage 1 is back");
        Assert.IsEmpty(harness.Navigation.Targets, "the position is not in the address");
        Assert.AreEqual(Address, harness.Navigation.Uri, "the address is unchanged");
    }

    /// <summary>
    /// When the stored outages shrink to a few while the second page is shown, the page offers no outage and no range but the way back, and the first outages show the few that are left.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageOffersTheFirstOutagesWhenTheOutagesShrink()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveOutagesAsync(store, 51);

        var shrinking = new ShrinkableIncidentStore(store);

        await using var harness = new ReportPageHarness(Address, store, shrinking);

        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        shrinking.LastId = 3;

        // Act
        await harness.Renderer.ClickAsync(harness.Actions(ReportsView.NextOutages).Single());
        await harness.WaitForHtmlAsync(html => html.Contains(Enc(ReportsView.FirstOutages), StringComparison.Ordinal))
                     .WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        var shrunkPieces = harness.Pieces();
        var shrunkHtml = harness.Html();
        var shrunkNext = harness.Actions(ReportsView.NextOutages);
        var shrunkFirst = harness.Actions(ReportsView.FirstOutages);

        await harness.Renderer.ClickAsync(shrunkFirst.Single());
        await harness.WaitForHtmlAsync(html => html.Contains(Enc("Outage #3 at"), StringComparison.Ordinal))
                     .WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        var back = harness.Pieces();

        // Assert
        Assert.DoesNotContain(piece => piece.StartsWith("Outage #", StringComparison.Ordinal), shrunkPieces, "no outage is listed on the second page");
        Assert.DoesNotContain("Outages 51 to", shrunkHtml, "no range line");
        Assert.IsEmpty(shrunkNext, "no next outages");
        Assert.HasCount(1, shrunkFirst, "the first outages are offered");
        Assert.HasCount(3, back.Where(piece => piece.StartsWith("Outage #", StringComparison.Ordinal)).ToList(), "the three outages that are left");
        Assert.IsEmpty(harness.Actions(ReportsView.NextOutages), "no pager next");
        Assert.IsEmpty(harness.Actions(ReportsView.FirstOutages), "no pager first");
    }

    /// <summary>
    /// The page links both languages and the download of the language that is shown, and the download link carries the download attribute.
    /// </summary>
    /// <param name="language">The language of the address</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task ReportPageLinksTheLanguagesAndTheDownload(string language)
    {
        // Arrange
        await using var harness = new ReportPageHarness($"{Address}?lang={language}");

        var other = language == "en" ? "de" : "en";

        // Act
        await harness.AddAsync<ReportPage>().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var download = harness.Links($"report/markdown?lang={language}");

        Assert.HasCount(1, harness.Links("report?lang=en"), "the link to English");
        Assert.AreEqual("English", harness.Links("report?lang=en").Single().Text.Trim(), "its text");
        Assert.HasCount(1, harness.Links("report?lang=de"), "the link to German");
        Assert.AreEqual("Deutsch", harness.Links("report?lang=de").Single().Text.Trim(), "its text");
        Assert.HasCount(1, download, "the download of the language shown");
        Assert.AreEqual(ReportsView.Download, download.Single().Text.Trim(), "its text");
        Assert.IsTrue(download.Single().Attributes.ContainsKey("download"), "the download attribute");
        Assert.IsEmpty(harness.Links($"report/markdown?lang={other}"), "no download of the other language");
    }

    /// <summary>
    /// Rendered statically, as the prerender does, the page shows its title and the status of the run and reads nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPagePrerenderedShowsTheStatusAndRunsNothing()
    {
        // Arrange
        await using var harness = new ReportPageHarness(Address);

        // Act
        var html = await harness.RenderStaticAsync<ReportPage>();

        // Assert
        Assert.Contains($"<h1>{ReportsView.Title}</h1>", html, "the title");
        Assert.Contains(Enc(ReportsView.Running), html, "the status of the run");
        Assert.IsEmpty(ReportPieces.FromHtml(html), "no report");
        Assert.IsEmpty(harness.Records.Queries, "nothing is read while prerendering");
    }

    /// <summary>
    /// An anonymous request for the page is redirected to the login; with a session the page is served, prerendered.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ReportPageNeedsASession()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);

        // Act
        using var anonymous = await client.GetAsync("/report", TestContext.CancellationToken);

        using (var signIn = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.Found, signIn.StatusCode, "the sign-in is answered with a redirect");
        }

        using var page = await client.GetAsync("/report", TestContext.CancellationToken);

        var text = await page.Content.ReadAsStringAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.Found, anonymous.StatusCode, "the anonymous page is redirected");
        Assert.AreEqual("/login", LoginTestClient.LocationOf(anonymous)?.Split('?')[0], "to the login");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, "the page with a session");
        Assert.Contains(ReportsView.Title, text, "the title of the page");
    }

    /// <summary>
    /// Disposing the page cancels the run in the store.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportPageCancelsTheRunWhenDisposed()
    {
        // Arrange
        await using var harness = new ReportPageHarness(Address);

        harness.Records.Before = (_, token) => Task.Delay(Timeout.Infinite, token);

        _ = harness.AddAsync<ReportPage>();

        await harness.Records.WaitForCallsAsync(1);

        var token = harness.Records.Tokens[0];
        var openBefore = token.IsCancellationRequested;

        // Act
        await harness.Renderer.DisposeAllAsync();

        // Assert
        Assert.IsFalse(openBefore, "the store call is open while the page is shown");
        Assert.IsTrue(token.IsCancellationRequested, "disposing the page cancelled the store call");
    }

    /// <summary>
    /// Encodes a text as the renderer does.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The encoded text</returns>
    private static string Enc(string text)
    {
        return HtmlEncoder.Default.Encode(text);
    }

    /// <summary>
    /// Saves outages in the order of their numbers, one day apart.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="count">The number of outages</param>
    /// <returns>A task that completes when the outages are saved</returns>
    private async Task SaveOutagesAsync(SqliteStore store, int count)
    {
        for (var id = 1; id <= count; id++)
        {
            var saved = await store.SaveIncidentAsync(Outage(IncidentSamples.Base.AddDays(id), 1, "mariadbd"), IncidentSamples.Base, TestContext.CancellationToken);

            Assert.AreEqual(IncidentSaveOutcome.Created, saved.Outcome, "the incident is created");
        }
    }

    #endregion // Methods
}