using System.Net;
using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="IpJson"/> and the converters built on it
/// </summary>
[TestClass]
public class IpJsonTests
{
    #region Methods

    /// <summary>
    /// Valid addresses are parsed.
    /// </summary>
    /// <param name="text">The address</param>
    [TestMethod]
    [DataRow("1.2.3.4")]
    [DataRow("255.255.255.255")]
    [DataRow("::1")]
    [DataRow("2001:db8::1")]
    public void IpJsonTryParseAddressAcceptsAddress(string text)
    {
        // Act
        var parsed = IpJson.TryParseAddress(text, out var address);

        // Assert
        Assert.IsTrue(parsed, "address is accepted");
        Assert.IsNotNull(address, "address is set");
    }

    /// <summary>
    /// Invalid addresses are refused.
    /// </summary>
    /// <param name="text">The text</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("1.2.3")]
    [DataRow("127.1")]
    [DataRow("01.2.3.4")]
    [DataRow("256.1.1.1")]
    [DataRow("1.2.3.4\n")]
    [DataRow("::g")]
    [DataRow("host")]
    public void IpJsonTryParseAddressRefusesText(string text)
    {
        // Act
        var parsed = IpJson.TryParseAddress(text, out var address);

        // Assert
        Assert.IsFalse(parsed, "text is refused");
        Assert.IsNull(address, "address is not set");
    }

    /// <summary>
    /// Valid endpoints are parsed.
    /// </summary>
    /// <param name="text">The endpoint</param>
    /// <param name="port">The expected port</param>
    [TestMethod]
    [DataRow("1.2.3.4:80", 80)]
    [DataRow("[::1]:443", 443)]
    [DataRow("0.0.0.0:0", 0)]
    [DataRow("[2001:db8::1]:65535", 65535)]
    public void IpJsonTryParseEndpointAcceptsEndpoint(string text, int port)
    {
        // Act
        var parsed = IpJson.TryParseEndpoint(text, out var endpoint);

        // Assert
        Assert.IsTrue(parsed, "endpoint is accepted");
        Assert.AreEqual(port, endpoint!.Port, "port");
    }

    /// <summary>
    /// Invalid endpoints are refused.
    /// </summary>
    /// <param name="text">The text</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("1.2.3.4")]
    [DataRow("1.2.3.4:")]
    [DataRow(":80")]
    [DataRow("1.2.3.4:65536")]
    [DataRow("1.2.3.4:8x")]
    [DataRow("::1:80")]
    [DataRow("[1.2.3.4]:80")]
    [DataRow("[::1:80")]
    [DataRow("[]:80")]
    [DataRow("host:80")]
    public void IpJsonTryParseEndpointRefusesText(string text)
    {
        // Act
        var parsed = IpJson.TryParseEndpoint(text, out var endpoint);

        // Assert
        Assert.IsFalse(parsed, "text is refused");
        Assert.IsNull(endpoint, "endpoint is not set");
    }

    /// <summary>
    /// The converters round-trip a value, read an empty text as no value and reject garbage.
    /// </summary>
    [TestMethod]
    public void IpJsonConvertersRoundTripAndRejectGarbage()
    {
        // Arrange
        var listener = new Listener
                       {
                           Proto = "tcp",
                           Local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 22)
                       };

        // Act
        var json = JsonSerializer.Serialize(listener, PayloadRegistry.Options);
        var back = JsonSerializer.Deserialize<Listener>(json, PayloadRegistry.Options);
        var empty = JsonSerializer.Deserialize<Listener>("{\"proto\":\"tcp\",\"local\":\"\"}", PayloadRegistry.Options);
        var remote = JsonSerializer.Deserialize<RemoteCount>("{\"addr\":\"\",\"count\":1}", PayloadRegistry.Options);

        // Assert
        Assert.AreEqual("10.0.0.1:22", back!.Local!.ToString(), "round trip of an endpoint");
        Assert.IsNull(empty!.Local, "an empty text is no endpoint");
        Assert.IsNull(remote!.Addr, "an empty text is no address");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<Listener>("{\"local\":\"nope\"}", PayloadRegistry.Options), "garbage endpoint");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<RemoteCount>("{\"addr\":\"nope\"}", PayloadRegistry.Options), "garbage address");
        Assert.AreEqual("{\"addr\":\"2001:db8::1\",\"count\":2}",
                        JsonSerializer.Serialize(new RemoteCount
                                                 {
                                                     Addr = IPAddress.Parse("2001:db8::1"),
                                                     Count = 2
                                                 },
                                                 PayloadRegistry.Options),
                        "address is written canonically");
    }

    #endregion // Methods
}