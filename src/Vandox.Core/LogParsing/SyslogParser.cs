#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

using NodaTime;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads rsyslog files such as <c>syslog</c> and <c>kern.log</c>.
/// </summary>
public sealed class SyslogParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of this parser.
    /// </summary>
    public const string ParserType = "syslog";

    /// <summary>
    /// The message of the exception thrown at the first year-less line when no time zone is set.
    /// </summary>
    public const string TimeZoneNotSet = "import.time_zone is not set";

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SyslogParser"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of year-less lines; <c>null</c> makes the first year-less line fail the file</param>
    public SyslogParser(DateTimeZone? timeZone)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region ILogParser

    /// <inheritdoc />
    public string Type => ParserType;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // ILogParser
}