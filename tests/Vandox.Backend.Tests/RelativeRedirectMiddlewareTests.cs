using Microsoft.AspNetCore.Http;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="RelativeRedirectMiddleware"/>
/// </summary>
[TestClass]
public class RelativeRedirectMiddlewareTests
{
    #region Methods

    /// <summary>
    /// An absolute address of the request's own scheme and host becomes its path, query and fragment; every other value stays as it is.
    /// </summary>
    /// <param name="scheme">The scheme of the request</param>
    /// <param name="host">The <c>Host</c> of the request</param>
    /// <param name="location">The <c>Location</c> of the response</param>
    /// <param name="expected">The <c>Location</c> to send</param>
    [TestMethod]
    [DataRow("http", "vandox.home.example", "http://vandox.home.example/", "/")]
    [DataRow("http", "Vandox.Home.Example", "http://vandox.home.example/logs?q=disk#top", "/logs?q=disk#top")]
    [DataRow("http", "127.0.0.1:8080", "http://127.0.0.1:8080/", "/")]
    [DataRow("http", "nas:80", "http://nas/login", "/login")]
    [DataRow("http", "[::1]:8080", "http://[::1]:8080/incidents/7", "/incidents/7")]
    [DataRow("http", "vandox.home.example", "https://vandox.home.example/", "https://vandox.home.example/")]
    [DataRow("http", "vandox.home.example", "http://other.example/", "http://other.example/")]
    [DataRow("http", "127.0.0.1:8080", "http://127.0.0.1:8081/", "http://127.0.0.1:8081/")]
    [DataRow("http", "vandox.home.example", "http://vandox.home.example//other.example/", "http://vandox.home.example//other.example/")]
    [DataRow("http", "vandox.home.example", "/login", "/login")]
    [DataRow("http", "", "http://vandox.home.example/", "http://vandox.home.example/")]
    public void RelativeRedirectMiddlewareRelativeLocationKeepsOnlyOwnOriginPaths(string scheme, string host, string location, string expected)
    {
        // Act
        var actual = RelativeRedirectMiddleware.RelativeLocation(scheme, new HostString(host), location);

        // Assert
        Assert.AreEqual(expected, actual, $"Location for {scheme}://{host} and {location}");
    }

    /// <summary>
    /// The rewrite replaces a single own-origin <c>Location</c> and leaves a response without one alone.
    /// </summary>
    [TestMethod]
    public void RelativeRedirectMiddlewareRewriteReplacesOwnOriginLocation()
    {
        // Arrange
        var redirect = NewContext();
        var plain = NewContext();

        redirect.Response.Headers.Location = "http://vandox.home.example/";

        // Act
        RelativeRedirectMiddleware.Rewrite(redirect);
        RelativeRedirectMiddleware.Rewrite(plain);

        // Assert
        Assert.AreEqual("/", redirect.Response.Headers.Location.ToString(), "the redirect keeps only the path");
        Assert.IsEmpty(plain.Response.Headers.Location, "no Location is added");
    }

    /// <summary>
    /// The middleware calls the rest of the pipeline exactly once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task RelativeRedirectMiddlewareCallsNext()
    {
        // Arrange
        var calls = 0;
        var context = NewContext();
        var middleware = new RelativeRedirectMiddleware(_ =>
                                                        {
                                                            calls++;

                                                            return Task.CompletedTask;
                                                        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.AreEqual(1, calls, "the next delegate is called exactly once");
    }

    /// <summary>
    /// Creates a request to <c>http://vandox.home.example/</c>.
    /// </summary>
    /// <returns>The request</returns>
    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();

        context.Request.Scheme = "http";
        context.Request.Host = new HostString("vandox.home.example");

        return context;
    }

    #endregion // Methods
}