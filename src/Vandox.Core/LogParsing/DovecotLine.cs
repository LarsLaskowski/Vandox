namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// A line of Dovecot's own log file: the traditional time stamp and the message with its severity marker.
/// </summary>
internal sealed class DovecotLine
{
    #region Properties

    /// <summary>
    /// Gets the time stamp of the line.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the message as written, beginning with the severity marker or its first segment.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses a line of Dovecot's own log file.
    /// </summary>
    /// <param name="line">The line without line end</param>
    /// <returns>The line, or <c>null</c> when it is no Dovecot line</returns>
    internal static DovecotLine? TryParse(string line)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}