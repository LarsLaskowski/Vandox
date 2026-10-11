namespace Vandox.Core.LogParsing;

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
        var position = 0;
        var time = SyslogLine.ParseTraditional(line, ref position);

        if (time is null || position >= line.Length || line[position] != ' ')
        {
            return null;
        }

        position++;

        var message = line[position..];

        if (MailLineClassifier.HasDovecotFileMarker(message))
        {
            return new DovecotLine
                   {
                       Time = time.Value,
                       Message = message
                   };
        }

        return null;
    }

    #endregion // Methods
}