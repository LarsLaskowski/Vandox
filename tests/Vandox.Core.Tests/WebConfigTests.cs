using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="WebConfig"/>
/// </summary>
[TestClass]
public class WebConfigTests
{
    #region Methods

    /// <summary>
    /// The option is empty by default and gives no network, and a value gives its networks.
    /// </summary>
    [TestMethod]
    public void WebConfigReturnsTrustedProxies()
    {
        // Arrange
        var defaults = new WebConfig();
        var configured = new WebConfig
                         {
                             TrustedProxies = "172.18.0.1, 10.0.0.0/8, 2001:db8::/32"
                         };

        // Act
        var none = defaults.GetTrustedProxies();
        var networks = configured.GetTrustedProxies();

        // Assert
        Assert.AreEqual(string.Empty, defaults.TrustedProxies, "empty by default");
        Assert.IsEmpty(none, "no network by default");
        Assert.AreEqual(":8080", defaults.Listen, "the listen address keeps its default");
        Assert.AreEqual("172.18.0.1/32|10.0.0.0/8|2001:db8::/32", string.Join('|', networks.Select(network => $"{network.BaseAddress}/{network.PrefixLength}")), "networks");
    }

    #endregion // Methods
}