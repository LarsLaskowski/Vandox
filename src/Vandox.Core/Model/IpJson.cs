using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Vandox.Core.Model;

/// <summary>
/// Strict parsing of IP addresses and endpoints: the textual forms a Go <c>netip</c> parser accepts, so that the
/// wire format is read the same way on both sides.
/// </summary>
public static partial class IpJson
{
    #region Constants

    private const int MaxPort = 65535;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Parses a dotted-decimal IPv4 address or an IPv6 address; brackets are rejected, and a zone (<c>%eth0</c>) is accepted and kept as a non-zero scope ID so that validation can refuse it.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when the text is an address</returns>
    public static bool TryParseAddress(string text, out IPAddress? address)
    {
        address = null;

        if (text.AsSpan().ContainsAny('[', ']'))
        {
            return false;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            if (IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // A zone that names no interface leaves the scope ID at 0; the text decides, so a zone is never lost.
                if (parsed.ScopeId == 0 && text.Contains('%', StringComparison.Ordinal))
                {
                    parsed.ScopeId = 1;
                }

                address = parsed;

                return true;
            }

            return false;
        }

        if (Ipv4Pattern().IsMatch(text) && IPAddress.TryParse(text, out var v4))
        {
            address = v4;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses <c>1.2.3.4:80</c> or <c>[::1]:80</c>.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="endpoint">The endpoint</param>
    /// <returns><c>true</c> when the text is an address with a port</returns>
    public static bool TryParseEndpoint(string text, out IPEndPoint? endpoint)
    {
        endpoint = null;

        var colon = text.LastIndexOf(':');

        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        var host = text[..colon];
        var portText = text[(colon + 1)..];
        var bracketed = host.StartsWith('[');

        if (bracketed)
        {
            if (host.EndsWith(']') && host.Length >= 3)
            {
                host = host[1..^1];
            }
            else
            {
                return false;
            }
        }

        // An IPv6 address is written in brackets, an IPv4 address without.
        if (bracketed != host.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (TryParseAddress(host, out var address)
            && address is not null
            && PortPattern().IsMatch(portText)
            && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port <= MaxPort)
        {
            endpoint = new IPEndPoint(address, port);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates the pattern of an IPv4 address in dotted decimal without leading zeros.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    /// <summary>
    /// Creates the pattern of a port number.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[0-9]{1,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();

    #endregion // Methods
}