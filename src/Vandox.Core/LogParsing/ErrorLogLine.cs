#pragma warning disable RH2003, S2325

namespace Vandox.Core.LogParsing;

/// <summary>
/// One parsed line of an error log.
/// </summary>
internal sealed class ErrorLogLine
{
    #region Properties

    /// <summary>
    /// Gets the time of the line.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the syslog priority of the line.
    /// </summary>
    internal byte Priority { get; init; }

    /// <summary>
    /// Gets the message of the line.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses a line.
    /// </summary>
    /// <param name="format">The format</param>
    /// <param name="line">The line</param>
    /// <returns>The parsed line, or <c>null</c> when the line is not of the format</returns>
    internal static ErrorLogLine? TryParse(ErrorLogFormat format, string line)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}