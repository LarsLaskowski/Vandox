using System.Net;
using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentNoteEndpoint"/>: the handler with a request context and a fake browser, and the pipeline of the running service for what the middleware decides.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class IncidentNoteEndpointTests
{
    #region Constants

    private const string FormType = "application/x-www-form-urlencoded";

    private const string ValidForm = "note=hello&written_for=5";

    private const string NotesPath = "/incidents/1/notes";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the invalid forms: the body, and a query string where the fields are only there.
    /// </summary>
    /// <returns>The cases: name, body, query string and whether the fields are sent as multipart</returns>
    public static IEnumerable<object[]> InvalidForms()
    {
        yield return new object[] { "empty written_for", "note=hello&written_for=", string.Empty, false };
        yield return new object[] { "zero", "note=hello&written_for=0", string.Empty, false };
        yield return new object[] { "negative", "note=hello&written_for=-1", string.Empty, false };
        yield return new object[] { "plus sign", "note=hello&written_for=%2B1", string.Empty, false };
        yield return new object[] { "leading space", "note=hello&written_for=%201", string.Empty, false };
        yield return new object[] { "trailing space", "note=hello&written_for=1%20", string.Empty, false };
        yield return new object[] { "fullwidth digit", "note=hello&written_for=%EF%BC%91", string.Empty, false };
        yield return new object[] { "arabic-indic digit", "note=hello&written_for=%D9%A3", string.Empty, false };
        yield return new object[] { "exponent", "note=hello&written_for=1e3", string.Empty, false };
        yield return new object[] { "hexadecimal", "note=hello&written_for=0x1", string.Empty, false };
        yield return new object[] { "19 digits", "note=hello&written_for=1234567890123456789", string.Empty, false };
        yield return new object[] { "U+0000 first, multipart", "note=%00abc&written_for=1", string.Empty, true };
        yield return new object[] { "U+0000 inside, multipart", "note=a%00b&written_for=1", string.Empty, true };
        yield return new object[] { "1801 characters", "note=" + new string('a', 1801) + "&written_for=1", string.Empty, false };
        yield return new object[] { "1801 characters, multipart", "note=" + new string('a', 1801) + "&written_for=1", string.Empty, true };
        yield return new object[] { "missing written_for", "note=hello", string.Empty, false };
        yield return new object[] { "missing note", "written_for=1", string.Empty, false };
        yield return new object[] { "written_for twice in other case", "note=hello&written_for=1&WRITTEN_FOR=2", string.Empty, false };
        yield return new object[] { "note twice in other case", "note=a&NOTE=b&written_for=1", string.Empty, false };
        yield return new object[] { "written_for twice", "note=hello&written_for=1&written_for=1", string.Empty, false };
        yield return new object[] { "fields only in the query string", "other=1", "?note=hello&written_for=1", false };
        yield return new object[] { "note in the query string, written_for in the form", "written_for=1", "?note=hello", false };
    }

    /// <summary>
    /// Names a case of <see cref="InvalidForms"/>.
    /// </summary>
    /// <param name="method">The test method</param>
    /// <param name="data">The data of the case</param>
    /// <returns>The name</returns>
    public static string CaseName(MethodInfo method, object[] data)
    {
        return $"{method.Name} ({data[0]})";
    }

    /// <summary>
    /// A valid post stores the text with line ends as line feeds and without leading line feeds for the incident of the address and the number of the form, as a form or as multipart, and is answered with a redirect to the incident page; a blank text deletes the note.
    /// </summary>
    /// <param name="form">The fields of the form</param>
    /// <param name="outcome">The outcome the store answers</param>
    /// <param name="text">The text that the store receives</param>
    /// <param name="writtenFor">The number that the store receives</param>
    /// <param name="multipart">Whether the fields are sent as multipart</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("note=first%0D%0Asecond&written_for=5", IncidentNoteOutcome.Saved, "first\nsecond", 5L, false, DisplayName = "CR LF")]
    [DataRow("note=first%0Dsecond&written_for=5", IncidentNoteOutcome.Saved, "first\nsecond", 5L, false, DisplayName = "CR")]
    [DataRow("note=%0D%0A%0D%0Alead&written_for=12", IncidentNoteOutcome.Saved, "lead", 12L, false, DisplayName = "leading line ends")]
    [DataRow("note=%E2%82%AC+text&written_for=9", IncidentNoteOutcome.Saved, "\u20ac text", 9L, false, DisplayName = "UTF-8 and plus")]
    [DataRow("note=%20%0A&written_for=5", IncidentNoteOutcome.Deleted, "", 5L, false, DisplayName = "blank text deletes")]
    [DataRow("note=first%0D%0Asecond&written_for=5", IncidentNoteOutcome.Saved, "first\nsecond", 5L, true, DisplayName = "multipart")]
    public async Task IncidentNoteEndpointSavesNoteAndRedirects(string form, IncidentNoteOutcome outcome, string text, long writtenFor, bool multipart)
    {
        // Arrange
        var browser = new FakeIncidentBrowser
                      {
                          SaveOutcome = outcome
                      };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var context = multipart ? await MultipartAsync(form, true) : Request(form);

        // Act
        await IncidentNoteEndpoint.SaveAsync(context, 9, browser, clock, Factory(new ListLogger()));

        // Assert
        var edits = browser.Queries<IncidentNoteEdit>(FakeIncidentBrowser.SaveIncidentNote);

        Assert.AreEqual(StatusCodes.Status303SeeOther, context.Response.StatusCode, "see other");
        Assert.AreEqual("/incidents/9", context.Response.Headers.Location.ToString(), "back to the incident page, built from the number");
        Assert.HasCount(1, edits, "one save");
        Assert.AreEqual(9L, edits[0].IncidentId, "the incident is the one of the address");
        Assert.AreEqual(writtenFor, edits[0].WrittenFor, "the number is the one of the form");
        Assert.AreEqual(text, edits[0].Text, "the normalized text");
        Assert.AreEqual(clock.GetUtcNow(), edits[0].SavedAt, "the time of the save is the time of the clock");
        Assert.AreEqual(0L, context.Response.Body.Length, "no body");
    }

    /// <summary>
    /// Without a valid antiforgery validation feature the post is refused with 400 before its form is read, and a post through the running service needs a session, and then the token: anonymous is 401, a post without the token, with a body that is no form, with a value of 16,385 bytes, with 65 values or with U+0000 in the note is 400, and none of them stores anything.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentNoteEndpointRefusesWithoutSessionOrToken()
    {
        // Arrange
        var browser = new FakeIncidentBrowser();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var missing = Request(ValidForm, valid: null);
        var invalid = Request(ValidForm, valid: false);
        var statuses = new Dictionary<string, HttpStatusCode>();
        Uri? controlLocation = null;

        // Act
        await IncidentNoteEndpoint.SaveAsync(missing, 5, browser, clock, Factory(new ListLogger()));
        await IncidentNoteEndpoint.SaveAsync(invalid, 5, browser, clock, Factory(new ListLogger()));

        var notes = await RunWithServiceAsync(async client =>
                                              {
                                                  statuses["anonymous"] = await PostAsync(client, NotesPath, Fields("anonymous note", "1"));

                                                  await SignInAsync(client);

                                                  var token = await client.ReadFormFieldsAsync("/", TestContext.CancellationToken);

                                                  statuses["no token"] = await PostAsync(client, NotesPath, Fields("no token note", "1"));

                                                  using var plain = await client.SendAsync(HttpMethod.Post, NotesPath, new StringContent("note=plain+text&written_for=1", Encoding.UTF8, "text/plain"), null, TestContext.CancellationToken);

                                                  statuses["text/plain"] = plain.StatusCode;
                                                  statuses["16,385 bytes"] = await PostAsync(client, NotesPath, token.Concat(Fields(new string('a', 16385), "1")));
                                                  statuses["65 values"] = await PostAsync(client, NotesPath, token.Concat(Fields("many values", "1")).Concat(Enumerable.Range(0, 70).Select(index => KeyValuePair.Create($"extra{index}", "x"))));
                                                  statuses["U+0000"] = await PostAsync(client, NotesPath, token.Concat(Fields($"a{(char)0}b", "1")));

                                                  using var control = await client.PostFormAsync("/incidents/01/notes", token.Concat(Fields("control note", "1")), TestContext.CancellationToken);

                                                  statuses["control"] = control.StatusCode;
                                                  controlLocation = LoginTestClient.LocationOf(control) is { } location ? new Uri(location, UriKind.RelativeOrAbsolute) : null;
                                              });

        // Assert
        Assert.AreEqual(StatusCodes.Status400BadRequest, missing.Response.StatusCode, "a missing validation feature is a refusal");
        Assert.AreEqual(StatusCodes.Status400BadRequest, invalid.Response.StatusCode, "an invalid token is a refusal");
        Assert.AreEqual(0L, ((MemoryStream)missing.Request.Body).Position, "the form is not read without the feature");
        Assert.AreEqual(0L, ((MemoryStream)invalid.Request.Body).Position, "the form is not read after an invalid token");
        Assert.IsEmpty(browser.Calls, "the store is not called");
        Assert.AreEqual(HttpStatusCode.Unauthorized, statuses["anonymous"], "no session");
        Assert.AreEqual(HttpStatusCode.BadRequest, statuses["no token"], "a form post without the antiforgery token");
        Assert.AreEqual(HttpStatusCode.BadRequest, statuses["text/plain"], "a body that is no form");
        Assert.AreEqual(HttpStatusCode.BadRequest, statuses["16,385 bytes"], "a value of 16,385 bytes");
        Assert.AreEqual(HttpStatusCode.BadRequest, statuses["65 values"], "65 values");
        Assert.AreEqual(HttpStatusCode.BadRequest, statuses["U+0000"], "a note with U+0000, which the form reader of the middleware refuses");
        Assert.AreEqual(HttpStatusCode.SeeOther, statuses["control"], "a post with a session and the token is saved, also with the route value 01");
        Assert.AreEqual("/incidents/1", controlLocation?.ToString(), "the redirect is built from the number, not from the path");
        Assert.AreEqual("control note", notes.Notes.Single().Text, "only the post of the control is stored");
    }

    /// <summary>
    /// Every invalid field is refused with 400, nothing is stored and no redirect is sent.
    /// </summary>
    /// <param name="name">The name of the case</param>
    /// <param name="body">The body of the post</param>
    /// <param name="query">The query string of the post</param>
    /// <param name="multipart">Whether the fields are sent as multipart, where U+0000 reaches the form as it is</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DynamicData(nameof(InvalidForms), DynamicDataDisplayName = nameof(CaseName))]
    public async Task IncidentNoteEndpointRefusesInvalidFields(string name, string body, string query, bool multipart)
    {
        // Arrange
        var browser = new FakeIncidentBrowser();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var context = multipart ? await MultipartAsync(body, true) : Request(body, query: query);

        // Act
        await IncidentNoteEndpoint.SaveAsync(context, 5, browser, clock, Factory(new ListLogger()));

        // Assert
        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode, name);
        Assert.IsEmpty(browser.Calls, $"nothing is stored: {name}");
        Assert.AreEqual(string.Empty, context.Response.Headers.Location.ToString(), $"no redirect: {name}");
        Assert.AreEqual(0L, context.Response.Body.Length, $"no text: {name}");
    }

    /// <summary>
    /// A note of 1,800 characters of three bytes each, which is 16,200 bytes in a form, passes the form limits of the service and is stored.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentNoteEndpointAcceptsTheLongestNoteInTheWorstEncoding()
    {
        // Arrange
        var longest = new string((char)0x20AC, StorageLimits.MaxNoteChars);
        var status = HttpStatusCode.Unused;
        var location = string.Empty;

        // Act
        var notes = await RunWithServiceAsync(async client =>
                                              {
                                                  await SignInAsync(client);

                                                  var token = await client.ReadFormFieldsAsync("/", TestContext.CancellationToken);

                                                  using var response = await client.PostFormAsync(NotesPath, token.Concat(Fields(longest, "1")), TestContext.CancellationToken);

                                                  status = response.StatusCode;
                                                  location = LoginTestClient.LocationOf(response) ?? string.Empty;
                                              });

        // Assert
        Assert.AreEqual(HttpStatusCode.SeeOther, status, "the longest note is accepted");
        Assert.AreEqual("/incidents/1", location, "the redirect");
        Assert.AreEqual(longest, notes.Notes.Single().Text, "the note is stored as it was sent");
    }

    /// <summary>
    /// An unknown incident, and an incident number that is not positive, are 404 without text and without a redirect; a number that is not positive does not reach the store.
    /// </summary>
    /// <param name="id">The number of the route</param>
    /// <param name="outcome">The outcome the store answers</param>
    /// <param name="calls">The number of store calls that are expected</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(99L, IncidentNoteOutcome.NotFound, 1)]
    [DataRow(0L, IncidentNoteOutcome.Saved, 0)]
    [DataRow(-1L, IncidentNoteOutcome.Saved, 0)]
    [DataRow(-9223372036854775808L, IncidentNoteOutcome.Saved, 0)]
    public async Task IncidentNoteEndpointAnswersNotFoundForUnknownIncident(long id, IncidentNoteOutcome outcome, int calls)
    {
        // Arrange
        var browser = new FakeIncidentBrowser
                      {
                          SaveOutcome = outcome
                      };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var context = Request(ValidForm);

        // Act
        await IncidentNoteEndpoint.SaveAsync(context, id, browser, clock, Factory(new ListLogger()));

        // Assert
        Assert.AreEqual(StatusCodes.Status404NotFound, context.Response.StatusCode, "not found");
        Assert.HasCount(calls, browser.Calls, "the number of store calls");
        Assert.AreEqual(string.Empty, context.Response.Headers.Location.ToString(), "no redirect");
        Assert.AreEqual(0L, context.Response.Body.Length, "no text");
    }

    /// <summary>
    /// A store error is 500 without text and is logged with its exception only, never with the text of the note, a value of the form or the number it was written for; a note the store itself refuses is 400.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentNoteEndpointAnswersWithBareStatusesWhenTheStoreFails()
    {
        // Arrange
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var failing = new FakeIncidentBrowser();
        var refusing = new FakeIncidentBrowser();
        var failure = new StoreException("store: saving note: disk full");

        failing.Failures[FakeIncidentBrowser.SaveIncidentNote] = failure;
        refusing.Failures[FakeIncidentBrowser.SaveIncidentNote] = new InvalidNoteException("store: invalid note: text is not valid UTF-16");

        var failed = Request("note=SENTINEL-NOTE-TEXT&written_for=424242");
        var refused = Request(ValidForm);

        // Act
        await IncidentNoteEndpoint.SaveAsync(failed, 5, failing, clock, Factory(logger));
        await IncidentNoteEndpoint.SaveAsync(refused, 5, refusing, clock, Factory(new ListLogger()));

        // Assert
        Assert.AreEqual(StatusCodes.Status500InternalServerError, failed.Response.StatusCode, "a store error");
        Assert.AreEqual(0L, failed.Response.Body.Length, "no text");
        Assert.Contains("incidents view: saving a note failed", logger.Messages, "the message of the log method");
        Assert.Contains(exception => ReferenceEquals(exception, failure), logger.Exceptions, "the exception is logged");
        Assert.Contains(entry => entry.Level == LogLevel.Error, logger.Entries, "at level error");
        Assert.DoesNotContain(message => message.Contains("SENTINEL", StringComparison.Ordinal) || message.Contains("424242", StringComparison.Ordinal), logger.Messages, "neither the text nor the number is logged");
        Assert.AreEqual(StatusCodes.Status400BadRequest, refused.Response.StatusCode, "a note the store refuses");
        Assert.AreEqual(0L, refused.Response.Body.Length, "no text of the refusal");
    }

    /// <summary>
    /// Creates a request context with a form body.
    /// </summary>
    /// <param name="body">The URL-encoded body</param>
    /// <param name="contentType">The content type</param>
    /// <param name="valid">The result of the antiforgery validation, or <c>null</c> for a request without the feature</param>
    /// <param name="query">The query string</param>
    /// <returns>The context</returns>
    private static DefaultHttpContext Request(string body, string contentType = FormType, bool? valid = true, string query = "")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        if (valid is not null)
        {
            context.Features.Set<IAntiforgeryValidationFeature>(new FixedAntiforgeryValidationFeature(valid.Value));
        }

        return context;
    }

    /// <summary>
    /// Creates a request context with a multipart body of the fields of a URL-encoded body.
    /// </summary>
    /// <param name="fields">The URL-encoded fields</param>
    /// <param name="valid">The result of the antiforgery validation</param>
    /// <returns>A task that returns the context</returns>
    private static async Task<DefaultHttpContext> MultipartAsync(string fields, bool valid)
    {
        using var content = new MultipartFormDataContent();

        foreach (var pair in fields.Split('&'))
        {
            var parts = pair.Split('=', 2);

            content.Add(new StringContent(Uri.UnescapeDataString(parts[1].Replace('+', ' '))), parts[0]);
        }

        var bytes = await content.ReadAsByteArrayAsync();
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = content.Headers.ContentType!.ToString();
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Response.Body = new MemoryStream();
        context.Features.Set<IAntiforgeryValidationFeature>(new FixedAntiforgeryValidationFeature(valid));

        return context;
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
    /// Creates the two fields of a note form.
    /// </summary>
    /// <param name="note">The text</param>
    /// <param name="writtenFor">The number the note is written for</param>
    /// <returns>The fields</returns>
    private static IEnumerable<KeyValuePair<string, string>> Fields(string note, string writtenFor)
    {
        return [KeyValuePair.Create("note", note), KeyValuePair.Create("written_for", writtenFor)];
    }

    /// <summary>
    /// Posts a form and returns the status.
    /// </summary>
    /// <param name="client">The client</param>
    /// <param name="path">The path</param>
    /// <param name="fields">The fields</param>
    /// <returns>A task that returns the status code</returns>
    private async Task<HttpStatusCode> PostAsync(LoginTestClient client, string path, IEnumerable<KeyValuePair<string, string>> fields)
    {
        using var response = await client.PostFormAsync(path, fields, TestContext.CancellationToken);

        return response.StatusCode;
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

    /// <summary>
    /// Stores one incident, runs the service over the data directory, runs a body with a client of its web port, stops the service and reads the notes of the incident.
    /// </summary>
    /// <param name="body">The body</param>
    /// <returns>A task that returns the notes of incident 1 after the service stopped</returns>
    private async Task<IncidentNotePage> RunWithServiceAsync(Func<LoginTestClient, Task> body)
    {
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        await using (var seeding = await SqliteStore.OpenAsync(fixture.Storage, TestContext.CancellationToken))
        {
            await seeding.SaveIncidentAsync(IncidentViewSamples.Full(1).Incident, IncidentViewSamples.Start.AddDays(1), TestContext.CancellationToken);
        }

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);

        try
        {
            await body(client);
        }
        finally
        {
            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        }

        await using var store = await SqliteStore.OpenAsync(fixture.Storage, TestContext.CancellationToken);

        return await store.IncidentNotesAsync(1, TestContext.CancellationToken);
    }

    #endregion // Methods
}