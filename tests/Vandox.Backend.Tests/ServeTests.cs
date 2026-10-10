using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Server.Kestrel.Core;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests that run the real service with <see cref="BackendApp"/>
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class ServeTests
{
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

        using var client = new HttpClient
                           {
                               Timeout = TimeSpan.FromSeconds(10)
                           };
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
        Assert.AreEqual(HttpStatusCode.OK, home.StatusCode, "home status");
        Assert.Contains("Vandox backend", await home.Content.ReadAsStringAsync(TestContext.CancellationToken), "the web UI page is served");
        Assert.IsFalse(home.Headers.Contains("Server"), "no server header");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode, "an unknown route is not found");
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
    }

    #endregion // Methods
}