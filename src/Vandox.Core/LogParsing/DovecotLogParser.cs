using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

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
        var bytes = reader.Line.Span;

        if (reader.LineNumber == 1 && bytes.StartsWith(_bom))
        {
            bytes = bytes[_bom.Length..];
        }

        if (bytes.Length == 0)
        {
            output.Skip(reader.LineNumber, EmptyLine);

            return null;
        }

        var line = DovecotLine.TryParse(Utf8Text.Decode(bytes, int.MaxValue, out _));

        if (line is null)
        {
            output.Skip(reader.LineNumber, NotDovecotLine);

            return null;
        }

        var reason = line.Time.HasYear ? line.Time.TryGetInstant(out var instant) : SyslogParser.Resolve(line.Time, clock, out instant);

        if (reason is not null)
        {
            output.Skip(reader.LineNumber, reason);

            return null;
        }

        var payload = new LogLine
                      {
                          Log = file.Name,
                          Message = Utf8Text.Cut(line.Message, ModelLimits.MaxTextBytes, out var cut),
                          Truncated = reader.Truncated || cut
                      };

        MailLineClassifier.ClassifyDovecotFileLine(payload);

        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = ParserType,
                   CapturedAt = instant,
                   Data = payload
               };
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
        if (SyslogParser.HasSyslogName(file.Name))
        {
            return Confidence.NoMatch;
        }

        var first = head;

        if (first.StartsWith(_bom))
        {
            first = first[_bom.Length..];
        }

        var lineEnd = first.IndexOf((byte)'\n');

        if (lineEnd >= 0)
        {
            first = first[..lineEnd];
        }

        if (first.Length > 0 && first[^1] == (byte)'\r')
        {
            first = first[..^1];
        }

        return DovecotLine.TryParse(Utf8Text.Decode(first, int.MaxValue, out _)) is null ? Confidence.NoMatch : Confidence.MatchContent;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var reader = new LogLineReader(input);
        var clock = _timeZone is null ? null : new SyslogClock(_timeZone, file);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = Map(file, reader, clock, output);

            if (record is not null)
            {
                await output.RecordAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    #endregion // ILogParser
}