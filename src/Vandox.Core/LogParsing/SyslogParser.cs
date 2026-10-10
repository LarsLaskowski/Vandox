using System.Text;

using NodaTime;

using Vandox.Core.Model;

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

    private const string EmptyLine = "empty line";
    private const string NotSyslogLine = "not a syslog line";

    #endregion // Constants

    #region Fields

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    private readonly DateTimeZone? _timeZone;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SyslogParser"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of year-less lines; <c>null</c> makes the first year-less line fail the file</param>
    public SyslogParser(DateTimeZone? timeZone)
    {
        _timeZone = timeZone;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Tells whether the base name of a file is <c>syslog</c> or <c>kern.log</c>, with an optional rotation number or date.
    /// </summary>
    /// <param name="name">The name of the file</param>
    /// <returns><c>true</c> for <c>syslog</c>, <c>kern.log</c>, <c>*.N</c> and <c>*-YYYYMMDD</c> of them</returns>
    internal static bool HasSyslogName(string name)
    {
        var baseName = name[(name.LastIndexOf('/') + 1)..];
        string rest;

        if (baseName.StartsWith("syslog", StringComparison.Ordinal))
        {
            rest = baseName[6..];
        }
        else if (baseName.StartsWith("kern.log", StringComparison.Ordinal))
        {
            rest = baseName[8..];
        }
        else
        {
            return false;
        }

        return rest.Length == 0
               || (rest.Length > 1 && rest[0] == '.' && rest[1..].All(char.IsAsciiDigit))
               || (rest.Length == 9 && rest[0] == '-' && rest[1..].All(char.IsAsciiDigit));
    }

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

        var line = SyslogLine.TryParse(Utf8Text.Decode(bytes, int.MaxValue, out _));

        if (line is null)
        {
            output.Skip(reader.LineNumber, NotSyslogLine);

            return null;
        }

        var reason = line.Time.HasYear ? line.Time.TryGetInstant(out var instant) : Resolve(line.Time, clock, out instant);

        if (reason is not null)
        {
            output.Skip(reader.LineNumber, reason);

            return null;
        }

        var message = Utf8Text.Cut(line.Message, ModelLimits.MaxTextBytes, out var cut);

        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = ParserType,
                   CapturedAt = instant,
                   Data = new LogLine
                          {
                              Log = file.Name,
                              Host = line.Host,
                              Program = line.Program,
                              Pid = line.Pid,
                              Priority = line.Priority,
                              Message = message,
                              Truncated = reader.Truncated || cut
                          }
               };
    }

    /// <summary>
    /// Resolves a year-less time; fails the file when no time zone is set.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="clock">The clock; <c>null</c> without a time zone</param>
    /// <param name="instant">The instant on success</param>
    /// <returns><c>null</c> on success, else the reason</returns>
    private static string? Resolve(SyslogTime time, SyslogClock? clock, out DateTimeOffset instant)
    {
        if (clock is null)
        {
            throw new InvalidOperationException(TimeZoneNotSet);
        }

        return clock.Resolve(time, out instant);
    }

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc />
    public string Type => ParserType;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
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

        if (HasSyslogName(file.Name) || SyslogLine.TryParse(Utf8Text.Decode(first, int.MaxValue, out _)) is not null)
        {
            return Confidence.MatchName;
        }

        return Confidence.NoMatch;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var reader = new LogLineReader(input);
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        var clock = _timeZone is null ? null : new SyslogClock(_timeZone, file);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = Map(file, reader, clock, output);

            if (record is not null)
            {
                grouper.Add(record, ready);
                await KernelReportGrouper.EmitAsync(ready, output, cancellationToken).ConfigureAwait(false);
            }
        }

        grouper.Finish(ready);
        await KernelReportGrouper.EmitAsync(ready, output, cancellationToken).ConfigureAwait(false);
    }

    #endregion // ILogParser
}