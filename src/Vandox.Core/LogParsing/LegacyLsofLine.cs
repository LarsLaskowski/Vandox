using System.Net;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

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

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the decoded command.
    /// </summary>
    internal string Command => throw new NotImplementedException();

    /// <summary>
    /// Gets the process id.
    /// </summary>
    internal int Pid => throw new NotImplementedException();

    /// <summary>
    /// Gets the protocol: tcp, tcp6, udp or udp6.
    /// </summary>
    internal string Proto => throw new NotImplementedException();

    /// <summary>
    /// Gets the local endpoint.
    /// </summary>
    internal IPEndPoint Local => throw new NotImplementedException();

    /// <summary>
    /// Gets the remote endpoint; <c>null</c> for a listener.
    /// </summary>
    internal IPEndPoint? Remote => throw new NotImplementedException();

    /// <summary>
    /// Gets the state; empty for UDP.
    /// </summary>
    internal string State => throw new NotImplementedException();

    /// <summary>
    /// Gets the key of the socket: the text of the line from the TYPE field on.
    /// </summary>
    internal string SocketKey => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether a line is the column header of lsof.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for the header</returns>
    internal static bool IsHeader(string line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a socket line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="reason"><see cref="NotRead"/> or <see cref="NotMapped"/> when not read</param>
    /// <returns>The line; <c>null</c> when not read</returns>
    internal static LegacyLsofLine? TryParse(string line, out string? reason)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}