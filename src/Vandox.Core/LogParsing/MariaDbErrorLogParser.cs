using NodaTime;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Reads the error log of MariaDB.
/// </summary>
public sealed class MariaDbErrorLogParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of this parser.
    /// </summary>
    public const string ParserType = "mariadb";

    #endregion // Constants

    #region Fields

    private readonly DateTimeZone? _timeZone;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbErrorLogParser"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of the local times; <c>null</c> makes the first header fail the file with <see cref="SyslogParser.TimeZoneNotSet"/></param>
    public MariaDbErrorLogParser(DateTimeZone? timeZone)
    {
        _timeZone = timeZone;
    }

    #endregion // Constructors

    #region ILogParser

    /// <inheritdoc />
    public string Type => ParserType;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        // The first-line rule relies on the syslog grammar never accepting a line starting "DDDD-DD-DD " or "DDDDDD " (SyslogLine.TryParse), so the two detectors cannot overlap.
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // ILogParser
}