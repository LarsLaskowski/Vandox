using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyServiceNames"/>
/// </summary>
[TestClass]
public class LegacyServiceNamesTests
{
    #region Methods

    /// <summary>
    /// The names of the Ubuntu service table map to their port for the protocol they are listed for, aliases included.
    /// </summary>
    /// <param name="name">The service name</param>
    /// <param name="udp">Whether the protocol is UDP</param>
    /// <param name="expected">The expected port</param>
    [TestMethod]
    [DataRow("ssh", false, 22)]
    [DataRow("domain", true, 53)]
    [DataRow("domain", false, 53)]
    [DataRow("imap2", false, 143)]
    [DataRow("imap", false, 143)]
    [DataRow("submissions", false, 465)]
    [DataRow("smtps", false, 465)]
    [DataRow("submission", false, 587)]
    [DataRow("mysql", false, 3306)]
    [DataRow("fsp", true, 21)]
    [DataRow("http", false, 80)]
    [DataRow("https", false, 443)]
    [DataRow("smtp", false, 25)]
    [DataRow("imaps", false, 993)]
    [DataRow("pop3s", false, 995)]
    [DataRow("ntp", true, 123)]
    [DataRow("http-alt", false, 8080)]
    [DataRow("webcache", false, 8080)]
    public void LegacyServiceNamesTryGetPortMapsUbuntuNames(string name, bool udp, int expected)
    {
        // Act
        var found = LegacyServiceNames.TryGetPort(name, udp, out var port);

        // Assert
        Assert.IsTrue(found, "the name is known");
        Assert.AreEqual(expected, port, "the port");
    }

    /// <summary>
    /// A name that is unknown, or not listed for the protocol, has no port.
    /// </summary>
    /// <param name="name">The service name</param>
    /// <param name="udp">Whether the protocol is UDP</param>
    [TestMethod]
    [DataRow("nosuchservice", false)]
    [DataRow("nosuchservice", true)]
    [DataRow("", false)]
    [DataRow("fsp", false)]
    [DataRow("ntp", false)]
    [DataRow("ssh ", false)]
    [DataRow("22", false)]
    [DataRow("*", false)]
    public void LegacyServiceNamesTryGetPortRefusesUnknownNames(string name, bool udp)
    {
        // Act
        var found = LegacyServiceNames.TryGetPort(name, udp, out _);

        // Assert
        Assert.IsFalse(found, "the name has no port for the protocol");
    }

    #endregion // Methods
}