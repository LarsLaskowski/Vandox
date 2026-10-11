using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vandox.Core.LogParsing;

/// <summary>
/// One socket line of the lsof section.
/// </summary>
internal sealed class LegacyLsofLine
{
    #region Constants

    /// <summary>
    /// The skip reason for a line of the wrong shape.
    /// </summary>
    internal const string NotRead = "lsof line not read";

    /// <summary>
    /// The skip reason for a socket whose address, port or state is not mapped.
    /// </summary>
    internal const string NotMapped = "socket address not mapped";

    private const int MaxCommandBytes = 1024;
    private const int MaxPidDigits = 10;
    private const int MaxPortDigits = 5;
    private const int MaxPort = 65535;
    private const int MaxServiceNameCharacters = 32;
    private const int MinTypeIndex = 3;
    private const int MinAddressCharacters = 2;
    private const int MaxAddressCharacters = 45;
    private const int OctetCount = 4;
    private const int MaxOctetDigits = 3;
    private const string Arrow = "->";

    #endregion // Constants

    #region Fields

    private static readonly string[] _header = ["COMMAND", "PID", "USER", "FD", "TYPE", "DEVICE", "SIZE/OFF", "NODE", "NAME"];

    private static readonly string[] _tcpStates = [
                                                      "ESTABLISHED",
                                                      "SYN_SENT",
                                                      "SYN_RECV",
                                                      "FIN_WAIT1",
                                                      "FIN_WAIT2",
                                                      "TIME_WAIT",
                                                      "CLOSE",
                                                      "CLOSE_WAIT",
                                                      "LAST_ACK",
                                                      "LISTEN",
                                                      "CLOSING",
                                                      "NEW_SYN_RECV"
                                                  ];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyLsofLine"/> class.
    /// </summary>
    /// <param name="command">The decoded command</param>
    /// <param name="pid">The process id</param>
    /// <param name="proto">The protocol</param>
    /// <param name="local">The local endpoint</param>
    /// <param name="remote">The remote endpoint; <c>null</c> for a listener</param>
    /// <param name="state">The state</param>
    /// <param name="socketKey">The key of the socket</param>
    private LegacyLsofLine(string command, int pid, string proto, IPEndPoint local, IPEndPoint? remote, string state, string socketKey)
    {
        Command = command;
        Pid = pid;
        Proto = proto;
        Local = local;
        Remote = remote;
        State = state;
        SocketKey = socketKey;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the decoded command.
    /// </summary>
    internal string Command { get; }

    /// <summary>
    /// Gets the process id.
    /// </summary>
    internal int Pid { get; }

    /// <summary>
    /// Gets the protocol: tcp, tcp6, udp or udp6.
    /// </summary>
    internal string Proto { get; }

    /// <summary>
    /// Gets the local endpoint.
    /// </summary>
    internal IPEndPoint Local { get; }

    /// <summary>
    /// Gets the remote endpoint; <c>null</c> for a listener.
    /// </summary>
    internal IPEndPoint? Remote { get; }

    /// <summary>
    /// Gets the state; empty for UDP.
    /// </summary>
    internal string State { get; }

    /// <summary>
    /// Gets the key of the socket: the text of the line from the TYPE field on.
    /// </summary>
    internal string SocketKey { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether a line is the column header of lsof.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for the header</returns>
    internal static bool IsHeader(string line)
    {
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(_header, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads a socket line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="reason"><see cref="NotRead"/> or <see cref="NotMapped"/> when not read</param>
    /// <returns>The line; <c>null</c> when not read</returns>
    internal static LegacyLsofLine? TryParse(string line, out string? reason)
    {
        reason = NotRead;

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = FindType(tokens);

        if (typeIndex < 0)
        {
            return null;
        }

        var hasState = tokens[^1].Length >= 2 && tokens[^1][0] == '(' && tokens[^1][^1] == ')';
        var nameIndex = tokens.Length - (hasState ? 2 : 1);
        var nodeIndex = nameIndex - 1;

        if (nodeIndex > typeIndex && tokens[nodeIndex] is "TCP" or "UDP" && TryParsePid(tokens[1], out var pid))
        {
            return Map(tokens, typeIndex, nameIndex, pid, out reason);
        }

        return null;
    }

    /// <summary>
    /// Maps the addresses, ports and state of a line of the right shape.
    /// </summary>
    /// <param name="tokens">The tokens of the line</param>
    /// <param name="typeIndex">The index of the TYPE column</param>
    /// <param name="nameIndex">The index of the NAME column</param>
    /// <param name="pid">The process id</param>
    /// <param name="reason"><c>null</c> when mapped, else <see cref="NotMapped"/></param>
    /// <returns>The line; <c>null</c> when the addresses, ports or state are not mapped</returns>
    private static LegacyLsofLine? Map(string[] tokens, int typeIndex, int nameIndex, int pid, out string? reason)
    {
        reason = NotMapped;

        var ipv6 = tokens[typeIndex] == "IPv6";
        var udp = tokens[nameIndex - 1] == "UDP";
        var stateToken = nameIndex < tokens.Length - 1 ? tokens[^1][1..^1] : string.Empty;
        var state = udp ? string.Empty : stateToken;

        if (TryMap(tokens[nameIndex], ipv6, udp, state, out var local, out var remote) && local is not null)
        {
            reason = null;

            return new LegacyLsofLine(DecodeCommand(tokens[0]), pid, (udp ? "udp" : "tcp") + (ipv6 ? "6" : string.Empty), local, remote, state, string.Join(' ', tokens.Skip(typeIndex)));
        }

        return null;
    }

    /// <summary>
    /// Finds the TYPE column: the first token from the fourth on that is <c>IPv4</c> or <c>IPv6</c>.
    /// </summary>
    /// <param name="tokens">The tokens of the line</param>
    /// <returns>The index; -1 when there is none</returns>
    private static int FindType(string[] tokens)
    {
        for (var index = MinTypeIndex; index < tokens.Length; index++)
        {
            if (tokens[index] is "IPv4" or "IPv6")
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the process id: 1 to 10 ASCII digits within 1 and 2,147,483,647.
    /// </summary>
    /// <param name="token">The token</param>
    /// <param name="pid">The process id</param>
    /// <returns><c>true</c> when read</returns>
    private static bool TryParsePid(string token, out int pid)
    {
        pid = 0;

        return LegacyTopLine.IsDigits(token, 1, MaxPidDigits) && int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid) && pid > 0;
    }

    /// <summary>
    /// Maps the NAME column and the state to endpoints.
    /// </summary>
    /// <param name="name">The NAME column</param>
    /// <param name="ipv6"><c>true</c> for TYPE IPv6</param>
    /// <param name="udp"><c>true</c> for UDP</param>
    /// <param name="state">The state; empty for UDP</param>
    /// <param name="local">The local endpoint</param>
    /// <param name="remote">The remote endpoint; <c>null</c> without one</param>
    /// <returns><c>true</c> when the state, the addresses and the ports are mapped</returns>
    private static bool TryMap(string name, bool ipv6, bool udp, string state, out IPEndPoint? local, out IPEndPoint? remote)
    {
        local = null;
        remote = null;

        var arrow = name.IndexOf(Arrow, StringComparison.Ordinal);
        var localText = arrow < 0 ? name : name[..arrow];
        var stateKnown = udp || Array.IndexOf(_tcpStates, state) >= 0;

        if (stateKnown && TryParseEndpoint(localText, ipv6, udp, out local))
        {
            return arrow < 0 || TryParseEndpoint(name[(arrow + Arrow.Length)..], ipv6, udp, out remote);
        }

        return false;
    }

    /// <summary>
    /// Reads an endpoint <c>ADDR:PORT</c>.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="ipv6"><c>true</c> for TYPE IPv6</param>
    /// <param name="udp"><c>true</c> for UDP</param>
    /// <param name="endpoint">The endpoint</param>
    /// <returns><c>true</c> when the address and the port are mapped</returns>
    private static bool TryParseEndpoint(string text, bool ipv6, bool udp, out IPEndPoint? endpoint)
    {
        endpoint = null;

        string addressText;
        string portText;

        if (ipv6 && text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);

            addressText = close < 0 ? string.Empty : text[..(close + 1)];
            portText = close >= 0 && close + 1 < text.Length && text[close + 1] == ':' ? text[(close + 2)..] : string.Empty;
        }
        else
        {
            var colon = text.LastIndexOf(':');

            addressText = colon < 0 ? string.Empty : text[..colon];
            portText = colon < 0 ? string.Empty : text[(colon + 1)..];
        }

        if (TryParseAddress(addressText, ipv6, out var address) && TryParsePort(portText, udp, out var port))
        {
            endpoint = new IPEndPoint(address!, port);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads an address by the strict forms only.
    /// </summary>
    /// <param name="text">The address text: <c>*</c>, a dotted quad, or an IPv6 address in brackets</param>
    /// <param name="ipv6"><c>true</c> for TYPE IPv6</param>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when read</returns>
    private static bool TryParseAddress(string text, bool ipv6, out IPAddress? address)
    {
        address = null;

        if (text == "*")
        {
            address = ipv6 ? IPAddress.IPv6Any : IPAddress.Any;

            return true;
        }

        if (ipv6)
        {
            return text.Length > 1 && text[0] == '[' && text[^1] == ']' && TryParseIpv6(text[1..^1], out address);
        }

        return TryParseQuad(text, out address);
    }

    /// <summary>
    /// Reads the text inside the brackets of an IPv6 address.
    /// </summary>
    /// <param name="inside">The text between the brackets, with an optional zone that is removed</param>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when the text is an IPv6 address without a zone</returns>
    private static bool TryParseIpv6(string inside, out IPAddress? address)
    {
        address = null;

        var percent = inside.IndexOf('%', StringComparison.Ordinal);
        var text = percent < 0 ? inside : inside[..percent];
        var lastColon = text.LastIndexOf(':');
        var firstPoint = text.IndexOf('.', StringComparison.Ordinal);
        var shapeOk = text.Length is >= MinAddressCharacters and <= MaxAddressCharacters && lastColon >= 0 && text.All(IsAddressCharacter);
        var quadOk = firstPoint < 0 || (firstPoint > lastColon && TryParseQuad(text[(lastColon + 1)..], out _));

        if (shapeOk && quadOk)
        {
            return TryParseCanonical(text, out address);
        }

        return false;
    }

    /// <summary>
    /// Reads the guarded text of an IPv6 address with the framework.
    /// </summary>
    /// <param name="text">The text of at most 45 hexadecimal digits, colons and points</param>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when the text is an IPv6 address without a zone</returns>
    private static bool TryParseCanonical(string text, out IPAddress? address)
    {
        address = null;

        if (IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.ScopeId == 0)
        {
            address = parsed;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads four dot-separated decimal octets of 1 to 3 digits without a leading zero.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="address">The address built from the four bytes</param>
    /// <returns><c>true</c> when read</returns>
    private static bool TryParseQuad(string text, out IPAddress? address)
    {
        address = null;

        var parts = text.Split('.');

        if (parts.Length != OctetCount)
        {
            return false;
        }

        var bytes = new byte[OctetCount];

        for (var index = 0; index < OctetCount; index++)
        {
            if (TryParseOctet(parts[index], out bytes[index]))
            {
                continue;
            }

            return false;
        }

        address = new IPAddress(bytes);

        return true;
    }

    /// <summary>
    /// Reads one octet of a dotted quad.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="value">The value</param>
    /// <returns><c>true</c> for 1 to 3 ASCII digits up to 255, without a leading zero unless the octet is <c>0</c></returns>
    private static bool TryParseOctet(string text, out byte value)
    {
        value = 0;

        return LegacyTopLine.IsDigits(text, 1, MaxOctetDigits) && (text.Length == 1 || text[0] != '0') && byte.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads a port: <c>*</c>, a number without leading zero, or a service name of the table.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="udp"><c>true</c> to look a name up for UDP, <c>false</c> for TCP</param>
    /// <param name="port">The port</param>
    /// <returns><c>true</c> when read</returns>
    private static bool TryParsePort(string text, bool udp, out int port)
    {
        port = 0;

        if (text == "*")
        {
            return true;
        }

        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            return text.Length <= MaxPortDigits && (text.Length == 1 || text[0] != '0') && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port <= MaxPort;
        }

        return text.Length is >= 1 and <= MaxServiceNameCharacters && text.All(IsServiceCharacter) && LegacyServiceNames.TryGetPort(text, udp, out port);
    }

    /// <summary>
    /// Decodes the <c>\xHH</c> escapes of a command and reads the result as UTF-8.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <returns>The command, at most 1,024 bytes</returns>
    private static string DecodeCommand(string column)
    {
        var bytes = new List<byte>(column.Length);
        var start = 0;
        var index = 0;

        while (index < column.Length)
        {
            if (IsEscape(column, index))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(column[start..index]));
                bytes.Add(Convert.ToByte(column.Substring(index + 2, 2), 16));
                index += 4;
                start = index;
            }
            else
            {
                index++;
            }
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(column[start..]));

        return Utf8Text.Cut(Encoding.UTF8.GetString(bytes.ToArray()), MaxCommandBytes, out _);
    }

    /// <summary>
    /// Tells whether a <c>\xHH</c> escape begins at an index.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="index">The index</param>
    /// <returns><c>true</c> for a backslash, an <c>x</c> and two hexadecimal digits</returns>
    private static bool IsEscape(string text, int index)
    {
        return index + 3 < text.Length && text[index] == '\\' && text[index + 1] == 'x' && char.IsAsciiHexDigit(text[index + 2]) && char.IsAsciiHexDigit(text[index + 3]);
    }

    /// <summary>
    /// Tells whether a character may be part of the text of an IPv6 address.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for hexadecimal digits, <c>:</c> and <c>.</c></returns>
    private static bool IsAddressCharacter(char character)
    {
        return char.IsAsciiHexDigit(character) || character is ':' or '.';
    }

    /// <summary>
    /// Tells whether a character may be part of a service name.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for an ASCII letter or digit, <c>.</c>, <c>_</c>, <c>+</c> or <c>-</c></returns>
    private static bool IsServiceCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-';
    }

    #endregion // Methods
}