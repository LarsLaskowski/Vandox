using System.Buffers;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The parsed header of an entry of the MariaDB error log.
/// </summary>
internal sealed class MariaDbLine
{
    #region Constants

    /// <summary>
    /// The level <c>ERROR</c>.
    /// </summary>
    internal const string LevelError = "ERROR";

    /// <summary>
    /// The level <c>Warning</c>.
    /// </summary>
    internal const string LevelWarning = "Warning";

    /// <summary>
    /// The level <c>Note</c>.
    /// </summary>
    internal const string LevelNote = "Note";

    /// <summary>
    /// The program of a line written by the start script.
    /// </summary>
    internal const string SafeProgram = "mysqld_safe";

    private const byte Space = (byte)' ';
    private const byte Zero = (byte)'0';
    private const byte Nine = (byte)'9';
    private const int LongDateLength = 10;
    private const int ShortDateLength = 6;
    private const int ClockLength = 10;
    private const int MaxThreadDigits = 20;
    private const int MaxHexDigits = 16;
    private const int ShortYearBase = 2000;

    #endregion // Constants

    #region Fields

    private static readonly SearchValues<byte> _hexDigits = SearchValues.Create("0123456789abcdef"u8);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the time: year set (2000 + YY for YYMMDD), no offset, no fraction; digits only, ranges not checked.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the level, one of the level constants, or empty.
    /// </summary>
    internal string Level { get; init; } = string.Empty;

    /// <summary>
    /// Gets the program: <see cref="SafeProgram"/> for the start script form, else empty.
    /// </summary>
    internal string Program { get; init; } = string.Empty;

    /// <summary>
    /// Gets the rest of the line after the prefix, decoded, not cut.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the syslog priority: ERROR 3, Warning 4, Note 6, else <c>null</c>.
    /// </summary>
    internal byte? Priority => Level switch
                               {
                                   LevelError => 3,
                                   LevelWarning => 4,
                                   LevelNote => 6,
                                   _ => null
                               };

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses the header of an entry; never throws.
    /// </summary>
    /// <param name="line">The raw line</param>
    /// <returns>The header, or <c>null</c> for a continuation line</returns>
    internal static MariaDbLine? TryParse(ReadOnlySpan<byte> line)
    {
        var dateLength = ParseDate(line, out var year, out var month, out var day);

        if (dateLength == 0)
        {
            return null;
        }

        var rest = line[dateLength..];
        var clockLength = ParseClock(rest, out var hour, out var minute, out var second);

        if (clockLength == 0)
        {
            return null;
        }

        rest = rest[clockLength..];

        var time = new SyslogTime(year, month, day, hour, minute, second, 0, null);

        return dateLength == LongDateLength ? ParseServer(time, rest) : ParseShort(time, rest);
    }

    /// <summary>
    /// Reads the date at the start of a line: <c>DDDD-DD-DD</c> or <c>YYMMDD</c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="year">The year</param>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <returns>The number of bytes of the date, 0 when the line does not start with one</returns>
    private static int ParseDate(ReadOnlySpan<byte> line, out int year, out int month, out int day)
    {
        year = 0;
        month = 0;
        day = 0;

        if (line.Length >= LongDateLength && AllDigits(line[..4]) && line[4] == (byte)'-' && AllDigits(line[5..7]) && line[7] == (byte)'-' && AllDigits(line[8..10]))
        {
            year = (Digits(line[0], line[1]) * 100) + Digits(line[2], line[3]);
            month = Digits(line[5], line[6]);
            day = Digits(line[8], line[9]);

            return LongDateLength;
        }

        if (line.Length >= ShortDateLength && AllDigits(line[..ShortDateLength]))
        {
            year = ShortYearBase + Digits(line[0], line[1]);
            month = Digits(line[2], line[3]);
            day = Digits(line[4], line[5]);

            return ShortDateLength;
        }

        return 0;
    }

    /// <summary>
    /// Reads the clock after a date: a space, the hour (<c>DD</c> or a space and a digit), <c>:DD:DD</c> and a space.
    /// </summary>
    /// <param name="span">The bytes after the date</param>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <param name="second">The second</param>
    /// <returns>The number of bytes of the clock including both spaces, 0 when there is none</returns>
    private static int ParseClock(ReadOnlySpan<byte> span, out int hour, out int minute, out int second)
    {
        hour = 0;
        minute = 0;
        second = 0;

        if (span.Length < ClockLength || span[0] != Space || span[3] != (byte)':' || span[6] != (byte)':' || span[9] != Space)
        {
            return 0;
        }

        if (TryHour(span[1], span[2], out hour) && AllDigits(span.Slice(4, 2)) && AllDigits(span.Slice(7, 2)))
        {
            minute = Digits(span[4], span[5]);
            second = Digits(span[7], span[8]);

            return ClockLength;
        }

        return 0;
    }

    /// <summary>
    /// Reads the hour, which is padded with a space when it has one digit.
    /// </summary>
    /// <param name="tens">The first byte</param>
    /// <param name="ones">The second byte</param>
    /// <param name="hour">The hour</param>
    /// <returns><c>true</c> when the two bytes are an hour</returns>
    private static bool TryHour(byte tens, byte ones, out int hour)
    {
        hour = 0;

        if (IsDigit(ones) && tens == Space)
        {
            hour = ones - Zero;

            return true;
        }

        if (IsDigit(ones) && IsDigit(tens))
        {
            hour = Digits(tens, ones);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the rest of a line that starts with a date of the form <c>DDDD-DD-DD</c>: a server line or an InnoDB time stamp.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="rest">The bytes after the clock</param>
    /// <returns>The header, or <c>null</c></returns>
    private static MariaDbLine? ParseServer(SyslogTime time, ReadOnlySpan<byte> rest)
    {
        var threadEnd = rest.IndexOfAnyExceptInRange(Zero, Nine);

        if (threadEnd < 0)
        {
            return null;
        }

        if (threadEnd is >= 1 and <= MaxThreadDigits && rest[threadEnd] == Space)
        {
            return ParseLevel(time, rest[(threadEnd + 1)..]);
        }

        return ParseInnoDb(time, rest);
    }

    /// <summary>
    /// Reads an InnoDB time stamp line: <c>0x</c>, one to sixteen lower-case hex digits, then spaces and the message.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="rest">The bytes after the clock</param>
    /// <returns>The header, or <c>null</c></returns>
    private static MariaDbLine? ParseInnoDb(SyslogTime time, ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 3 || rest[0] != Zero || rest[1] != (byte)'x')
        {
            return null;
        }

        var hex = rest[2..];
        var hexEnd = hex.IndexOfAnyExcept(_hexDigits);

        if (hexEnd is < 1 or > MaxHexDigits || hex[hexEnd] != Space)
        {
            return null;
        }

        var after = hex[hexEnd..];
        var spaces = after.IndexOfAnyExcept(Space);

        return Create(time, string.Empty, string.Empty, spaces < 0 ? ReadOnlySpan<byte>.Empty : after[spaces..]);
    }

    /// <summary>
    /// Reads the rest of a line that starts with <c>YYMMDD</c>: a signal handler line or a line of the start script.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="rest">The bytes after the clock</param>
    /// <returns>The header, or <c>null</c></returns>
    private static MariaDbLine? ParseShort(SyslogTime time, ReadOnlySpan<byte> rest)
    {
        ReadOnlySpan<byte> safe = "mysqld_safe "u8;

        if (rest.StartsWith(safe))
        {
            return Create(time, string.Empty, SafeProgram, rest[safe.Length..]);
        }

        return ParseLevel(time, rest);
    }

    /// <summary>
    /// Reads <c>[level]</c> and the message after it.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="span">The bytes that start at the bracket</param>
    /// <returns>The header, or <c>null</c></returns>
    private static MariaDbLine? ParseLevel(SyslogTime time, ReadOnlySpan<byte> span)
    {
        var level = LevelOf(span);

        if (level is null)
        {
            return null;
        }

        var after = span[(level.Length + 2)..];

        if (after.Length == 0)
        {
            return Create(time, level, string.Empty, after);
        }

        return after[0] == Space ? Create(time, level, string.Empty, after[1..]) : null;
    }

    /// <summary>
    /// Reads the level in brackets at the start of the bytes.
    /// </summary>
    /// <param name="span">The bytes</param>
    /// <returns>The level, or <c>null</c> when there is none</returns>
    private static string? LevelOf(ReadOnlySpan<byte> span)
    {
        if (span.StartsWith("[ERROR]"u8))
        {
            return LevelError;
        }

        if (span.StartsWith("[Warning]"u8))
        {
            return LevelWarning;
        }

        return span.StartsWith("[Note]"u8) ? LevelNote : null;
    }

    /// <summary>
    /// Builds a header and decodes its message.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="level">The level</param>
    /// <param name="program">The program</param>
    /// <param name="message">The raw message</param>
    /// <returns>The header</returns>
    private static MariaDbLine Create(SyslogTime time, string level, string program, ReadOnlySpan<byte> message)
    {
        return new MariaDbLine
               {
                   Time = time,
                   Level = level,
                   Program = program,
                   Message = Utf8Text.Decode(message, int.MaxValue, out _)
               };
    }

    /// <summary>
    /// Tells whether a byte is an ASCII digit.
    /// </summary>
    /// <param name="value">The byte</param>
    /// <returns><c>true</c> for <c>0</c> to <c>9</c></returns>
    private static bool IsDigit(byte value)
    {
        return value is >= Zero and <= Nine;
    }

    /// <summary>
    /// Tells whether every byte is an ASCII digit.
    /// </summary>
    /// <param name="span">The bytes</param>
    /// <returns><c>true</c> when there is no other byte</returns>
    private static bool AllDigits(ReadOnlySpan<byte> span)
    {
        return span.IndexOfAnyExceptInRange(Zero, Nine) < 0;
    }

    /// <summary>
    /// Converts two digit bytes to a number.
    /// </summary>
    /// <param name="tens">The first digit</param>
    /// <param name="ones">The second digit</param>
    /// <returns>The number</returns>
    private static int Digits(byte tens, byte ones)
    {
        return ((tens - Zero) * 10) + (ones - Zero);
    }

    #endregion // Methods
}