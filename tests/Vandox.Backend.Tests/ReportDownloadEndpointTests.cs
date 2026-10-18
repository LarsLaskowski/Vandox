using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Hosting;
using Vandox.Backend.Reports;
using Vandox.Core.Signatures;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportDownloadEndpoint"/>: the handler with a request context and a scripted store, and the pipeline of the running service for the session, the listener and the output.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class ReportDownloadEndpointTests
{
    #region Constants

    private const string Path = "/report/markdown";

    private static readonly DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The body of the download of the running service is, byte for byte, standard output of the report command for the same database, clock and language, in UTF-8 without a byte order mark, with the headers of a download that is not stored.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task ReportDownloadEndpointWritesTheOutputOfTheReportCommand(string language)
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var clock = new FakeTimeProvider(_now);

        await using (var seeding = await SqliteStore.OpenAsync(fixture.Storage, TestContext.CancellationToken))
        {
            await IncidentSamples.WriteAsync(seeding, TestContext.CancellationToken, IncidentSamples.Kill(IncidentSamples.Base), IncidentSamples.Processes(IncidentSamples.Base.AddMinutes(-20), ("mariadbd", 300 * Mib)));
        }

        Assert.AreEqual(0, await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken), "the incidents are rebuilt first");
        fixture.Out.GetStringBuilder().Clear();
        Assert.AreEqual(0,
                        await fixture.RunAsync(["report", "-lang", language],
                                               new ServeHooks
                                               {
                                                   Clock = clock
                                               },
                                               TestContext.CancellationToken),
                        "the command succeeds");

        var expected = fixture.Out.ToString();

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken, clock);

        using var client = new LoginTestClient(fixture.WebPort);

        await SignInAsync(client);

        // Act
        using var response = await client.GetAsync($"{Path}?lang={language}", TestContext.CancellationToken);

        var body = await response.Content.ReadAsByteArrayAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "status");
        Assert.AreEqual(expected, Encoding.UTF8.GetString(body), "the text of the report command");
        Assert.AreSequenceEqual(new UTF8Encoding(false).GetBytes(expected), body, "the bytes, without a byte order mark");
        Assert.AreEqual("text/markdown; charset=utf-8", string.Join(",", response.Content.Headers.GetValues("Content-Type")), "content type");
        Assert.AreEqual($"attachment; filename=\"vandox-report-{language}.md\"", string.Join(",", response.Content.Headers.GetValues("Content-Disposition")), "content disposition");
        Assert.AreEqual("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")), "cache control");
        Assert.AreEqual("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")), "the type is not sniffed");
    }

    /// <summary>
    /// An anonymous request for the download is redirected to the login; with a session it is served.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportDownloadEndpointNeedsASession()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);

        // Act
        using var anonymousDownload = await client.GetAsync($"{Path}?lang=en", TestContext.CancellationToken);

        await SignInAsync(client);

        using var download = await client.GetAsync($"{Path}?lang=en", TestContext.CancellationToken);

        var downloadText = await download.Content.ReadAsStringAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.Found, anonymousDownload.StatusCode, "the anonymous download is redirected");
        Assert.AreEqual("/login", LoginTestClient.LocationOf(anonymousDownload)?.Split('?')[0], "to the login");
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode, "the download with a session");
        Assert.StartsWith("# Vandox forensics report", downloadText, "the report");
    }

    /// <summary>
    /// Neither the page nor the download exists on the ingest port, while the web port serves them to a signed-in client.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportDownloadEndpointAnswers404OnTheIngestListener()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);
        using var ingest = new HttpClient(new HttpClientHandler
                                          {
                                              AllowAutoRedirect = false
                                          })
                           {
                               Timeout = TimeSpan.FromSeconds(10)
                           };

        await SignInAsync(client);

        // Act
        using var web = await client.GetAsync($"{Path}?lang=en", TestContext.CancellationToken);
        using var ingestDownload = await ingest.GetAsync($"http://127.0.0.1:{fixture.IngestPort}{Path}?lang=en", TestContext.CancellationToken);
        using var ingestPage = await ingest.GetAsync($"http://127.0.0.1:{fixture.IngestPort}/report", TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, web.StatusCode, "the download exists on the web port");
        Assert.AreEqual(HttpStatusCode.NotFound, ingestDownload.StatusCode, "no download on the ingest port");
        Assert.AreEqual(HttpStatusCode.NotFound, ingestPage.StatusCode, "no page on the ingest port");
    }

    /// <summary>
    /// A language that is refused is answered with 400 and no body before anything is read from the store.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("?lang=DE")]
    [DataRow("?lang=en&lang=de")]
    [DataRow("?lang=")]
    public async Task ReportDownloadEndpointRefusesAnInvalidLanguage(string query)
    {
        // Arrange
        var records = new ScriptedRecordReader();
        var (context, response, _) = Request(query);

        using var service = Service(records, new ScriptedIncidentStore([], false));

        // Act
        await ReportDownloadEndpoint.DownloadAsync(context, service, new FakeTimeProvider(_now), Factory(new ListLogger()));

        // Assert
        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode, "bad request");
        Assert.AreEqual(0L, response.Length, "no body");
        Assert.IsEmpty(records.Queries, "no store call");
    }

    /// <summary>
    /// A run that is still reading at the deadline is cancelled in the store and answered with 503 and no body, when nothing was written.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportDownloadEndpointAnswers503AtTheDeadline()
    {
        // Arrange
        var clock = new FakeTimeProvider(_now);
        var records = new ScriptedRecordReader
                      {
                          Before = (_, token) => Task.Delay(Timeout.Infinite, token)
                      };
        var logger = new ListLogger();
        var (context, response, lifetime) = Request("?lang=en");

        using var service = Service(records, new ScriptedIncidentStore([], false));

        var download = ReportDownloadEndpoint.DownloadAsync(context, service, clock, Factory(logger));

        await records.WaitForCallsAsync(1);

        var tokenBefore = records.Tokens[0].IsCancellationRequested;

        // Act
        clock.Advance(ReportsView.Deadline);

        await download.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(tokenBefore, "the store call is open before the deadline");
        Assert.IsTrue(records.Tokens[0].IsCancellationRequested, "the store call in flight is cancelled at the deadline");
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode, "service unavailable");
        Assert.AreEqual(0L, response.Length, "no body");
        Assert.IsFalse(lifetime.Aborted, "the connection is not aborted");
        Assert.IsFalse(context.Response.Headers.ContainsKey("Content-Disposition"), "the answer is not saved as a report");
        Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString(), "cache control");
    }

    /// <summary>
    /// A store failure before anything is written is answered with 500 and no body, and logged with its exception only.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportDownloadEndpointAnswers500WhenTheStoreFailsBeforeWriting()
    {
        // Arrange
        var failure = new StoreException("store: reading records: secret detail of the store");
        var records = new ScriptedRecordReader
                      {
                          Failure = failure
                      };
        var logger = new ListLogger();
        var (context, response, lifetime) = Request("?lang=en");

        using var service = Service(records, new ScriptedIncidentStore([], false));

        // Act
        await ReportDownloadEndpoint.DownloadAsync(context, service, new FakeTimeProvider(_now), Factory(logger));

        // Assert
        var logged = logger.Entries.Select((entry, index) => (entry, index)).Where(item => item.entry.EventId.Id == 53).ToArray();

        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode, "internal server error");
        Assert.AreEqual(0L, response.Length, "no body, so no detail of the store");
        Assert.IsFalse(lifetime.Aborted, "the connection is not aborted");
        Assert.IsFalse(context.Response.Headers.ContainsKey("Content-Disposition"), "the answer is not saved as a report");
        Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString(), "cache control");
        Assert.HasCount(1, logged, "one entry with the event ID 53");
        Assert.AreEqual(LogLevel.Error, logged[0].entry.Level, "logged as an error");
        Assert.AreEqual("report download: making the report failed", logger.Messages[logged[0].index], "the message");
        Assert.AreSame(failure, logger.Exceptions[logged[0].index], "the exception only");
    }

    /// <summary>
    /// A store failure after the first byte aborts the connection, so that no truncated file is saved, and is logged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportDownloadEndpointAbortsWhenTheStoreFailsAfterWriting()
    {
        // Arrange
        var failure = new StoreException("store: reading incidents: failed during the second pass");
        var incidents = new ScriptedIncidentStore([[Stored(1, Outage(KillAt, 1, "mariadbd"))]], false)
                        {
                            FailAtCall = 3,
                            Failure = failure
                        };
        var logger = new ListLogger();
        var (context, response, lifetime) = Request("?lang=en");

        using var service = Service(new ScriptedRecordReader(), incidents);

        // Act
        await ReportDownloadEndpoint.DownloadAsync(context, service, new FakeTimeProvider(_now), Factory(logger));

        // Assert
        var logged = logger.Entries.Select((entry, index) => (entry, index)).Where(item => item.entry.EventId.Id == 53).ToArray();

        Assert.AreEqual(3, incidents.Calls, "the failure is the first query of the second pass");
        Assert.IsGreaterThan(0L, response.Length, "the report was started before the failure");
        Assert.IsTrue(response.Started, "the response has started");
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode, "the status of the started response is not changed");
        Assert.IsTrue(lifetime.Aborted, "the connection is aborted");
        Assert.IsTrue(context.RequestAborted.IsCancellationRequested, "the request is aborted");
        Assert.HasCount(1, logged, "one entry with the event ID 53");
        Assert.AreSame(failure, logger.Exceptions[logged[0].index], "the exception only");
    }

    /// <summary>
    /// A client that closes the connection while the report is read ends the run silently: no status change, no log entry and no abort by the handler.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportDownloadEndpointEndsSilentlyWhenTheClientCloses()
    {
        // Arrange
        var records = new ScriptedRecordReader
                      {
                          Before = (_, token) => Task.Delay(Timeout.Infinite, token)
                      };
        var logger = new ListLogger();
        var (context, response, lifetime) = Request("?lang=en");

        using var service = Service(records, new ScriptedIncidentStore([], false));

        var download = ReportDownloadEndpoint.DownloadAsync(context, service, new FakeTimeProvider(_now), Factory(logger));

        await records.WaitForCallsAsync(1);

        // Act
        lifetime.Close();

        await download.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode, "the status is not changed to 500 or 503");
        Assert.AreEqual(0L, response.Length, "no body");
        Assert.DoesNotContain(entry => entry.EventId.Id == 53, logger.Entries, "nothing is logged as a failure");
        Assert.IsFalse(lifetime.Aborted, "the handler does not abort the connection");
    }

    /// <summary>
    /// Creates a request context for the download whose response reports when the first byte was written.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>The context, the response body and the lifetime of the request</returns>
    private static (DefaultHttpContext Context, StartingResponseBody Response, AbortableRequestLifetime Lifetime) Request(string query)
    {
        var lifetime = new AbortableRequestLifetime();
        var feature = new StartingResponseFeature();
        var body = new StartingResponseBody(feature);
        var context = new DefaultHttpContext();

        context.Features.Set<IHttpResponseFeature>(feature);
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = body;

        return (context, body, lifetime);
    }

    /// <summary>
    /// Creates the service over a reader and a store, with a lookback of one hour.
    /// </summary>
    /// <param name="records">The reader</param>
    /// <param name="incidents">The store</param>
    /// <returns>The service</returns>
    private static ReportService Service(IRecordReader records, IIncidentStore incidents)
    {
        return new ReportService(records, incidents, new ScriptedListenerHistoryReader(), BuiltInSignatures.Catalog, TimeSpan.FromHours(1), new FakeTimeProvider(_now));
    }

    /// <summary>
    /// Creates a logger factory that writes to a logger that keeps its messages.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <returns>The factory</returns>
    private static ILoggerFactory Factory(ListLogger logger)
    {
        return LoggerFactory.Create(builder => builder.AddProvider(new ListLoggerProvider(logger)));
    }

    /// <summary>
    /// Signs the client in with the password of the fixture.
    /// </summary>
    /// <param name="client">The client</param>
    /// <returns>A task that completes when the client is signed in</returns>
    private async Task SignInAsync(LoginTestClient client)
    {
        using var response = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode, "the sign-in is answered with a redirect");
        Assert.IsTrue(client.HasSession, "the client holds a session cookie");
    }

    #endregion // Methods
}