using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the legacy monitoring log of top, /proc and lsof snapshots.
/// </summary>
public sealed class LegacyMonitorParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of the legacy monitoring log.
    /// </summary>
    public const string ParserType = "legacy-monitor";

    /// <summary>
    /// The message of the exception for a first header whose zone does not match the configured time zone.
    /// </summary>
    public const string ZoneMismatchFirst = "the time zone of the first snapshot header does not match import.time_zone";

    private const string InvalidHeader = "invalid snapshot header";
    private const string BeforeFirstSnapshot = "line before the first snapshot";

    #endregion // Constants

    #region Fields

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    private readonly DateTimeZone? _timeZone;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyMonitorParser"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of the snapshot headers; <c>null</c> when not set</param>
    public LegacyMonitorParser(DateTimeZone? timeZone)
    {
        _timeZone = timeZone;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Removes a UTF-8 byte order mark from the first line and a trailing carriage return from every line.
    /// </summary>
    /// <param name="bytes">The line</param>
    /// <param name="lineNumber">The 1-based line number</param>
    /// <returns>The line without them</returns>
    private static ReadOnlySpan<byte> Clean(ReadOnlySpan<byte> bytes, long lineNumber)
    {
        var line = lineNumber == 1 && bytes.StartsWith(_bom) ? bytes[_bom.Length..] : bytes;

        return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
    }

    /// <summary>
    /// Requires that a header of this parse has resolved: a zone mismatch of the first header fails the file.
    /// </summary>
    /// <param name="state">The state of the parse</param>
    /// <exception cref="InvalidOperationException">No header of this parse has resolved</exception>
    private static void RequireResolved(LegacyParseState state)
    {
        if (state.Resolved)
        {
            return;
        }

        throw new InvalidOperationException(ZoneMismatchFirst);
    }

    /// <summary>
    /// Opens the snapshot of a header line, or reports why the snapshot is skipped.
    /// </summary>
    /// <param name="line">The header line</param>
    /// <param name="lineNumber">The 1-based line number</param>
    /// <param name="state">The state of the parse</param>
    /// <param name="output">Receives the skip</param>
    /// <returns>The snapshot; <c>null</c> when it is skipped</returns>
    /// <exception cref="InvalidOperationException">The time zone is not set, or the zone of the first header does not match it</exception>
    private LegacyMonitorSnapshot? Open(ReadOnlySpan<byte> line, long lineNumber, LegacyParseState state, IRecordEmitter output)
    {
        var header = LegacyMonitorHeader.TryParse(line);

        if (header is null)
        {
            output.Skip(lineNumber, InvalidHeader);

            return null;
        }

        if (_timeZone is null)
        {
            throw new InvalidOperationException(SyslogParser.TimeZoneNotSet);
        }

        var reason = header.Value.Resolve(_timeZone, out var instant);

        if (reason is null)
        {
            state.Resolved = true;

            return new LegacyMonitorSnapshot(lineNumber, instant, state.Counters);
        }

        if (reason == LegacyMonitorHeader.ZoneMismatch)
        {
            RequireResolved(state);
        }

        output.Skip(lineNumber, reason);

        return null;
    }

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc/>
    public string Type => ParserType;

    /// <inheritdoc/>
    public bool LineFramed => true;

    /// <inheritdoc/>
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        var first = head.StartsWith(_bom) ? head[_bom.Length..] : head;
        var lineEnd = first.IndexOf((byte)'\n');

        first = lineEnd >= 0 ? first[..lineEnd] : first;
        first = first.Length > 0 && first[^1] == (byte)'\r' ? first[..^1] : first;

        return SyslogParser.HasSyslogName(file.Name) || LegacyMonitorHeader.TryParse(first) is null ? Confidence.NoMatch : Confidence.MatchContent;
    }

    /// <inheritdoc/>
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var reader = new LogLineReader(input);
        var ready = new List<DataRecord>();
        var state = new LegacyParseState();
        var started = false;
        LegacyMonitorSnapshot? current = null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = Clean(reader.Line.Span, reader.LineNumber);

            if (LegacyMonitorHeader.IsHeaderLine(bytes))
            {
                current?.Finish(false, output, ready);
                started = true;
                current = Open(bytes, reader.LineNumber, state, output);
            }
            else if (started)
            {
                current?.Add(reader.LineNumber, Utf8Text.Decode(bytes, int.MaxValue, out _), reader.Truncated, output, ready);
            }
            else
            {
                output.Skip(reader.LineNumber, BeforeFirstSnapshot);
            }

            await KernelReportGrouper.EmitAsync(ready, output, cancellationToken).ConfigureAwait(false);
        }

        current?.Finish(true, output, ready);
        await KernelReportGrouper.EmitAsync(ready, output, cancellationToken).ConfigureAwait(false);
    }

    #endregion // ILogParser
}