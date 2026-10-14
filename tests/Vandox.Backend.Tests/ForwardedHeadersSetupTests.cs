using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ForwardedHeadersSetup"/>
/// </summary>
[TestClass]
public class ForwardedHeadersSetupTests
{
    #region Methods

    /// <summary>
    /// Without a configured proxy there are no options, so the middleware is not added.
    /// </summary>
    [TestMethod]
    public void ForwardedHeadersSetupReturnsNoOptionsWithoutProxies()
    {
        // Act
        var options = ForwardedHeadersSetup.CreateOptions([]);

        // Assert
        Assert.IsNull(options, "no options for an empty list");
    }

    /// <summary>
    /// The middleware takes the client address and the scheme from the rightmost values, only from a configured proxy, and nothing else.
    /// </summary>
    /// <param name="peer">The address of the peer</param>
    /// <param name="forwardedFor">The value of <c>X-Forwarded-For</c>, empty for none</param>
    /// <param name="forwardedProto">The value of <c>X-Forwarded-Proto</c>, empty for none</param>
    /// <param name="forwardedHost">The value of <c>X-Forwarded-Host</c>, empty for none</param>
    /// <param name="address">The expected client address</param>
    /// <param name="scheme">The expected scheme</param>
    /// <param name="host">The expected host</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("10.0.0.1", "192.0.2.1, 198.51.100.7", "http, https", "", "198.51.100.7", "https", "app.local")]
    [DataRow("::ffff:10.0.0.1", "192.0.2.1, 198.51.100.7", "http, https", "", "198.51.100.7", "https", "app.local")]
    [DataRow("10.0.0.2", "192.0.2.1, 198.51.100.7", "http, https", "", "10.0.0.2", "http", "app.local")]
    [DataRow("127.0.0.1", "192.0.2.1, 198.51.100.7", "http, https", "", "127.0.0.1", "http", "app.local")]
    [DataRow("10.0.0.1", "198.51.100.7", "https", "evil.example", "198.51.100.7", "https", "app.local")]
    [DataRow("10.0.0.1", "unknown", "", "", "10.0.0.1", "http", "app.local")]
    public async Task ForwardedHeadersSetupAppliesHeadersOnlyFromConfiguredProxies(string peer, string forwardedFor, string forwardedProto, string forwardedHost, string address, string scheme, string host)
    {
        // Arrange
        var networks = new[] { System.Net.IPNetwork.Parse("10.0.0.1/32") };
        var options = ForwardedHeadersSetup.CreateOptions(networks);

        Assert.IsNotNull(options, "options for a configured proxy");

        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        var context = new DefaultHttpContext();

        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("app.local");

        if (forwardedFor.Length > 0)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (forwardedProto.Length > 0)
        {
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }

        if (forwardedHost.Length > 0)
        {
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        }

        // Act
        await middleware.Invoke(context);

        // Assert
        Assert.HasCount(1, options.KnownIPNetworks, "exactly the configured network");
        Assert.AreEqual("10.0.0.1/32", $"{options.KnownIPNetworks[0].BaseAddress}/{options.KnownIPNetworks[0].PrefixLength}", "the configured network");
        Assert.IsEmpty(options.KnownProxies, "no known proxy, so no loopback");
        Assert.AreEqual(1, options.ForwardLimit, "one hop");
        Assert.AreEqual(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders, "only the client address and the scheme");
        Assert.AreEqual(address, context.Connection.RemoteIpAddress?.ToString(), "client address");
        Assert.AreEqual(scheme, context.Request.Scheme, "scheme");
        Assert.AreEqual(host, context.Request.Host.Value, "host");
    }

    #endregion // Methods
}