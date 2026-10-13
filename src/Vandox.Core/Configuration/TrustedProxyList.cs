using System.Net;

namespace Vandox.Core.Configuration;

#pragma warning disable RH2003, S2325

/// <summary>
/// Parses the option <c>web.trusted_proxies</c>.
/// </summary>
public static class TrustedProxyList
{
    #region Constants

    /// <summary>
    /// The most entries the option may hold.
    /// </summary>
    public const int MaxEntries = 16;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Parses a comma-separated list of IP addresses and networks.
    /// </summary>
    /// <param name="text">The option value</param>
    /// <param name="networks">The parsed networks; empty when the text is not accepted</param>
    /// <returns><c>true</c> when the text is accepted</returns>
    public static bool TryParse(string text, out IReadOnlyList<IPNetwork> networks)
    {
        networks = [];

        throw new NotImplementedException();
    }

    #endregion // Methods
}