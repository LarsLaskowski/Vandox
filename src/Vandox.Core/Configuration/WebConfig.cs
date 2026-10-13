using System.Net;

namespace Vandox.Core.Configuration;

/// <summary>
/// The options of the web UI listener and of the reverse proxy in front of it.
/// </summary>
public sealed class WebConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the address as <c>[host]:port</c>; the host is empty or an IP address.
    /// </summary>
    [ConfigKey("listen")]
    public string Listen { get; set; } = ":8080";

    /// <summary>
    /// Gets or sets the addresses and networks of the reverse proxies whose forwarded headers are trusted, comma-separated; empty trusts none.
    /// </summary>
    [ConfigKey("trusted_proxies")]
    public string TrustedProxies { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the trusted proxies as networks.
    /// </summary>
    /// <returns>The parsed networks</returns>
    /// <exception cref="InvalidOperationException">The option does not parse</exception>
    public IReadOnlyList<IPNetwork> GetTrustedProxies()
    {
        if (TrustedProxyList.TryParse(TrustedProxies, out var networks))
        {
            return networks;
        }

        throw new InvalidOperationException("web.trusted_proxies is not a valid list of IP addresses and networks");
    }

    #endregion // Methods
}