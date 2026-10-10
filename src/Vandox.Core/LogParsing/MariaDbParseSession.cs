using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The state of one parse of a MariaDB error log: the open entry, the classifier and the previous instant.
/// </summary>
internal sealed class MariaDbParseSession
{
    #region Constants

    private const string EmptyLine = "empty line";
    private const string BeforeFirstEntry = "line before the first entry";

    #endregion // Constants

    #region Fields

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    private readonly LogFile _file;
    private readonly DateTimeZone? _timeZone;
    private readonly IRecordEmitter _output;
    private readonly MariaDbEventClassifier _classifier = new();

    private MariaDbLine? _header;
    private MariaDbMessage? _message;
    private DateTimeOffset _instant;
    private string _event = string.Empty;
    private bool _started;
    private DateTimeOffset? _previous;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbParseSession"/> class.
    /// </summary>
    /// <param name="file">The file</param>
    /// <param name="timeZone">The zone; <c>null</c> when not set</param>
    /// <param name="output">Receives the skipped lines</param>
    internal MariaDbParseSession(LogFile file, DateTimeZone? timeZone, IRecordEmitter output)
    {
        _file = file;
        _timeZone = timeZone;
        _output = output;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Handles the line the reader returned last.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The record of the entry that this line ended, or <c>null</c></returns>
    /// <exception cref="InvalidOperationException">A header is read and no time zone is set</exception>
    internal DataRecord? Consume(LogLineReader reader)
    {
        var bytes = reader.Line.Span;

        if (reader.LineNumber == 1 && bytes.StartsWith(_bom))
        {
            bytes = bytes[_bom.Length..];
        }

        var header = MariaDbLine.TryParse(bytes);

        if (header is not null)
        {
            return StartEntry(header, reader);
        }

        Continue(bytes, reader);

        return null;
    }

    /// <summary>
    /// Ends the parse at the end of the input.
    /// </summary>
    /// <returns>The record of the open entry, or <c>null</c></returns>
    internal DataRecord? Finish()
    {
        return TakeRecord();
    }

    /// <summary>
    /// Handles a line that is no header: it belongs to the open entry, is dropped with a skipped entry, or comes before the first entry.
    /// </summary>
    /// <param name="bytes">The raw line</param>
    /// <param name="reader">The reader</param>
    private void Continue(ReadOnlySpan<byte> bytes, LogLineReader reader)
    {
        if (_message is not null)
        {
            _message.Add(bytes, reader.Truncated);

            return;
        }

        if (_started)
        {
            return;
        }

        _output.Skip(reader.LineNumber, bytes.Length == 0 ? EmptyLine : BeforeFirstEntry);
    }

    /// <summary>
    /// Handles a header: ends the open entry, resolves the time and opens the new entry, or skips it.
    /// </summary>
    /// <param name="header">The header</param>
    /// <param name="reader">The reader</param>
    /// <returns>The record of the entry that ended, or <c>null</c></returns>
    private DataRecord? StartEntry(MariaDbLine header, LogLineReader reader)
    {
        if (_timeZone is null)
        {
            throw new InvalidOperationException(SyslogParser.TimeZoneNotSet);
        }

        var completed = TakeRecord();
        var reason = SyslogClock.ResolveLocal(_timeZone, header.Time, _previous, out var instant);

        _started = true;

        if (reason is not null)
        {
            _output.Skip(reader.LineNumber, reason);

            return completed;
        }

        _previous = instant;
        _instant = instant;
        _header = header;
        _event = _classifier.Classify(header);
        _message = new MariaDbMessage(header.Message, reader.Truncated);

        return completed;
    }

    /// <summary>
    /// Builds the record of the open entry and closes it.
    /// </summary>
    /// <returns>The record, or <c>null</c> when no entry is open</returns>
    private DataRecord? TakeRecord()
    {
        if (_header is null || _message is null)
        {
            return null;
        }

        var message = _message.Build(out var truncated);
        var record = new DataRecord
                     {
                         Origin = RecordOrigin.Import,
                         Source = MariaDbErrorLogParser.ParserType,
                         CapturedAt = _instant,
                         Data = new LogLine
                                {
                                    Log = _file.Name,
                                    Program = _header.Program,
                                    Priority = _header.Priority,
                                    Message = message,
                                    Truncated = truncated,
                                    Event = _event
                                }
                     };

        _header = null;
        _message = null;

        return record;
    }

    #endregion // Methods
}