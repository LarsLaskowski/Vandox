using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the error log of MariaDB: every entry (a header line and the lines without a header after it) becomes one log line
/// record with its local time resolved in the configured zone and, for lifecycle entries, an event.
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

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

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
        if (SyslogParser.HasSyslogName(file.Name))
        {
            return Confidence.NoMatch;
        }

        var rest = head.StartsWith(_bom) ? head[_bom.Length..] : head;

        while (rest.Length > 0)
        {
            var end = rest.IndexOf((byte)'\n');
            var line = end >= 0 ? rest[..end] : rest;

            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.Length > 0)
            {
                return MariaDbLine.TryParse(line) is null ? Confidence.NoMatch : Confidence.MatchContent;
            }

            rest = end >= 0 ? rest[(end + 1)..] : [];
        }

        return Confidence.NoMatch;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var reader = new LogLineReader(input);
        var session = new MariaDbParseSession(file, _timeZone, output);

        // The per-line path calls no async method other than ReadAsync; a record is awaited only when an entry ends.
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completed = session.Consume(reader);

            if (completed is not null)
            {
                await output.RecordAsync(completed, cancellationToken).ConfigureAwait(false);
            }
        }

        var last = session.Finish();

        if (last is not null)
        {
            await output.RecordAsync(last, cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion // ILogParser
}