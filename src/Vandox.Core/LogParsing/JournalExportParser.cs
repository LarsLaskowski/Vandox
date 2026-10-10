using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the output of <c>journalctl -o export</c>.
/// </summary>
public sealed class JournalExportParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of this parser.
    /// </summary>
    public const string ParserType = "journal";

    private const int MaxTimestampDigits = 20;
    private const int MaxPidDigits = 10;
    private const long MaxMicroseconds = 9223372036854775;
    private const long TicksPerMicrosecond = 10;

    #endregion // Constants

    #region Fields

    private static readonly byte[] _cursor = "__CURSOR="u8.ToArray();
    private static readonly byte[] _timestamp = "__REALTIME_TIMESTAMP="u8.ToArray();
    private static readonly byte[] _timestampLine = "\n__REALTIME_TIMESTAMP="u8.ToArray();

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Maps an entry to a record.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="reason">The fixed skip reason when the entry gives no record</param>
    /// <returns>The record, or <c>null</c></returns>
    private static DataRecord? Map(JournalEntry entry, out string reason)
    {
        reason = entry.Problem ?? string.Empty;

        if (entry.Problem is not null)
        {
            return null;
        }

        if (entry.Realtime is null)
        {
            reason = "entry without __REALTIME_TIMESTAMP";

            return null;
        }

        var microseconds = ParseMicroseconds(entry.Realtime);

        if (microseconds == 0)
        {
            reason = "invalid __REALTIME_TIMESTAMP";

            return null;
        }

        if (entry.Message is null)
        {
            reason = "entry without MESSAGE";

            return null;
        }

        var pid = ParsePid(entry.Pid);

        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = ParserType,
                   CapturedAt = DateTimeOffset.UnixEpoch.AddTicks(microseconds * TicksPerMicrosecond),
                   Data = new LogLine
                          {
                              Log = ParserType,
                              Host = entry.Hostname ?? string.Empty,
                              Program = Program(entry),
                              Pid = pid != 0 ? pid : ParsePid(entry.SyslogPid),
                              Priority = ParsePriority(entry.Priority),
                              Message = entry.Message,
                              Truncated = entry.Truncated
                          }
               };
    }

    /// <summary>
    /// Chooses the program: the syslog identifier, else the command name.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <returns>The program; empty when there is none</returns>
    private static string Program(JournalEntry entry)
    {
        return string.IsNullOrEmpty(entry.SyslogIdentifier) ? entry.Comm ?? string.Empty : entry.SyslogIdentifier;
    }

    /// <summary>
    /// Reads the time stamp: 1 to 20 decimal digits, greater than 0 and at most the storable maximum. Every step is bound-checked,
    /// so no overflow can occur.
    /// </summary>
    /// <param name="text">The value</param>
    /// <returns>The microseconds since the Unix epoch; 0 when the value is not a storable time stamp</returns>
    private static long ParseMicroseconds(string text)
    {
        long microseconds = 0;

        if (text.Length is 0 or > MaxTimestampDigits)
        {
            return 0;
        }

        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character) && microseconds <= (MaxMicroseconds - (character - '0')) / 10)
            {
                microseconds = (microseconds * 10) + (character - '0');
            }
            else
            {
                return 0;
            }
        }

        return microseconds;
    }

    /// <summary>
    /// Reads a process ID: 1 to 10 decimal digits with a value of 1 to 2,147,483,647.
    /// </summary>
    /// <param name="text">The value; may be <c>null</c></param>
    /// <returns>The process ID; 0 when the value is not usable</returns>
    private static int ParsePid(string? text)
    {
        if (text is null || text.Length is 0 or > MaxPidDigits)
        {
            return 0;
        }

        long value = 0;

        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
            {
                value = (value * 10) + (character - '0');
            }
            else
            {
                return 0;
            }
        }

        return value <= int.MaxValue ? (int)value : 0;
    }

    /// <summary>
    /// Reads the priority: exactly one digit from 0 to 7.
    /// </summary>
    /// <param name="text">The value; may be <c>null</c></param>
    /// <returns>The priority, or <c>null</c></returns>
    private static byte? ParsePriority(string? text)
    {
        if (text is { Length: 1 } && text[0] is >= '0' and <= '7')
        {
            return (byte)(text[0] - '0');
        }

        return null;
    }

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc />
    public string Type => ParserType;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        var startsWithTimestamp = head.StartsWith(_timestamp);

        if (startsWithTimestamp || (head.StartsWith(_cursor) && head.IndexOf(_timestampLine) >= 0))
        {
            return Confidence.MatchContent;
        }

        return Confidence.NoMatch;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var reader = new JournalExportReader(input);
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        for (var entry = await reader.ReadAsync(cancellationToken).ConfigureAwait(false); entry is not null; entry = await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = Map(entry, out var reason);

            if (record is null)
            {
                output.Skip(0, reason);

                continue;
            }

            grouper.Add(record, ready);
            await KernelReportGrouper.EmitAsync(ready, output, cancellationToken).ConfigureAwait(false);
        }

        grouper.Finish(ready);
        await KernelReportGrouper.EmitAsync(ready, output, cancellationToken).ConfigureAwait(false);
    }

    #endregion // ILogParser
}