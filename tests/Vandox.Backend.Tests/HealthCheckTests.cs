using System.Net;

using Vandox.Backend.Cli;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for the health probe <c>vandoxd -healthcheck</c>
/// </summary>
[TestClass]
public class HealthCheckTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The probe goes to loopback for an empty or unspecified host and keeps a specific address.
    /// </summary>
    /// <param name="listen">The web listen address</param>
    /// <param name="url">The expected URL</param>
    [TestMethod]
    [DataRow(":8080", "http://127.0.0.1:8080/healthz")]
    [DataRow("0.0.0.0:9", "http://127.0.0.1:9/healthz")]
    [DataRow("[::]:9", "http://[::1]:9/healthz")]
    [DataRow("[::ffff:0.0.0.0]:9", "http://127.0.0.1:9/healthz")]
    [DataRow("192.168.1.5:8080", "http://192.168.1.5:8080/healthz")]
    [DataRow("[2001:db8::1]:80", "http://[2001:db8::1]:80/healthz")]
    public void HealthCheckHealthUrlUsesLoopbackForUnspecifiedHosts(string listen, string url)
    {
        // Act
        var result = HealthCheckCommand.HealthUrl(listen);

        // Assert
        Assert.AreEqual(url, result, "URL");
    }

    /// <summary>
    /// Status 200 is healthy, any other status and a failed request are not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task HealthCheckReportsStatusAndErrors()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var healthy = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var down = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var redirect = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect));
        var refused = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var slow = new StubHandler(_ => throw new TaskCanceledException());
        var errors = new StringWriter();

        // Act
        var ok = await HealthCheckCommand.RunAsync(fixture.ConfigPath, errors, healthy, TestContext.CancellationToken);
        var unavailable = await HealthCheckCommand.RunAsync(fixture.ConfigPath, errors, down, TestContext.CancellationToken);
        var redirected = await HealthCheckCommand.RunAsync(fixture.ConfigPath, errors, redirect, TestContext.CancellationToken);
        var failed = await HealthCheckCommand.RunAsync(fixture.ConfigPath, errors, refused, TestContext.CancellationToken);
        var timedOut = await HealthCheckCommand.RunAsync(fixture.ConfigPath, errors, slow, TestContext.CancellationToken);
        var missing = await HealthCheckCommand.RunAsync(Path.Combine(fixture.Folder.Path, "missing.yaml"), errors, healthy, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, ok, "healthy");
        Assert.AreEqual(1, unavailable, "503");
        Assert.AreEqual(1, redirected, "a redirect is not followed and not healthy");
        Assert.AreEqual(1, failed, "connection error");
        Assert.AreEqual(1, timedOut, "timeout");
        Assert.AreEqual(1, missing, "missing configuration");
        Assert.Contains("unexpected status 503", errors.ToString(), "status message");
        Assert.Contains("unexpected status 302", errors.ToString(), "redirect message");
        Assert.Contains("connection refused", errors.ToString(), "connection message");
        Assert.Contains("timed out", errors.ToString(), "timeout message");
        Assert.Contains("no such file or directory", errors.ToString(), "configuration message");
        Assert.AreEqual($"http://127.0.0.1:{fixture.WebPort}/healthz", healthy.Requests[0], "the probe asks the configured web address");
    }

    #endregion // Methods
}