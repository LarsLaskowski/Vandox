using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="TrustedProxyList"/>
/// </summary>
[TestClass]
public class TrustedProxyListTests
{
    #region Properties

    /// <summary>
    /// Gets the accepted texts with the networks they give, joined with a bar.
    /// </summary>
    public static IEnumerable<(string Text, string Networks)> AcceptedForms
    {
        get
        {
            var sixteen = Enumerable.Range(1, 16).Select(index => $"10.0.0.{index}").ToArray();

            return
            [
                (string.Empty, string.Empty),
                ("172.18.0.1", "172.18.0.1/32"),
                ("172.18.0.0/16", "172.18.0.0/16"),
                ("10.0.0.0/8", "10.0.0.0/8"),
                ("192.168.1.10/32", "192.168.1.10/32"),
                (" 172.18.0.1 , 192.168.1.10 ", "172.18.0.1/32|192.168.1.10/32"),
                ("2001:db8::1", "2001:db8::1/128"),
                ("2001:db8::/32", "2001:db8::/32"),
                ("fd00::/16", "fd00::/16"),
                ("2001:DB8::1", "2001:db8::1/128"),
                (string.Join(',', sixteen), string.Join('|', sixteen.Select(entry => entry + "/32")))
            ];
        }
    }

    /// <summary>
    /// Gets the refused texts.
    /// </summary>
    public static IEnumerable<string> RefusedForms
    {
        get
        {
            var seventeen = string.Join(',', Enumerable.Range(1, 17).Select(index => $"10.0.0.{index}"));

            return
            [
                seventeen,
                "127.1",
                "0x7f.0.0.1",
                "0177.0.0.1",
                "010.0.0.1",
                "2130706433",
                "1.2.3",
                "1.2.3.4.",
                "256.1.1.1",
                "172.18.0.0/33",
                "10.0.0.0/7",
                "0.0.0.0/0",
                "172.18.0.0/016",
                "172.18.0.0/+16",
                "172.18.0.0/",
                "172.18.0.0/ 16",
                "2001:db8::/15",
                "2001:db8::/129",
                "172.18.0.1/16",
                "2001:db8::1/32",
                "0.0.0.0",
                "0.0.0.0/8",
                "::",
                "::/16",
                "::ffff:172.18.0.1",
                "fe80::1%eth0",
                "fe80::1%2",
                "[::1]",
                "172.18.0.1:443",
                "proxy.lan",
                "172.18.0.1,,10.0.0.1",
                "172.18.0.1,",
                ",",
                "\t172.18.0.1",
                "172.18.0.1\t",
                "172.18.0.1,\t10.0.0.1",
                "\u00A0172.18.0.1",
                "172.18.0.1\u00A0",
                "172.18.0.1,\u00A010.0.0.1",
                "\uFF11\uFF17\uFF12.18.0.1",
                "172.18.0.\uFF11"
            ];
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The accepted forms give exactly the networks they name.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="networks">The expected networks, joined with a bar</param>
    [TestMethod]
    [DynamicData(nameof(AcceptedForms))]
    public void TrustedProxyListParsesAcceptedForms(string text, string networks)
    {
        // Act
        var accepted = TrustedProxyList.TryParse(text, out var parsed);

        // Assert
        Assert.IsTrue(accepted, "the text is accepted");
        Assert.AreEqual(networks, string.Join('|', parsed.Select(network => $"{network.BaseAddress}/{network.PrefixLength}")), "networks");
    }

    /// <summary>
    /// Every other form is refused and gives no network.
    /// </summary>
    /// <param name="text">The text</param>
    [TestMethod]
    [DynamicData(nameof(RefusedForms))]
    public void TrustedProxyListRefusesOtherForms(string text)
    {
        // Act
        var accepted = TrustedProxyList.TryParse(text, out var parsed);

        // Assert
        Assert.IsFalse(accepted, "the text is refused");
        Assert.IsEmpty(parsed, "no network");
    }

    #endregion // Methods
}