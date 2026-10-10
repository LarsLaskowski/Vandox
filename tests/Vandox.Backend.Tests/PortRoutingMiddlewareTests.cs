using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

using Vandox.Backend.Hosting;

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
                                                   });
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
                                                   });

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

    #endregion // Methods
}