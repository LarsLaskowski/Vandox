using System.Text;

namespace Vandox.Core.LogParsing;

/// <summary>
/// One parsed syslog line.
/// </summary>
internal sealed class SyslogLine
{
    #region Constants

    private const int MaxHostBytes = 255;
    private const int MaxProgramBytes = 128;
    private const int MaxPidDigits = 10;
    private const int MaxPriority = 191;
    private const int MaxPriorityDigits = 3;
    private const int PriorityClasses = 8;
    private const int TraditionalLength = 15;
    private const int Rfc3339Length = 19;
    private const int MaxFractionDigits = 9;
    private const int FractionTickDigits = 7;
    private const int MaxOffsetHours = 14;

    #endregion // Constants

    #region Fields

    private static readonly string[] _months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the priority (0 to 7); <c>null</c> without a <c>&lt;PRI&gt;</c>.
    /// </summary>
    internal byte? Priority { get; init; }

    /// <summary>
    /// Gets the time stamp.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the host.
    /// </summary>
    internal string Host { get; init; } = string.Empty;

    /// <summary>
    /// Gets the program of the tag; empty without a tag.
    /// </summary>
    internal string Program { get; init; } = string.Empty;

    /// <summary>
    /// Gets the process ID of the tag; 0 without one.
    /// </summary>
    internal int Pid { get; init; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses a line; builds no date or instant. The parse is linear in the length of the line.
    /// </summary>
    /// <param name="line">The decoded line</param>
    /// <returns>The line, or <c>null</c> when it is not a syslog line</returns>
    internal static SyslogLine? TryParse(string line)
    {
        var position = 0;
        byte? priority = null;

        if (line.StartsWith('<'))
        {
            priority = ParsePriority(line, ref position);

            if (priority is null)
            {
                return null;
            }
        }

        var time = position < line.Length && char.IsAsciiDigit(line[position]) ? ParseRfc3339(line, ref position) : ParseTraditional(line, ref position);

        if (time is null || position >= line.Length || line[position] != ' ')
        {
            return null;
        }

        position++;

        var hostEnd = line.IndexOf(' ', position);

        if (hostEnd <= position || hostEnd - position > MaxHostBytes || Encoding.UTF8.GetByteCount(line.AsSpan(position, hostEnd - position)) > MaxHostBytes)
        {
            return null;
        }

        var host = line[position..hostEnd];

        return ParseTag(line,
                        hostEnd + 1,
                        new SyslogLine
                        {
                            Priority = priority,
                            Time = time.Value,
                            Host = host
                        });
    }

    /// <summary>
    /// Reads <c>&lt;PRI&gt;</c>: 1 to 3 digits, 0 to 191.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="position">The position of the <c>&lt;</c>; set behind the <c>&gt;</c></param>
    /// <returns>The priority (the number modulo 8), or <c>null</c></returns>
    private static byte? ParsePriority(string line, ref int position)
    {
        var index = 1;
        var value = 0;

        while (index < line.Length && char.IsAsciiDigit(line[index]) && index <= MaxPriorityDigits)
        {
            value = (value * 10) + (line[index] - '0');
            index++;
        }

        if (index == 1 || index >= line.Length || line[index] != '>' || value > MaxPriority)
        {
            return null;
        }

        position = index + 1;

        return (byte)(value % PriorityClasses);
    }

    /// <summary>
    /// Reads <c>Mmm dd HH:MM:SS</c>: a month name, a day with a leading space or zero, and a time of day.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="position">The position of the month; set behind the time</param>
    /// <returns>The time, or <c>null</c></returns>
    private static SyslogTime? ParseTraditional(string line, ref int position)
    {
        if (line.Length - position < TraditionalLength || line[position + 3] != ' ' || line[position + 6] != ' ')
        {
            return null;
        }

        var month = Array.IndexOf(_months, line.Substring(position, 3)) + 1;
        var day = line[position + 4] == ' ' ? Digit(line, position + 5) : Digits(line, position + 4, 2);
        var clock = ParseClock(line, position + 7);

        if (month == 0 || day is < 1 or > 31 || clock is null)
        {
            return null;
        }

        position += TraditionalLength;

        return new SyslogTime(0, month, day, clock.Value.Hour, clock.Value.Minute, clock.Value.Second, 0, null);
    }

    /// <summary>
    /// Reads <c>HH:MM:SS</c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="start">The position of the hour</param>
    /// <returns>The parts, or <c>null</c> when it is not a time of day</returns>
    private static (int Hour, int Minute, int Second)? ParseClock(string line, int start)
    {
        if (line[start + 2] != ':' || line[start + 5] != ':')
        {
            return null;
        }

        var hour = Digits(line, start, 2);
        var minute = Digits(line, start + 3, 2);
        var second = Digits(line, start + 6, 2);

        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return null;
        }

        return (hour, minute, second);
    }

    /// <summary>
    /// Reads an RFC 3339 time: <c>YYYY-MM-DDTHH:MM:SS</c>, an optional fraction of 1 to 9 digits and <c>Z</c> or an offset of up to
    /// 14 hours.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="position">The position of the year; set behind the offset</param>
    /// <returns>The time, or <c>null</c></returns>
    private static SyslogTime? ParseRfc3339(string line, ref int position)
    {
        if (line.Length - position < Rfc3339Length + 1 || line[position + 4] != '-' || line[position + 7] != '-' || line[position + 10] != 'T')
        {
            return null;
        }

        var year = Digits(line, position, 4);
        var month = Digits(line, position + 5, 2);
        var day = Digits(line, position + 8, 2);
        var clock = ParseClock(line, position + 11);

        if (year < 0 || month is < 1 or > 12 || day is < 1 or > 31 || clock is null)
        {
            return null;
        }

        var index = position + Rfc3339Length;
        var ticks = ParseFraction(line, ref index);

        if (ticks is not { } fraction || ParseOffset(line, ref index) is not { } offset)
        {
            return null;
        }

        position = index;

        return new SyslogTime(year, month, day, clock.Value.Hour, clock.Value.Minute, clock.Value.Second, fraction, offset);
    }

    /// <summary>
    /// Reads an optional fraction: a point and 1 to 9 digits, kept to 100 ns.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="index">The position behind the seconds; set behind the fraction</param>
    /// <returns>The fraction in ticks, or <c>null</c> when a point is not followed by 1 to 9 digits</returns>
    private static int? ParseFraction(string line, ref int index)
    {
        if (index >= line.Length || line[index] != '.')
        {
            return 0;
        }

        var start = index + 1;
        var end = start;
        var ticks = 0;

        while (end < line.Length && char.IsAsciiDigit(line[end]) && end - start < MaxFractionDigits + 1)
        {
            if (end - start < FractionTickDigits)
            {
                ticks = (ticks * 10) + (line[end] - '0');
            }

            end++;
        }

        var digits = end - start;

        if (digits is < 1 or > MaxFractionDigits)
        {
            return null;
        }

        for (var padding = digits; padding < FractionTickDigits; padding++)
        {
            ticks *= 10;
        }

        index = end;

        return ticks;
    }

    /// <summary>
    /// Reads <c>Z</c> or <c>+HH:MM</c> / <c>-HH:MM</c> with an offset of at most 14:00.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="index">The position of the offset; set behind it</param>
    /// <returns>The offset in minutes, or <c>null</c></returns>
    private static int? ParseOffset(string line, ref int index)
    {
        if (index >= line.Length)
        {
            return null;
        }

        if (line[index] == 'Z')
        {
            index++;

            return 0;
        }

        if (line[index] is not ('+' or '-') || line.Length - index < 6 || line[index + 3] != ':')
        {
            return null;
        }

        var hours = Digits(line, index + 1, 2);
        var minutes = Digits(line, index + 4, 2);

        if (hours is < 0 or > MaxOffsetHours || minutes is < 0 or > 59 || (hours == MaxOffsetHours && minutes != 0))
        {
            return null;
        }

        var offset = (hours * 60) + minutes;

        index += 6;

        return line[index - 6] == '-' ? -offset : offset;
    }

    /// <summary>
    /// Reads the tag <c>PROGRAM[PID]:</c> or <c>PROGRAM:</c> and the message after it.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="start">The position of the first character after the host and its space</param>
    /// <param name="header">The line parsed so far</param>
    /// <returns>The line with program, process ID and message</returns>
    private static SyslogLine ParseTag(string line, int start, SyslogLine header)
    {
        var index = start;
        var bytes = 0;

        while (index < line.Length && line[index] is not (' ' or '[' or ']' or ':') && bytes <= MaxProgramBytes)
        {
            bytes += Utf8Width(line[index]);
            index++;
        }

        var pid = 0;
        var messageStart = -1;

        if (index > start && bytes <= MaxProgramBytes && index < line.Length)
        {
            messageStart = line[index] == ':' ? index + 1 : ParsePid(line, index, out pid);
        }

        if (messageStart < 0)
        {
            return new SyslogLine
                   {
                       Priority = header.Priority,
                       Time = header.Time,
                       Host = header.Host,
                       Message = line[start..]
                   };
        }

        if (messageStart < line.Length && line[messageStart] == ' ')
        {
            messageStart++;
        }

        return new SyslogLine
               {
                   Priority = header.Priority,
                   Time = header.Time,
                   Host = header.Host,
                   Program = line[start..index],
                   Pid = pid,
                   Message = line[messageStart..]
               };
    }

    /// <summary>
    /// Reads <c>[PID]:</c> of a tag.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="bracket">The position of the <c>[</c></param>
    /// <param name="pid">The process ID; 0 above 2,147,483,647</param>
    /// <returns>The position behind the colon, or -1 when the line has no such tag</returns>
    private static int ParsePid(string line, int bracket, out int pid)
    {
        pid = 0;

        if (line[bracket] != '[')
        {
            return -1;
        }

        long value = 0;
        var index = bracket + 1;

        while (index < line.Length && char.IsAsciiDigit(line[index]) && index - bracket <= MaxPidDigits)
        {
            value = (value * 10) + (line[index] - '0');
            index++;
        }

        if (index == bracket + 1 || index + 1 >= line.Length || line[index] != ']' || line[index + 1] != ':')
        {
            return -1;
        }

        pid = value <= int.MaxValue ? (int)value : 0;

        return index + 2;
    }

    /// <summary>
    /// Returns the UTF-8 bytes a UTF-16 code unit takes; a surrogate pair takes four bytes in all.
    /// </summary>
    /// <param name="character">The code unit</param>
    /// <returns>The number of bytes</returns>
    private static int Utf8Width(char character)
    {
        if (character < 0x80)
        {
            return 1;
        }

        if (character < 0x800 || char.IsSurrogate(character))
        {
            return 2;
        }

        return 3;
    }

    /// <summary>
    /// Reads one ASCII digit.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="index">The position</param>
    /// <returns>The digit, or -1</returns>
    private static int Digit(string line, int index)
    {
        return char.IsAsciiDigit(line[index]) ? line[index] - '0' : -1;
    }

    /// <summary>
    /// Reads a number of ASCII digits.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="index">The position of the first digit</param>
    /// <param name="count">The number of digits</param>
    /// <returns>The number, or -1 when a character is not a digit</returns>
    private static int Digits(string line, int index, int count)
    {
        var value = 0;

        for (var offset = 0; offset < count; offset++)
        {
            var digit = Digit(line, index + offset);

            if (digit < 0)
            {
                return -1;
            }

            value = (value * 10) + digit;
        }

        return value;
    }

    #endregion // Methods
}