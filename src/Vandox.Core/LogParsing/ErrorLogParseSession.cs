using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The state of one parse of an error log: the capture time of the record before and the instant of the last recognized line.
/// </summary>
internal sealed class ErrorLogParseSession
{
    #region Constants

    private const string EmptyLine = "empty line";
    private const string NoTime = "raw line without a time";

    #endregion // Constants

    #region Fields

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    private readonly LogFile _file;
    private readonly string _type;
    private readonly ErrorLogFormat _format;
    private readonly DateTimeZone? _timeZone;
    private readonly IRecordEmitter _output;
    private DateTimeOffset? _lastCaptured;
    private DateTimeOffset? _previous;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorLogParseSession"/> class.
    /// </summary>
    /// <param name="file">The file</param>
    /// <param name="type">The source type of the records</param>
    /// <param name="format">The line format</param>
    /// <param name="timeZone">The zone of the local times; <c>null</c> when not set</param>
    /// <param name="output">Receives skipped lines and warnings</param>
    internal ErrorLogParseSession(LogFile file, string type, ErrorLogFormat format, DateTimeZone? timeZone, IRecordEmitter output)
    {
        _file = file;
        _type = type;
        _format = format;
        _timeZone = timeZone;
        _output = output;
        _lastCaptured = file.ModTime is { } modTime && StorableTime.Contains(modTime) ? modTime.ToUniversalTime() : null;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Maps the line the reader returned last to a record, or reports why there is none.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The record, or <c>null</c> for a skipped line</returns>
    /// <exception cref="InvalidOperationException">A line of the format is read and no time zone is set</exception>
    internal DataRecord? Map(LogLineReader reader)
    {
        var bytes = reader.Line.Span;

        if (reader.LineNumber == 1 && bytes.StartsWith(_bom))
        {
            bytes = bytes[_bom.Length..];
        }

        if (bytes.Length == 0)
        {
            _output.Skip(reader.LineNumber, EmptyLine);

            return null;
        }

        var text = Utf8Text.Decode(bytes, int.MaxValue, out _);
        var line = ErrorLogLine.TryParse(_format, text);

        if (line is null)
        {
            return MapRaw(reader.LineNumber, text, NotOfTheFormat(), reader.Truncated);
        }

        if (_timeZone is null)
        {
            throw new InvalidOperationException(SyslogParser.TimeZoneNotSet);
        }

        var reason = Resolve(_timeZone, line.Time, out var instant);

        if (reason is not null)
        {
            return MapRaw(reader.LineNumber, text, reason, reader.Truncated);
        }

        _previous = instant;

        return Build(instant, line.Priority, line.Message, reader.Truncated);
    }

    /// <summary>
    /// Resolves the local time of a line to a storable UTC instant.
    /// </summary>
    /// <param name="zone">The zone</param>
    /// <param name="time">The time digits</param>
    /// <param name="instant">The instant on success</param>
    /// <returns><c>null</c> on success, else the reason</returns>
    private string? Resolve(DateTimeZone zone, SyslogTime time, out DateTimeOffset instant)
    {
        var reason = SyslogClock.ResolveLocal(zone, time, _previous, out instant);

        if (reason is null)
        {
            instant = instant.AddTicks(time.FractionTicks);

            return StorableTime.Contains(instant) ? null : SyslogTime.OutsideRange;
        }

        return reason;
    }

    /// <summary>
    /// Keeps a line as a raw record after warning about it, or skips it when there is no time to give it.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="text">The whole line</param>
    /// <param name="reason">Why the line is raw</param>
    /// <param name="truncated">Whether the reader cut the line</param>
    /// <returns>The record, or <c>null</c> when the line was skipped</returns>
    private DataRecord? MapRaw(long lineNumber, string text, string reason, bool truncated)
    {
        if (_lastCaptured is not { } captured)
        {
            _output.Skip(lineNumber, NoTime);

            return null;
        }

        _output.Warn(lineNumber, reason);

        return Build(captured, null, text, truncated);
    }

    /// <summary>
    /// Builds the record of a line and remembers its capture time.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="priority">The priority; <c>null</c> for a raw line</param>
    /// <param name="message">The message</param>
    /// <param name="truncated">Whether the reader cut the line</param>
    /// <returns>The record</returns>
    private DataRecord Build(DateTimeOffset capturedAt, byte? priority, string message, bool truncated)
    {
        _lastCaptured = capturedAt;

        var payload = new LogLine
                      {
                          Log = _file.Name,
                          Priority = priority,
                          Message = Utf8Text.Cut(message, ModelLimits.MaxTextBytes, out var cut),
                          Truncated = truncated || cut
                      };

        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = _type,
                   CapturedAt = capturedAt,
                   Data = payload
               };
    }

    /// <summary>
    /// Returns the reason of a line that does not have the format.
    /// </summary>
    /// <returns>The reason</returns>
    private string NotOfTheFormat()
    {
        return _format switch
               {
                   ErrorLogFormat.PleskPanel => "not a Plesk panel log line",
                   ErrorLogFormat.Nginx => "not an nginx error log line",
                   _ => "not an Apache error log line"
               };
    }

    #endregion // Methods
}