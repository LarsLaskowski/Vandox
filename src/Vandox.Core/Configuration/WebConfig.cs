using System.Net;

namespace Vandox.Core.Configuration;

#pragma warning disable RH2003, S2325

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
        throw new NotImplementedException();
    }

    #endregion // Methods
}