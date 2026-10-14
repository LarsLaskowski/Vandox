using System.Net;
using System.Net.Sockets;
using System.Reflection;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Hosting;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Backend.Security;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests that run the real service with <see cref="BackendApp"/>
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class ServeTests
{
    #region Constants

    private const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";
    private const string WrongPassword = "wrong-password-marker-1";

    #endregion // Constants

    #region Fields

    private static readonly string[] _webAssets = [
                                                      "/_framework/blazor.web.js",
                                                      "/app.css",
                                                      "/js/app.js",
                                                      "/js/charts.js",
                                                      "/lib/uplot/uPlot.esm.js",
                                                      "/lib/uplot/uPlot.min.css",
                                                      "/brand/vandox-favicon.svg",
                                                      "/brand/vandox-logo-horizontal.svg",
                                                      "/brand/vandox-logo-horizontal-dark.svg"
                                                  ];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The service listens on the configured ports, answers the health check and the web UI on the web port, answers 404 on
    /// the ingest port, creates the database and stops cleanly.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeRunsServiceOnConfiguredPortsAndStopsCleanly()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ServeHooks
                    {
                        Started = _ => started.TrySetResult()
                    };
        var run = fixture.RunAsync([], hooks, stop.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        using var client = CreateClient();

        var web = $"http://127.0.0.1:{fixture.WebPort}";
        var ingest = $"http://127.0.0.1:{fixture.IngestPort}";

        // Act
        using var health = await client.GetAsync($"{web}/healthz", TestContext.CancellationToken);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{web}/healthz"), TestContext.CancellationToken);
        using var home = await client.GetAsync($"{web}/", TestContext.CancellationToken);
        using var missing = await client.GetAsync($"{web}/nothing-here", TestContext.CancellationToken);
        using var ingestHealth = await client.GetAsync($"{ingest}/healthz", TestContext.CancellationToken);
        using var ingestRoot = await client.GetAsync($"{ingest}/", TestContext.CancellationToken);
        var probe = await fixture.RunAsync(["-healthcheck"], null, TestContext.CancellationToken);

        await stop.CancelAsync();

        var code = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "health status");
        Assert.AreEqual("ok\n", await health.Content.ReadAsStringAsync(TestContext.CancellationToken), "health body");
        Assert.AreEqual("no-store", health.Headers.CacheControl?.ToString(), "health cache control");
        Assert.AreEqual("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single(), "health content type options");
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode, "HEAD health status");
        Assert.AreEqual(HttpStatusCode.Found, home.StatusCode, "an anonymous visitor of the home page is redirected");
        Assert.AreEqual("/login", LoginTestClient.LocationOf(home), "the redirect goes to the login page");
        Assert.IsFalse(home.Headers.Contains("Server"), "no server header");
        Assert.AreEqual(HttpStatusCode.Found, missing.StatusCode, "an unknown route is redirected to the login page too");
        Assert.AreEqual(HttpStatusCode.NotFound, ingestHealth.StatusCode, "no health check on the ingest port");
        Assert.AreEqual(HttpStatusCode.NotFound, ingestRoot.StatusCode, "no web UI on the ingest port");
        Assert.AreEqual(0, probe, "the health probe of the image passes");
        Assert.AreEqual(0, code, "exit code after a graceful stop");
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Storage, "vandox.db")), "the database exists");
        Assert.Contains("\"msg\":\"listening\"", fixture.Error.ToString(), "the listeners are logged");
        Assert.Contains("\"msg\":\"vandoxd stopped\"", fixture.Error.ToString(), "the stop is logged");
        Assert.DoesNotContain("secret", fixture.Error.ToString(), "no secret in the log");
    }

    /// <summary>
    /// An invalid configuration stops the start with exit code 1 and a log line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeRefusesInvalidConfiguration()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await File.WriteAllTextAsync(fixture.ConfigPath, "web:\n  listen: nope\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync([], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("\"msg\":\"configuration invalid\"", fixture.Error.ToString(), "log line");
        Assert.Contains("web.listen", fixture.Error.ToString(), "the key is named");
    }

    /// <summary>
    /// A missing storage directory stops the start with exit code 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeRefusesMissingStorageDirectory()
    {
        // Arrange
        using var fixture = new BackendFixture();

        Directory.Delete(fixture.Storage);

        // Act
        var code = await fixture.RunAsync([], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("opening database failed", fixture.Error.ToString(), "log line");
    }

    /// <summary>
    /// A port that is already taken stops the service with exit code 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeFailsWhenPortIsTaken()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var occupied = new TcpListener(IPAddress.Loopback, fixture.WebPort);

        occupied.Start();

        // Act
        var code = await fixture.RunAsync([], null, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("\"msg\":\"serving failed\"", fixture.Error.ToString(), "log line");
    }

    /// <summary>
    /// The listener hook replaces the configured addresses, and a database that stops answering makes the health check 503.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeUsesListenHookAndReportsUnavailableDatabase()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var started = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ServeHooks
                    {
                        Listen = options =>
                                 {
                                     options.Listen(IPAddress.Loopback, 0, ListenerRoutes.Web);
                                     options.Listen(IPAddress.Loopback, 0, ListenerRoutes.Ingest);
                                 },
                        Started = addresses => started.TrySetResult(addresses)
                    };
        var run = fixture.RunAsync([], hooks, stop.Token);
        var addresses = await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        using var client = new HttpClient
                           {
                               Timeout = TimeSpan.FromSeconds(10)
                           };

        // Act
        using var healthy = await client.GetAsync($"{addresses[0]}/healthz", TestContext.CancellationToken);

        File.Delete(Path.Combine(fixture.Storage, "vandox.db"));
        Directory.Delete(fixture.Storage, true);

        using var broken = await client.GetAsync($"{addresses[0]}/healthz", TestContext.CancellationToken);

        await stop.CancelAsync();

        var code = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, addresses, "two listeners");
        Assert.AreEqual(HttpStatusCode.OK, healthy.StatusCode, "healthy");
        Assert.AreEqual(0, code, "the run ends cleanly");
        Assert.IsTrue(broken.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, "the check answers after the database file is gone");
        Assert.AreSequenceEqual(new[] { SecurityHeadersMiddleware.ContentTypeOptions }, HeaderValues(broken, "X-Content-Type-Options"), "exactly one content type options value on the second answer");
        Assert.AreSequenceEqual(new[] { SecurityHeadersMiddleware.ContentSecurityPolicy }, HeaderValues(broken, "Content-Security-Policy"), "exactly one policy on the second answer");
    }

    /// <summary>
    /// The page on the web port loads only local resources and holds no inline code, and it carries the elements of the shell.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeServesPageWithLocalResourcesAndNoInlineCode()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);

        using var client = new LoginTestClient(fixture.WebPort);

        await SignInAsync(client);

        // Act
        using var response = await client.GetAsync("/", TestContext.CancellationToken);

        var html = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "page status");
        Assert.IsEmpty(WebSourceScanner.FindInMarkup(html), "rule ids found in the served page");
        Assert.Contains("<base href=\"/\">", html, "base element");
        Assert.Contains("name=\"viewport\"", html, "viewport meta element");
        Assert.Contains("<link rel=\"icon\"", html, "icon link");
        Assert.Contains("href=\"brand/vandox-favicon.svg\"", html, "icon file");
        Assert.Contains("<script src=\"js/app.js\">", html, "own script");
        Assert.Contains("<script src=\"_framework/blazor.web.js\">", html, "framework script");
        Assert.Contains("<!--Blazor:{\"type\":\"server\"", html, "interactive server marker");
        Assert.Contains("id=\"components-reconnect-modal\"", html, "reconnect element");
        Assert.Contains("id=\"blazor-error-ui\"", html, "error banner");
    }

    /// <summary>
    /// Every response of the web port carries exactly one value of each security header.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeSetsSecurityHeadersOnWebResponses()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);

        using var client = CreateClient();

        var paths = new[] { "/", "/login", "/app.css", "/_framework/blazor.web.js", "/healthz", "/nothing-here" };
        var problems = new List<string>();

        // Act
        foreach (var path in paths)
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{fixture.WebPort}{path}", TestContext.CancellationToken);

            AddHeaderProblems(problems, path, response, "Content-Security-Policy", SecurityHeadersMiddleware.ContentSecurityPolicy);
            AddHeaderProblems(problems, path, response, "X-Content-Type-Options", SecurityHeadersMiddleware.ContentTypeOptions);
            AddHeaderProblems(problems, path, response, "Referrer-Policy", SecurityHeadersMiddleware.ReferrerPolicy);
            AddHeaderProblems(problems, path, response, "X-Frame-Options", SecurityHeadersMiddleware.FrameOptions);
        }

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(problems, $"header problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// The static files are served on the web port from the build output without a session, the live connection needs one, and none of them is served on the ingest port.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeServesWebAssetsOnlyOnWebPort()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);

        using var client = new LoginTestClient(fixture.WebPort);
        using var ingestClient = CreateClient();

        var problems = new List<string>();

        // Act
        foreach (var path in _webAssets)
        {
            using var web = await client.GetAsync(path, TestContext.CancellationToken);
            using var ingest = await ingestClient.GetAsync($"http://127.0.0.1:{fixture.IngestPort}{path}", TestContext.CancellationToken);

            AddStatusProblem(problems, $"GET web {path}", HttpStatusCode.OK, web);
            AddStatusProblem(problems, $"GET ingest {path}", HttpStatusCode.NotFound, ingest);
        }

        using var anonymousNegotiate = await client.SendAsync(HttpMethod.Post, Negotiate, new ByteArrayContent([]), null, TestContext.CancellationToken);
        using var ingestNegotiate = await ingestClient.PostAsync($"http://127.0.0.1:{fixture.IngestPort}{Negotiate}", new ByteArrayContent([]), TestContext.CancellationToken);

        AddStatusProblem(problems, "POST web negotiate without a session", HttpStatusCode.Unauthorized, anonymousNegotiate);
        AddStatusProblem(problems, "POST ingest negotiate", HttpStatusCode.NotFound, ingestNegotiate);

        await SignInAsync(client);

        using var signedInNegotiate = await client.SendAsync(HttpMethod.Post, Negotiate, new ByteArrayContent([]), null, TestContext.CancellationToken);

        AddStatusProblem(problems, "POST web negotiate with a session", HttpStatusCode.OK, signedInNegotiate);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(problems, $"status problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// Without the hash of the web password the service logs the missing variable, exits with code 1 and creates no database.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeRefusesMissingPasswordHash()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var hooks = new ServeHooks
                    {
                        Started = _ => stop.Cancel()
                    };

        // Act
        var code = await fixture.RunAsync([], hooks, stop.Token, new KeyValuePair<string, string>("LANG", "C")).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        var entries = fixture.LogEntries().Where(entry => entry.GetValueOrDefault("msg") == "web password hash missing").ToArray();

        Assert.AreEqual(1, code, "exit code");
        Assert.HasCount(1, entries, "one refusal is logged");
        Assert.AreEqual("ERROR", entries[0].GetValueOrDefault("level"), "level");
        Assert.AreEqual(BackendFixture.HashVariable, entries[0].GetValueOrDefault("variable"), "the variable is named");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Storage, "vandox.db")), "no database was created");
    }

    /// <summary>
    /// A hash outside the accepted form or bounds is refused with the exact reason, and the log does not contain the hash.
    /// </summary>
    /// <param name="kind">The kind of hash</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("weak reference vector")]
    [DataRow("argon2i string")]
    public async Task ServeRefusesInvalidPasswordHash(string kind)
    {
        // Arrange
        var (hash, reason) = kind == "argon2i string" ? (ReferenceVectors.InBounds().First().Hash.Replace("$argon2id$", "$argon2i$", StringComparison.Ordinal), PasswordHash.ReasonForm) : ReferenceVectors.OutOfBounds().First();

        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var hooks = new ServeHooks
                    {
                        Started = _ => stop.Cancel()
                    };

        // Act
        var code = await fixture.RunAsync([], hooks, stop.Token, new KeyValuePair<string, string>(BackendFixture.HashVariable, hash)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        var entries = fixture.LogEntries().Where(entry => entry.GetValueOrDefault("msg") == "web password hash invalid").ToArray();
        var log = fixture.Error.ToString();

        Assert.AreEqual(1, code, "exit code");
        Assert.HasCount(1, entries, "one refusal is logged");
        Assert.AreEqual("ERROR", entries[0].GetValueOrDefault("level"), "level");
        Assert.AreEqual(reason, entries[0].GetValueOrDefault("reason"), "the exact reason");
        Assert.DoesNotContain(hash, log, "the hash is not logged");
        Assert.DoesNotContain(hash.Split('$')[^1], log, "the hash part is not logged");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Storage, "vandox.db")), "no database was created");
    }

    /// <summary>
    /// Every route needs a session except the login page, the health check and the static files; the ingest port is unchanged; a signed-in user gets the pages.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeRequiresSessionForEveryRouteExceptLoginHealthAndAssets()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);

        using var client = new LoginTestClient(fixture.WebPort);
        using var ingestClient = CreateClient();

        var problems = new List<string>();

        // Act
        using var home = await client.GetAsync("/", TestContext.CancellationToken);
        using var missing = await client.GetAsync("/nothing-here", TestContext.CancellationToken);
        using var head = await client.SendAsync(HttpMethod.Head, "/", null, null, TestContext.CancellationToken);
        using var negotiate = await client.SendAsync(HttpMethod.Post, Negotiate, new ByteArrayContent([]), null, TestContext.CancellationToken);
        using var login = await client.GetAsync("/login", TestContext.CancellationToken);
        using var health = await client.GetAsync("/healthz", TestContext.CancellationToken);

        AddStatusProblem(problems, "anonymous GET /", HttpStatusCode.Found, home);
        AddLocationProblem(problems, "anonymous GET /", "/login", home);
        AddStatusProblem(problems, "anonymous GET /nothing-here", HttpStatusCode.Found, missing);
        AddLocationProblem(problems, "anonymous GET /nothing-here", "/login", missing);
        AddStatusProblem(problems, "anonymous HEAD /", HttpStatusCode.Found, head);
        AddStatusProblem(problems, "anonymous POST negotiate", HttpStatusCode.Unauthorized, negotiate);
        AddStatusProblem(problems, "anonymous GET /login", HttpStatusCode.OK, login);
        AddStatusProblem(problems, "anonymous GET /healthz", HttpStatusCode.OK, health);

        if (await health.Content.ReadAsStringAsync(TestContext.CancellationToken) != "ok\n")
        {
            problems.Add("GET /healthz did not answer ok");
        }

        foreach (var path in _webAssets)
        {
            using var asset = await client.GetAsync(path, TestContext.CancellationToken);

            AddStatusProblem(problems, $"anonymous GET {path}", HttpStatusCode.OK, asset);
        }

        foreach (var path in _webAssets.Concat(["/", "/login", "/healthz", "/nothing-here"]))
        {
            using var ingest = await ingestClient.GetAsync($"http://127.0.0.1:{fixture.IngestPort}{path}", TestContext.CancellationToken);

            AddStatusProblem(problems, $"GET ingest {path}", HttpStatusCode.NotFound, ingest);
        }

        await SignInAsync(client);

        using var signedHome = await client.GetAsync("/", TestContext.CancellationToken);
        using var signedMissing = await client.GetAsync("/nothing-here", TestContext.CancellationToken);
        using var signedNegotiate = await client.SendAsync(HttpMethod.Post, Negotiate, new ByteArrayContent([]), null, TestContext.CancellationToken);
        using var signedLogin = await client.GetAsync("/login", TestContext.CancellationToken);

        AddStatusProblem(problems, "signed-in GET /", HttpStatusCode.OK, signedHome);
        AddStatusProblem(problems, "signed-in GET /nothing-here", HttpStatusCode.NotFound, signedMissing);
        AddStatusProblem(problems, "signed-in POST negotiate", HttpStatusCode.OK, signedNegotiate);
        AddStatusProblem(problems, "signed-in GET /login", HttpStatusCode.Found, signedLogin);
        AddLocationProblem(problems, "signed-in GET /login", "/", signedLogin);

        var html = await signedHome.Content.ReadAsStringAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(problems, $"problems: {string.Join("; ", problems)}");
        Assert.Contains("Vandox backend", html, "the signed-in user gets the page");
    }

    /// <summary>
    /// The cookies are <c>Secure</c> exactly when a trusted proxy says the request was HTTPS, and always <c>HttpOnly</c> with their <c>SameSite</c> value.
    /// </summary>
    /// <param name="trustedProxies">The value of <c>web.trusted_proxies</c></param>
    /// <param name="trusted">Whether the peer address of the test is trusted</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("", false)]
    [DataRow("127.0.0.2", false)]
    [DataRow("127.0.0.1", true)]
    public async Task ServeSetsCookieFlagsFromTrustedProxyOnly(string trustedProxies, bool trusted)
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        fixture.UseTrustedProxies(trustedProxies);

        var run = await StartAsync(fixture, stop.Token);
        var forwarded = new Dictionary<string, string>
                        {
                            ["X-Forwarded-Proto"] = "https"
                        };
        var cases = new (string Label, IReadOnlyDictionary<string, string>? Headers, bool Secure)[]
                    {
                        ("forwarded as HTTPS", forwarded, trusted),
                        ("direct", null, false)
                    };
        var problems = new List<string>();

        // Act
        foreach (var (label, headers, secure) in cases)
        {
            using var client = new LoginTestClient(fixture.WebPort);
            using var page = await client.GetAsync("/login", TestContext.CancellationToken, headers);

            var fields = LoginTestClient.HiddenFields(await page.Content.ReadAsStringAsync(TestContext.CancellationToken));

            fields[LoginTestClient.PasswordField] = BackendFixture.Password;

            using var post = await client.PostFormAsync("/login", fields, TestContext.CancellationToken, headers);

            AddCookieProblems(problems, $"{label}: {LoginTestClient.AntiforgeryCookie}", LoginTestClient.SetCookie(page, LoginTestClient.AntiforgeryCookie), "strict", secure, false);
            AddStatusProblem(problems, $"{label}: sign-in", HttpStatusCode.Found, post);
            AddCookieProblems(problems, $"{label}: {LoginTestClient.SessionCookie}", LoginTestClient.SetCookie(post, LoginTestClient.SessionCookie), "lax", secure, true);
        }

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(problems, $"cookie problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// The client address of the lockout is taken from <c>X-Forwarded-For</c> only when the peer is a trusted proxy, otherwise the proxy's address is the client and the log says that the header was ignored.
    /// </summary>
    /// <param name="trustedProxies">The value of <c>web.trusted_proxies</c></param>
    /// <param name="trusted">Whether the peer address of the test is trusted</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("127.0.0.2", false)]
    [DataRow("", false)]
    public async Task ServeTakesClientAddressFromTrustedProxyOnly(string trustedProxies, bool trusted)
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        fixture.UseTrustedProxies(trustedProxies);

        var run = await StartAsync(fixture, stop.Token);
        var first = new Dictionary<string, string>
                    {
                        ["X-Forwarded-For"] = "198.51.100.7"
                    };
        var second = new Dictionary<string, string>
                     {
                         ["X-Forwarded-For"] = "198.51.100.8"
                     };
        var problems = new List<string>();

        using var client = new LoginTestClient(fixture.WebPort);

        // Act
        for (var count = 0; count < LoginThrottle.MaxFailures; count++)
        {
            using var wrong = await client.PostLoginAsync(WrongPassword, TestContext.CancellationToken, first);

            AddStatusProblem(problems, $"wrong password {count + 1}", HttpStatusCode.Unauthorized, wrong);
        }

        using var lockedFirst = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken, first);
        using var otherClient = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken, second);

        AddStatusProblem(problems, "right password of the locked client", HttpStatusCode.TooManyRequests, lockedFirst);
        AddStatusProblem(problems, "right password of the other client", trusted ? HttpStatusCode.Found : HttpStatusCode.TooManyRequests, otherClient);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var entries = fixture.LogEntries();
        var hints = entries.Where(entry => entry.GetValueOrDefault("msg") == "forwarded header ignored").ToArray();

        Assert.IsEmpty(problems, $"status problems: {string.Join("; ", problems)}");

        if (trusted)
        {
            Assert.IsNotEmpty(entries.Where(entry => entry.GetValueOrDefault("client") == "198.51.100.7").ToArray(), "the forwarded address is the client in the log");
            Assert.IsEmpty(hints, "no hint when the header is used");
        }
        else
        {
            Assert.HasCount(1, hints, "the hint is logged once");
            Assert.AreEqual("127.0.0.1", hints[0].GetValueOrDefault("client"), "the peer is the client");
        }
    }

    /// <summary>
    /// A form post without a valid antiforgery token, or whose body cannot be read as a form, is answered 400 without an effect and without counting as a sign-in attempt, and never causes a 500 or an error in the log.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeRejectsFormPostsWithoutValidAntiforgeryToken()
    {
        // Arrange
        const int mebibyte = 1 << 20;

        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);

        using var client = new LoginTestClient(fixture.WebPort);
        using var foreign = new LoginTestClient(fixture.WebPort);

        var problems = new List<string>();
        var valid = await client.ReadFormFieldsAsync("/login", TestContext.CancellationToken);
        var foreignToken = (await foreign.ReadFormFieldsAsync("/login", TestContext.CancellationToken))[LoginTestClient.TokenField];
        var withoutToken = new Dictionary<string, string>
                           {
                               [LoginTestClient.HandlerField] = "login",
                               [LoginTestClient.PasswordField] = WrongPassword
                           };
        var otherCookie = new Dictionary<string, string>(valid)
                          {
                              [LoginTestClient.TokenField] = foreignToken,
                              [LoginTestClient.PasswordField] = WrongPassword
                          };
        var oversized = new Dictionary<string, string>(valid)
                        {
                            [LoginTestClient.PasswordField] = new string('p', 20 * 1024)
                        };
        var multipart = new Dictionary<string, string>(valid)
                        {
                            [LoginTestClient.PasswordField] = WrongPassword
                        };

        // Act
        for (var round = 0; round < 2; round++)
        {
            using var noToken = await client.PostFormAsync("/login", withoutToken, TestContext.CancellationToken);
            using var foreignToken2 = await client.PostFormAsync("/login", otherCookie, TestContext.CancellationToken);

            AddRefusedProblems(problems, "login without a token", noToken);
            AddRefusedProblems(problems, "login with the token of another cookie", foreignToken2);
        }

        using var big = await client.PostFormAsync("/login", oversized, TestContext.CancellationToken);
        using var file = await client.PostMultipartAsync("/login", multipart, mebibyte, TestContext.CancellationToken);

        AddRefusedProblems(problems, "login with a 20 KiB password", big);

        using var bigWithHeader = await client.PostFormAsync("/login",
                                                             oversized,
                                                             TestContext.CancellationToken,
                                                             new Dictionary<string, string>
                                                             {
                                                                 ["RequestVerificationToken"] = valid[LoginTestClient.TokenField]
                                                             });

        AddRefusedProblems(problems, "login with a 20 KiB password and the token in the header", bigWithHeader);
        AddRefusedProblems(problems, "multipart login with a 1 MiB file part", file);

        valid[LoginTestClient.PasswordField] = BackendFixture.Password;

        using var signIn = await client.PostFormAsync("/login", valid, TestContext.CancellationToken);

        AddStatusProblem(problems, "sign-in after seven refused posts", HttpStatusCode.Found, signIn);

        var signed = await client.ReadFormFieldsAsync("/", TestContext.CancellationToken);
        var oversizedLogout = new Dictionary<string, string>(signed)
                              {
                                  ["big"] = new string('v', 20 * 1024)
                              };
        var manyValues = new Dictionary<string, string>(signed);

        for (var index = 0; index < 65; index++)
        {
            manyValues[$"value{index}"] = "x";
        }

        using var logoutWithoutToken = await client.PostFormAsync("/logout", new Dictionary<string, string>(), TestContext.CancellationToken);

        AddRefusedProblems(problems, "logout without a token", logoutWithoutToken, false);
        await AddSignedInProblemsAsync(problems, "after logout without a token", client);

        using var pageWithoutToken = await client.PostFormAsync("/", new Dictionary<string, string>(), TestContext.CancellationToken);

        AddRefusedProblems(problems, "page post without a token", pageWithoutToken, false);
        await AddSignedInProblemsAsync(problems, "after page post without a token", client);

        using var logoutBig = await client.PostFormAsync("/logout", oversizedLogout, TestContext.CancellationToken);

        AddRefusedProblems(problems, "logout with a 20 KiB value", logoutBig, false);
        await AddSignedInProblemsAsync(problems, "after logout with a 20 KiB value", client);

        using var logoutMany = await client.PostFormAsync("/logout", manyValues, TestContext.CancellationToken);

        AddRefusedProblems(problems, "logout with 65 values", logoutMany, false);
        await AddSignedInProblemsAsync(problems, "after logout with 65 values", client);

        using var logoutFile = await client.PostMultipartAsync("/logout", signed, mebibyte, TestContext.CancellationToken);

        AddRefusedProblems(problems, "multipart logout with a 1 MiB file part", logoutFile, false);
        await AddSignedInProblemsAsync(problems, "after multipart logout", client);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        problems.AddRange(fixture.LogEntries().Where(entry => entry.GetValueOrDefault("level") == "ERROR").Select(entry => $"error in the log: {entry.GetValueOrDefault("msg")}"));

        Assert.IsEmpty(problems, $"problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// Five wrong passwords lock the client, the sixth attempt gets 429 also with the right password, and after 15 minutes the client can sign in; the log has the lines and never the password.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeLocksOutClientAfterFiveFailedSignIns()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var clock = new FakeTimeProvider();
        var run = await StartAsync(fixture, stop.Token, clock);
        var problems = new List<string>();

        using var client = new LoginTestClient(fixture.WebPort);

        // Act
        for (var count = 0; count < LoginThrottle.MaxFailures; count++)
        {
            using var wrong = await client.PostLoginAsync(WrongPassword, TestContext.CancellationToken);

            AddStatusProblem(problems, $"wrong password {count + 1}", HttpStatusCode.Unauthorized, wrong);

            AddTextProblem(problems, $"wrong password {count + 1}", await wrong.Content.ReadAsStringAsync(TestContext.CancellationToken), "The password is not correct.");
        }

        using var locked = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken);

        AddStatusProblem(problems, "right password while locked", HttpStatusCode.TooManyRequests, locked);

        AddTextProblem(problems, "right password while locked", await locked.Content.ReadAsStringAsync(TestContext.CancellationToken), "Too many sign-in attempts. Try again later.");

        clock.Advance(TimeSpan.FromMinutes(15));

        using var unlocked = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken);

        AddStatusProblem(problems, "right password after 15 minutes", HttpStatusCode.Found, unlocked);
        AddLocationProblem(problems, "right password after 15 minutes", "/", unlocked);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var entries = fixture.LogEntries();
        var log = fixture.Error.ToString();

        Assert.IsEmpty(problems, $"problems: {string.Join("; ", problems)}");
        Assert.AreEqual(5, entries.Count(entry => entry.GetValueOrDefault("msg") == "login failed"), "login failed");
        Assert.ContainsSingle(entries.Where(entry => entry.GetValueOrDefault("msg") == "login locked"), "login locked");
        Assert.ContainsSingle(entries.Where(entry => entry.GetValueOrDefault("msg") == "login succeeded"), "login succeeded");
        Assert.DoesNotContain(WrongPassword, log, "the wrong password is not logged");
        Assert.DoesNotContain(BackendFixture.Password, log, "the right password is not logged");
    }

    /// <summary>
    /// A session ends after an hour without activity and 12 hours after the sign-in even with activity.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeEndsSessionAfterIdleAndAbsoluteTimeout()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var clock = new FakeTimeProvider();
        var run = await StartAsync(fixture, stop.Token, clock);
        var problems = new List<string>();

        using var idle = new LoginTestClient(fixture.WebPort);
        using var active = new LoginTestClient(fixture.WebPort);

        // Act
        await SignInAsync(idle);
        await AddSignedInProblemsAsync(problems, "just signed in", idle);

        clock.Advance(TimeSpan.FromMinutes(61));

        using var afterIdle = await idle.GetAsync("/", TestContext.CancellationToken);

        AddStatusProblem(problems, "after 61 idle minutes", HttpStatusCode.Found, afterIdle);
        AddLocationProblem(problems, "after 61 idle minutes", "/login", afterIdle);

        await SignInAsync(active);

        for (var count = 0; count < 14; count++)
        {
            clock.Advance(TimeSpan.FromMinutes(50));
            await AddSignedInProblemsAsync(problems, $"active session after {(count + 1) * 50} minutes", active);
        }

        clock.Advance(TimeSpan.FromMinutes(30));

        using var afterLifetime = await active.GetAsync("/", TestContext.CancellationToken);

        AddStatusProblem(problems, "12 hours 10 minutes after the sign-in", HttpStatusCode.Found, afterLifetime);
        AddLocationProblem(problems, "12 hours 10 minutes after the sign-in", "/login", afterLifetime);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(problems, $"problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// Signing out ends the session on the server, so a copy of its cookie is no longer accepted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeLogoutEndsSessionOnServer()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);
        var problems = new List<string>();

        using var client = new LoginTestClient(fixture.WebPort);
        using var copy = new LoginTestClient(fixture.WebPort);

        await SignInAsync(client);
        copy.CopyCookiesFrom(client);
        await AddSignedInProblemsAsync(problems, "the copy before sign-out", copy);

        // Act
        using var logout = await client.PostLogoutAsync(TestContext.CancellationToken);
        using var home = await copy.GetAsync("/", TestContext.CancellationToken);
        using var negotiate = await copy.SendAsync(HttpMethod.Post, Negotiate, new ByteArrayContent([]), null, TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        AddStatusProblem(problems, "POST /logout", HttpStatusCode.SeeOther, logout);
        AddLocationProblem(problems, "POST /logout", "/login", logout);
        AddStatusProblem(problems, "GET / with the copied cookie", HttpStatusCode.Found, home);
        AddLocationProblem(problems, "GET / with the copied cookie", "/login", home);
        AddStatusProblem(problems, "negotiate with the copied cookie", HttpStatusCode.Unauthorized, negotiate);

        var deleted = LoginTestClient.SetCookie(logout, LoginTestClient.SessionCookie);

        Assert.IsEmpty(problems, $"problems: {string.Join("; ", problems)}");
        Assert.IsNotNull(deleted, "the response deletes the session cookie");
        Assert.IsTrue(deleted.StartsWith(LoginTestClient.SessionCookie + "=;", StringComparison.Ordinal), $"the deleted cookie has no value: {deleted}");
        Assert.IsFalse(client.HasSession, "the client dropped the cookie");
        Assert.ContainsSingle(fixture.LogEntries().Where(entry => entry.GetValueOrDefault("msg") == "logged out"), "logged out is logged once");
    }

    /// <summary>
    /// The two log pages need a session on the web port (anonymous requests are redirected to the login page), answer 200 with the security headers to a signed-in user and 404 on the ingest port, with the real store wired.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeServesTheLogPagesOnlyOnWebPort()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await StartAsync(fixture, stop.Token);

        using var client = new LoginTestClient(fixture.WebPort);
        using var ingestClient = CreateClient();

        var paths = new[] { "/logs", "/logs/templates" };
        var problems = new List<string>();

        // Act
        foreach (var path in paths)
        {
            using var anonymous = await client.GetAsync(path, TestContext.CancellationToken);

            AddStatusProblem(problems, $"anonymous GET {path}", HttpStatusCode.Found, anonymous);
            AddLocationProblem(problems, $"anonymous GET {path}", "/login", anonymous);
        }

        await SignInAsync(client);

        foreach (var path in paths)
        {
            using var web = await client.GetAsync(path, TestContext.CancellationToken);
            using var ingest = await ingestClient.GetAsync($"http://127.0.0.1:{fixture.IngestPort}{path}", TestContext.CancellationToken);

            AddStatusProblem(problems, $"signed-in GET web {path}", HttpStatusCode.OK, web);
            AddStatusProblem(problems, $"GET ingest {path}", HttpStatusCode.NotFound, ingest);
            AddHeaderProblems(problems, path, web, "Content-Security-Policy", SecurityHeadersMiddleware.ContentSecurityPolicy);
            AddHeaderProblems(problems, path, web, "X-Content-Type-Options", SecurityHeadersMiddleware.ContentTypeOptions);
            AddHeaderProblems(problems, path, web, "Referrer-Policy", SecurityHeadersMiddleware.ReferrerPolicy);
            AddHeaderProblems(problems, path, web, "X-Frame-Options", SecurityHeadersMiddleware.FrameOptions);
        }

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(problems, $"problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// The explorer the pages get is one gated explorer with the stated slots over the store, the same for every scope, and the pages get the system clock.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeGatesTheLogExplorer()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        IServiceProvider? services = null;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ServeHooks
                    {
                        Built = provider => services = provider,
                        Started = _ => started.TrySetResult()
                    };
        var run = fixture.RunAsync([], hooks, stop.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        Assert.IsNotNull(services, "the Built hook ran before the service started");

        // Act
        var root = services.GetRequiredService<ILogExplorer>();

        using var first = services.CreateScope();
        using var second = services.CreateScope();

        var inFirst = first.ServiceProvider.GetRequiredService<ILogExplorer>();
        var inSecond = second.ServiceProvider.GetRequiredService<ILogExplorer>();
        var clock = services.GetRequiredService<TimeProvider>();
        var store = services.GetRequiredService<ILogSearcher>();
        var fields = typeof(GatedLogExplorer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(field => field.GetValue(root)).ToArray();

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsInstanceOfType<GatedLogExplorer>(root, "the explorer is gated");
        Assert.AreSame(root, inFirst, "the first scope gets the same explorer, so the slots count across circuits");
        Assert.AreSame(root, inSecond, "the second scope gets the same explorer");
        Assert.AreEqual(LogsView.MaxConcurrentQueries, fields.OfType<SemaphoreSlim>().Concat(fields.OfType<WebReaderGate>().SelectMany(FieldValues).OfType<SemaphoreSlim>()).Single().CurrentCount, "the slots of an idle gate, held by the explorer or by the gate it shares");
        Assert.AreSame<object>(store, fields.OfType<ILogExplorer>().Single(), "the gate wraps the store");
        Assert.AreSame(TimeProvider.System, clock, "the pages get the system clock");
    }

    /// <summary>
    /// The logs explorer, the incident browser and the reader of the incident window are built on one gate of the stated slots over the store, the same for every scope.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ServeSharesOneReaderGateBetweenLogsAndIncidents()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        IServiceProvider? services = null;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ServeHooks
                    {
                        Built = provider => services = provider,
                        Started = _ => started.TrySetResult()
                    };
        var run = fixture.RunAsync([], hooks, stop.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        Assert.IsNotNull(services, "the Built hook ran before the service started");

        // Act
        var explorer = services.GetRequiredService<ILogExplorer>();
        var browser = services.GetRequiredService<IIncidentBrowser>();
        var window = services.GetRequiredService<IncidentWindowReader>();
        var gate = services.GetRequiredService<WebReaderGate>();
        var store = services.GetRequiredService<ILogSearcher>();

        using var scope = services.CreateScope();

        var browserInScope = scope.ServiceProvider.GetRequiredService<IIncidentBrowser>();
        var windowInScope = scope.ServiceProvider.GetRequiredService<IncidentWindowReader>();
        var gateInScope = scope.ServiceProvider.GetRequiredService<WebReaderGate>();
        var records = FieldValues(window).OfType<IRecordReader>().ToArray();

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsInstanceOfType<GatedLogExplorer>(explorer, "the explorer is gated");
        Assert.IsInstanceOfType<GatedIncidentBrowser>(browser, "the browser is gated");
        Assert.AreSame(browser, browserInScope, "the browser is the same for every scope");
        Assert.AreSame(window, windowInScope, "the window reader is the same for every scope");
        Assert.AreSame(gate, gateInScope, "the gate is the same for every scope");
        Assert.AreSame(gate, FieldValues(explorer).OfType<WebReaderGate>().Single(), "the explorer uses the gate");
        Assert.AreSame(gate, FieldValues(browser).OfType<WebReaderGate>().Single(), "the browser uses the same gate");
        Assert.HasCount(1, records, "the window reader holds one record reader");
        Assert.IsInstanceOfType<GatedRecordReader>(records[0], "the record reader of the window reader is gated");
        Assert.AreSame(gate, FieldValues(records[0]).OfType<WebReaderGate>().Single(), "the record reader uses the same gate");
        Assert.AreEqual(LogsView.MaxConcurrentQueries, FieldValues(gate).OfType<SemaphoreSlim>().Single().CurrentCount, "the slots of the idle gate");
        Assert.AreSame<object>(store, FieldValues(explorer).OfType<ILogExplorer>().Single(), "the explorer wraps the store");
        Assert.AreSame<object>(store, FieldValues(browser).OfType<IIncidentBrowser>().Single(), "the browser wraps the store");
        Assert.AreSame<object>(store, FieldValues(records[0]).OfType<IRecordReader>().Single(), "the record reader wraps the store");
    }

    /// <summary>
    /// Reads the values of the instance fields of an object.
    /// </summary>
    /// <param name="instance">The object</param>
    /// <returns>The values</returns>
    private static object?[] FieldValues(object instance)
    {
        return instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(field => field.GetValue(instance)).ToArray();
    }

    /// <summary>
    /// Creates a client with a short timeout that does not follow redirects.
    /// </summary>
    /// <returns>The client</returns>
    private static HttpClient CreateClient()
    {
        return new HttpClient(new HttpClientHandler
                              {
                                  AllowAutoRedirect = false
                              })
               {
                   Timeout = TimeSpan.FromSeconds(10)
               };
    }

    /// <summary>
    /// Reads the values of a response header.
    /// </summary>
    /// <param name="response">The response</param>
    /// <param name="name">The name of the header</param>
    /// <returns>The values, empty when the header is absent</returns>
    private static string[] HeaderValues(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.ToArray() : [];
    }

    /// <summary>
    /// Adds a problem when a header does not have exactly the one expected value.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="path">The requested path</param>
    /// <param name="response">The response</param>
    /// <param name="name">The name of the header</param>
    /// <param name="expected">The expected value</param>
    private static void AddHeaderProblems(List<string> problems, string path, HttpResponseMessage response, string name, string expected)
    {
        var values = HeaderValues(response, name);

        if (values.Length != 1 || values[0] != expected)
        {
            problems.Add($"{path} {name}: [{string.Join(" | ", values)}]");
        }
    }

    /// <summary>
    /// Adds a problem when a response does not have the expected status.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="request">The description of the request</param>
    /// <param name="expected">The expected status</param>
    /// <param name="response">The response</param>
    private static void AddStatusProblem(List<string> problems, string request, HttpStatusCode expected, HttpResponseMessage response)
    {
        if (response.StatusCode != expected)
        {
            problems.Add($"{request} answered {(int)response.StatusCode}, expected {(int)expected}");
        }
    }

    /// <summary>
    /// Adds a problem when a response does not redirect to the expected place.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="request">The description of the request</param>
    /// <param name="expected">The expected <c>Location</c></param>
    /// <param name="response">The response</param>
    private static void AddLocationProblem(List<string> problems, string request, string expected, HttpResponseMessage response)
    {
        var actual = LoginTestClient.LocationOf(response);

        if (actual != expected)
        {
            problems.Add($"{request} redirected to {actual ?? "nowhere"}, expected {expected}");
        }
    }

    /// <summary>
    /// Adds a problem when a page does not contain a text.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="label">The description of the request</param>
    /// <param name="body">The page</param>
    /// <param name="expected">The text that must be in it</param>
    private static void AddTextProblem(List<string> problems, string label, string body, string expected)
    {
        if (body.Contains(expected, StringComparison.Ordinal))
        {
            return;
        }

        problems.Add($"{label}: the text \"{expected}\" is missing");
    }

    /// <summary>
    /// Adds the problems of a <c>Set-Cookie</c> header: flags that are missing, and a <c>Secure</c> flag that does not match.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="label">The description of the cookie</param>
    /// <param name="setCookie">The header value, <c>null</c> when there was none</param>
    /// <param name="sameSite">The expected <c>SameSite</c> value, lower case</param>
    /// <param name="secure">Whether the cookie must be <c>Secure</c></param>
    /// <param name="session">Whether it is the session cookie, which must have no expiry</param>
    private static void AddCookieProblems(List<string> problems, string label, string? setCookie, string sameSite, bool secure, bool session)
    {
        if (setCookie is null)
        {
            problems.Add($"{label}: no Set-Cookie header");

            return;
        }

        var attributes = LoginTestClient.CookieAttributes(setCookie);

        foreach (var required in new[] { "httponly", $"samesite={sameSite}", "path=/" })
        {
            if (attributes.Contains(required))
            {
                continue;
            }

            problems.Add($"{label}: {required} is missing in [{setCookie}]");
        }

        if (attributes.Contains("secure") != secure)
        {
            problems.Add($"{label}: secure should be {secure} in [{setCookie}]");
        }

        if (session && attributes.Any(attribute => attribute.StartsWith("expires=", StringComparison.Ordinal) || attribute.StartsWith("max-age=", StringComparison.Ordinal)))
        {
            problems.Add($"{label}: the cookie has an expiry in [{setCookie}]");
        }
    }

    /// <summary>
    /// Adds the problems of a refused form post: not 400, an error status, or a session cookie.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="label">The description of the post</param>
    /// <param name="response">The response</param>
    /// <param name="anonymous">Whether the poster has no session, so that no session cookie may be set</param>
    private static void AddRefusedProblems(List<string> problems, string label, HttpResponseMessage response, bool anonymous = true)
    {
        AddStatusProblem(problems, label, HttpStatusCode.BadRequest, response);

        if (anonymous && LoginTestClient.SetCookie(response, LoginTestClient.SessionCookie) is not null)
        {
            problems.Add($"{label}: a session cookie was set");
        }
    }

    /// <summary>
    /// Adds a problem when the client no longer gets the home page.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="label">The description of the moment</param>
    /// <param name="client">The client</param>
    /// <returns>A task that completes when the check is done</returns>
    private async Task AddSignedInProblemsAsync(List<string> problems, string label, LoginTestClient client)
    {
        using var home = await client.GetAsync("/", TestContext.CancellationToken);

        AddStatusProblem(problems, $"GET / {label}", HttpStatusCode.OK, home);
    }

    /// <summary>
    /// Starts the service of the fixture and waits until it listens.
    /// </summary>
    /// <param name="fixture">The fixture</param>
    /// <param name="stop">Stops the run</param>
    /// <param name="clock">The clock of the service, <c>null</c> for the real one</param>
    /// <returns>A task that returns the task of the run</returns>
    private Task<Task<int>> StartAsync(BackendFixture fixture, CancellationToken stop, TimeProvider? clock = null)
    {
        return fixture.StartServiceAsync(stop, TestContext.CancellationToken, clock);
    }

    /// <summary>
    /// Signs in with the password of the fixture and expects the redirect to the home page.
    /// </summary>
    /// <param name="client">The client</param>
    /// <returns>A task that completes when the client is signed in</returns>
    private async Task SignInAsync(LoginTestClient client)
    {
        using var response = await client.PostLoginAsync(BackendFixture.Password, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode, "the sign-in is answered with a redirect");
        Assert.AreEqual("/", LoginTestClient.LocationOf(response), "the redirect goes to the home page");
        Assert.IsTrue(client.HasSession, "the client holds a session cookie");
    }

    #endregion // Methods
}