using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Vandox.Backend.Hosting;
using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="PortRoutingMiddleware"/> and <see cref="ListenAddress"/>
/// </summary>
[TestClass]
public class PortRoutingMiddlewareTests
{
    #region Methods

    /// <summary>
    /// A web connection reaches the rest of the pipeline, an ingest connection gets 404.
    /// </summary>
    /// <param name="label">The label of the listener</param>
    /// <param name="reached">Whether the rest of the pipeline is reached</param>
    /// <param name="status">The expected status</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("web", true, 200)]
    [DataRow("ingest", false, 404)]
    [DataRow(null, false, 404)]
    public async Task PortRoutingMiddlewareSeparatesListeners(string? label, bool reached, int status)
    {
        // Arrange
        var called = false;
        var middleware = new PortRoutingMiddleware(_ =>
                                                   {
                                                       called = true;

                                                       return Task.CompletedTask;
                                                   },
                                                   CreateIngest());
        var context = new DefaultHttpContext();
        var items = new Dictionary<object, object?>();

        if (label is not null)
        {
            items[ListenerRoutes.ItemName] = label;
        }

        context.Features.Set<IConnectionItemsFeature>(new ConnectionItems(items));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.AreEqual(reached, called, "pipeline reached");
        Assert.AreEqual(status, context.Response.StatusCode, "status");
    }

    /// <summary>
    /// An ingest connection hands only a POST to exactly /v1/batches (ordinal, no trailing slash, the query ignored) to the ingest endpoint; every other
    /// request stays 404. A disabled endpoint answers 503, so that status shows who got the request.
    /// </summary>
    /// <param name="method">The method</param>
    /// <param name="path">The path</param>
    /// <param name="query">The query string</param>
    /// <param name="status">The expected status</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("POST", "/v1/batches", "", 503)]
    [DataRow("POST", "/v1/batches", "?token=x", 503)]
    [DataRow("POST", "/v1/batches/", "", 404)]
    [DataRow("POST", "/V1/batches", "", 404)]
    [DataRow("POST", "/v1/batches2", "", 404)]
    [DataRow("POST", "/v1", "", 404)]
    [DataRow("GET", "/v1/batches", "", 404)]
    [DataRow("PUT", "/v1/batches", "", 404)]
    [DataRow("POST", "/healthz", "", 404)]
    [DataRow("POST", "/", "", 404)]
    public async Task PortRoutingMiddlewareSendsOnlyPostV1BatchesToIngest(string method, string path, string query, int status)
    {
        // Arrange
        var called = false;
        var middleware = new PortRoutingMiddleware(_ =>
                                                   {
                                                       called = true;

                                                       return Task.CompletedTask;
                                                   },
                                                   CreateIngest());
        var context = new DefaultHttpContext();

        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Features.Set<IConnectionItemsFeature>(new ConnectionItems(new Dictionary<object, object?>
                                                                          {
                                                                              [ListenerRoutes.ItemName] = ListenerRoutes.IngestLabel
                                                                          }));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.IsFalse(called, "the web pipeline never runs on an ingest connection");
        Assert.AreEqual(status, context.Response.StatusCode, "status");
    }

    /// <summary>
    /// A post to /v1/batches on a web connection goes down the web pipeline and never to the ingest endpoint.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PortRoutingMiddlewareKeepsPostV1BatchesOnWebAwayFromIngest()
    {
        // Arrange
        var called = false;
        var middleware = new PortRoutingMiddleware(_ =>
                                                   {
                                                       called = true;

                                                       return Task.CompletedTask;
                                                   },
                                                   CreateIngest());
        var context = new DefaultHttpContext();

        context.Request.Method = "POST";
        context.Request.Path = "/v1/batches";
        context.Features.Set<IConnectionItemsFeature>(new ConnectionItems(new Dictionary<object, object?>
                                                                          {
                                                                              [ListenerRoutes.ItemName] = ListenerRoutes.WebLabel
                                                                          }));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.IsTrue(called, "the web pipeline runs");
        Assert.AreEqual(200, context.Response.StatusCode, "the ingest endpoint did not answer");
    }

    /// <summary>
    /// A request without connection items is not served: the routing fails closed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PortRoutingMiddlewareRefusesRequestWithoutConnectionItems()
    {
        // Arrange
        var called = false;
        var middleware = new PortRoutingMiddleware(_ =>
                                                   {
                                                       called = true;

                                                       return Task.CompletedTask;
                                                   },
                                                   CreateIngest());

        // Act
        await middleware.InvokeAsync(new DefaultHttpContext());

        // Assert
        Assert.IsFalse(called, "pipeline not reached");
    }

    /// <summary>
    /// Listen addresses are split into host and port.
    /// </summary>
    /// <param name="value">The address</param>
    /// <param name="host">The expected host; empty for all interfaces</param>
    /// <param name="port">The expected port</param>
    [TestMethod]
    [DataRow(":8080", "", 8080)]
    [DataRow("127.0.0.1:9", "127.0.0.1", 9)]
    [DataRow("[::1]:443", "::1", 443)]
    [DataRow("[::]:80", "::", 80)]
    public void ListenAddressParseSplitsHostAndPort(string value, string host, int port)
    {
        // Act
        var address = ListenAddress.Parse(value);

        // Assert
        Assert.AreEqual(host, address.Address?.ToString() ?? string.Empty, "host");
        Assert.AreEqual(port, address.Port, "port");
    }

    /// <summary>
    /// An invalid address is refused.
    /// </summary>
    /// <param name="value">The address</param>
    [TestMethod]
    [DataRow("8080")]
    [DataRow("host:80")]
    [DataRow(":0")]
    public void ListenAddressParseRefusesInvalidAddress(string value)
    {
        // Act and Assert
        Assert.ThrowsExactly<FormatException>(() => ListenAddress.Parse(value), "invalid address");
    }

    /// <summary>
    /// Creates an ingest endpoint that the test never reaches.
    /// </summary>
    /// <returns>The endpoint</returns>
    private static IngestEndpoint CreateIngest()
    {
        return new IngestEndpoint(null, new UnusedRecordWriter(), new FreeSpace(string.Empty, _ => null), new StoppedApplicationLifetime(), TimeProvider.System, NullLoggerFactory.Instance);
    }

    #endregion // Methods
}