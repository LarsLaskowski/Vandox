#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

namespace Vandox.Core.LogParsing;

/// <summary>
/// One parsed syslog line.
/// </summary>
internal sealed class SyslogLine
{
    #region Properties

    /// <summary>
    /// Gets the priority (0 to 7); <c>null</c> without a <c>&lt;PRI&gt;</c>.
    /// </summary>
    internal byte? Priority { get; init; }

    /// <summary>
    /// Gets the time stamp.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the host.
    /// </summary>
    internal string Host { get; init; } = string.Empty;

    /// <summary>
    /// Gets the program of the tag; empty without a tag.
    /// </summary>
    internal string Program { get; init; } = string.Empty;

    /// <summary>
    /// Gets the process ID of the tag; 0 without one.
    /// </summary>
    internal int Pid { get; init; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses a line; builds no date or instant.
    /// </summary>
    /// <param name="line">The decoded line</param>
    /// <returns>The line, or <c>null</c> when it is not a syslog line</returns>
    internal static SyslogLine? TryParse(string line)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}