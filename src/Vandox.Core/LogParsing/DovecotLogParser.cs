using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325, S1144, S4487

/// <summary>
/// Reads Dovecot's own log file (<c>log_path</c> set to a file): one log line record per line, priority and event from the marker.
/// </summary>
public sealed class DovecotLogParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of this parser.
    /// </summary>
    public const string ParserType = "dovecot";

    private const string EmptyLine = "empty line";
    private const string NotDovecotLine = "not a Dovecot line";

    #endregion // Constants

    #region Fields

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    private readonly DateTimeZone? _timeZone;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DovecotLogParser"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of the year-less lines; <c>null</c> makes the first Dovecot line fail the file</param>
    public DovecotLogParser(DateTimeZone? timeZone)
    {
        _timeZone = timeZone;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Maps the line the reader returned last to a record, or reports why there is none.
    /// </summary>
    /// <param name="file">The file</param>
    /// <param name="reader">The reader</param>
    /// <param name="clock">The clock for year-less times; <c>null</c> without a time zone</param>
    /// <param name="output">Receives skipped lines</param>
    /// <returns>The record, or <c>null</c></returns>
    /// <exception cref="InvalidOperationException">The line has no year and no time zone is set</exception>
    private static DataRecord? Map(LogFile file, LogLineReader reader, SyslogClock? clock, IRecordEmitter output)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc />
    public string Type => ParserType;

    /// <inheritdoc />
    public bool LineFramed => true;

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