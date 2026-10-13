using System.Net;
using System.Net.Sockets;

using Vandox.Core.Model;

namespace Vandox.Core.Configuration;

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

    private const int MinPrefixV4 = 8;
    private const int MinPrefixV6 = 16;
    private const int MaxPrefixDigits = 3;

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

        if (text.Length == 0)
        {
            return true;
        }

        var entries = text.Split(',');

        if (entries.Length > MaxEntries)
        {
            return false;
        }

        var parsed = new List<IPNetwork>(entries.Length);

        foreach (var entry in entries)
        {
            if (TryParseEntry(entry.Trim(' '), out var network))
            {
                parsed.Add(network);

                continue;
            }

            return false;
        }

        networks = parsed;

        return true;
    }

    /// <summary>
    /// Parses one entry: an address with an optional prefix.
    /// </summary>
    /// <param name="entry">The entry, without surrounding spaces</param>
    /// <param name="network">The network</param>
    /// <returns><c>true</c> when the entry is accepted</returns>
    private static bool TryParseEntry(string entry, out IPNetwork network)
    {
        network = default;

        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? entry : entry[..slash];

        if (TryReadAddress(addressText, out var address))
        {
            return TryBuildNetwork(address, slash < 0 ? null : entry[(slash + 1)..], out network);
        }

        return false;
    }

    /// <summary>
    /// Builds the network of an address and an optional prefix text.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="prefixText">The text after the slash; <c>null</c> for a single address</param>
    /// <param name="network">The network</param>
    /// <returns><c>true</c> when the prefix is accepted and no host bit below it is set</returns>
    private static bool TryBuildNetwork(IPAddress address, string? prefixText, out IPNetwork network)
    {
        network = default;

        var isV4 = address.AddressFamily == AddressFamily.InterNetwork;
        var maxPrefix = isV4 ? 32 : 128;
        var minPrefix = isV4 ? MinPrefixV4 : MinPrefixV6;
        var prefix = maxPrefix;

        var parsed = prefixText is null || TryParsePrefix(prefixText, out prefix);

        return parsed && prefix >= minPrefix && prefix <= maxPrefix && TryMask(address, prefix, out network);
    }

    /// <summary>
    /// Builds a network from an address that has no host bit set.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="prefix">The prefix</param>
    /// <param name="network">The network</param>
    /// <returns><c>true</c> when the address is the base address of the network</returns>
    private static bool TryMask(IPAddress address, int prefix, out IPNetwork network)
    {
        // The constructor masks the host bits instead of refusing them, so the base address must be the address itself.
        var candidate = new IPNetwork(address, prefix);

        network = candidate;

        return candidate.BaseAddress.Equals(address);
    }

    /// <summary>
    /// Reads an address that may be trusted as a proxy: ASCII hex digits, dots and colons only, so no zone, bracket or port; not unspecified and not an IPv4-mapped IPv6 address.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when the text is such an address</returns>
    private static bool TryReadAddress(string text, out IPAddress address)
    {
        address = IPAddress.None;

        if (text.Length > 0 && text.All(IsAddressCharacter) && IpJson.TryParseAddress(text, out var parsed) && parsed is not null && IsTrustable(parsed))
        {
            address = parsed;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Tells whether a character may occur in an address.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for an ASCII hex digit, a dot or a colon</returns>
    private static bool IsAddressCharacter(char character)
    {
        return character is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F') or '.' or ':';
    }

    /// <summary>
    /// Tells whether an address may be trusted: it is neither the unspecified address nor an IPv4-mapped IPv6 address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when it may</returns>
    private static bool IsTrustable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a prefix of 1 to 3 ASCII digits without a leading zero.
    /// </summary>
    /// <param name="text">The text after the slash</param>
    /// <param name="prefix">The prefix</param>
    /// <returns><c>true</c> when the text is such a prefix</returns>
    private static bool TryParsePrefix(string text, out int prefix)
    {
        prefix = 0;

        if (text.Length is 0 or > MaxPrefixDigits || (text.Length > 1 && text[0] == '0') || text.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        prefix = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        return true;
    }

    #endregion // Methods
}