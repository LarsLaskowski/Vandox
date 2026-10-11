namespace Vandox.Core.LogParsing;

/// <summary>
/// One parsed line of an error log. The parser is hand-written, linear in the length of the line, ordinal and ASCII only; the digits
/// of the time are not range-checked here and no date is built from them.
/// </summary>
internal sealed class ErrorLogLine
{
    #region Constants

    private const int MaxFractionDigits = 6;
    private const int MaxPidDigits = 10;
    private const int MaxRequestIdLength = 64;
    private const int MaxModuleLength = 64;
    private const int TicksPerFractionDigit = 7;
    private const int YearDigits = 4;
    private const int PairDigits = 2;

    #endregion // Constants

    #region Fields

    private static readonly string[] _pleskLevels = ["EMERG", "ALERT", "CRIT", "ERR", "WARN", "NOTICE", "INFO", "DEBUG"];
    private static readonly string[] _levels = ["emerg", "alert", "crit", "error", "warn", "notice", "info", "debug"];
    private static readonly string[] _weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] _months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the time of the line.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the syslog priority of the line.
    /// </summary>
    internal byte Priority { get; init; }

    /// <summary>
    /// Gets the message of the line.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses a line.
    /// </summary>
    /// <param name="format">The format</param>
    /// <param name="line">The line</param>
    /// <returns>The parsed line, or <c>null</c> when the line is not of the format</returns>
    internal static ErrorLogLine? TryParse(ErrorLogFormat format, string line)
    {
        return format switch
               {
                   ErrorLogFormat.PleskPanel => ParsePleskPanel(line),
                   ErrorLogFormat.Nginx => ParseNginx(line),
                   ErrorLogFormat.Apache => ParseApache(line),
                   _ => null
               };
    }

    /// <summary>
    /// Parses a Plesk panel line: <c>[YYYY-MM-DD HH:MM:SS(.D{1,6})] (PID:REQUESTID )LEVEL( message)</c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The parsed line, or <c>null</c></returns>
    private static ErrorLogLine? ParsePleskPanel(string line)
    {
        var pos = 1;

        if (line.Length > 0 && line[0] == '[' && TryDateTime(line, ref pos, '-', out var time) && TryFraction(line, ref pos, 1, out var ticks) && Expect(line, ref pos, "] "))
        {
            var start = pos;

            if (SkipOptionalProcessAndRequest(line, ref pos))
            {
                var priority = ReadLevel(line, ref pos, _pleskLevels);

                if (priority >= 0)
                {
                    return new ErrorLogLine
                           {
                               Time = time with
                                           {
                                               FractionTicks = ticks
                                           },
                               Priority = (byte)priority,
                               Message = line[start..]
                           };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Skips <c>PID:REQUESTID </c> of a Plesk panel line when the text starts with a digit.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position behind the time stamp; behind the prefix, or unchanged without one</param>
    /// <returns><c>false</c> when a prefix starts but does not have the shape</returns>
    private static bool SkipOptionalProcessAndRequest(string line, ref int pos)
    {
        if (pos < line.Length && char.IsAsciiDigit(line[pos]))
        {
            return SkipProcessAndRequest(line, ref pos);
        }

        return true;
    }

    /// <summary>
    /// Skips <c>PID:REQUESTID </c> of a Plesk panel line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position of the first digit; behind the space on success</param>
    /// <returns><c>true</c> when the prefix has the shape</returns>
    private static bool SkipProcessAndRequest(string line, ref int pos)
    {
        var digits = CountWhile(line, pos, char.IsAsciiDigit, MaxPidDigits + 1);

        if (digits is < 1 or > MaxPidDigits)
        {
            return false;
        }

        pos += digits;

        if (pos >= line.Length || line[pos] != ':')
        {
            return false;
        }

        pos++;

        var length = CountWhile(line, pos, char.IsAsciiLetterOrDigit, MaxRequestIdLength + 1);

        if (length is < 1 or > MaxRequestIdLength)
        {
            return false;
        }

        pos += length;

        return Expect(line, ref pos, " ");
    }

    /// <summary>
    /// Parses an nginx line: <c>YYYY/MM/DD HH:MM:SS [level]( message)</c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The parsed line, or <c>null</c></returns>
    private static ErrorLogLine? ParseNginx(string line)
    {
        var pos = 0;

        if (TryDateTime(line, ref pos, '/', out var time) && Expect(line, ref pos, " ["))
        {
            var bracket = pos - 1;
            var priority = ReadBracketLevel(line, ref pos, _levels);

            if (priority >= 0)
            {
                return new ErrorLogLine
                       {
                           Time = time,
                           Priority = (byte)priority,
                           Message = line[bracket..]
                       };
            }
        }

        return null;
    }

    /// <summary>
    /// Parses an Apache line: <c>[Www Mmm DD HH:MM:SS(.uuuuuu) YYYY] [(module:)level]( message)</c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The parsed line, or <c>null</c></returns>
    private static ErrorLogLine? ParseApache(string line)
    {
        var pos = 1;

        if (line.Length > 0 && line[0] == '[' && TryApacheTime(line, ref pos, out var time) && Expect(line, ref pos, "] ["))
        {
            var bracket = pos - 1;

            if (SkipModule(line, ref pos))
            {
                var priority = ReadBracketLevel(line, ref pos, _levels, "trace");

                if (priority >= 0)
                {
                    return new ErrorLogLine
                           {
                               Time = time,
                               Priority = (byte)priority,
                               Message = line[bracket..]
                           };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the time stamp of an Apache line, up to the closing bracket.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position behind the opening bracket; at the closing bracket on success</param>
    /// <param name="time">The time</param>
    /// <returns><c>true</c> when the time stamp has the shape</returns>
    private static bool TryApacheTime(string line, ref int pos, out SyslogTime time)
    {
        time = default;

        var month = 0;
        var day = 0;
        var hour = 0;
        var minute = 0;
        var second = 0;
        var ticks = 0;
        var year = 0;

        var ok = TryName(line, ref pos, _weekdays) >= 0
                 && Expect(line, ref pos, ' ')
                 && TryMonth(line, ref pos, out month)
                 && Expect(line, ref pos, ' ')
                 && TryNumber(line, ref pos, PairDigits, out day)
                 && Expect(line, ref pos, ' ')
                 && TryClock(line, ref pos, out hour, out minute, out second)
                 && TryFraction(line, ref pos, MaxFractionDigits, out ticks)
                 && Expect(line, ref pos, ' ')
                 && TryNumber(line, ref pos, YearDigits, out year);

        if (ok)
        {
            time = new SyslogTime(year, month, day, hour, minute, second, ticks, null);
        }

        return ok;
    }

    /// <summary>
    /// Reads an English month abbreviation.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position; behind the name on success</param>
    /// <param name="month">The month, 1 to 12</param>
    /// <returns><c>true</c> when the name is a month</returns>
    private static bool TryMonth(string line, ref int pos, out int month)
    {
        month = TryName(line, ref pos, _months) + 1;

        return month > 0;
    }

    /// <summary>
    /// Skips the module and the colon of an Apache level bracket, if the bracket has them.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position behind the opening bracket; behind the colon, or unchanged without a module</param>
    /// <returns><c>false</c> when the bracket cannot be a level bracket</returns>
    private static bool SkipModule(string line, ref int pos)
    {
        var length = CountWhile(line, pos, IsModuleChar, MaxModuleLength + 1);

        if (length > MaxModuleLength || pos + length >= line.Length)
        {
            return false;
        }

        if (line[pos + length] == ':')
        {
            pos += length + 1;
        }

        return true;
    }

    /// <summary>
    /// Tells whether a character may be part of an Apache module name.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for <c>A-Z a-z 0-9 _</c></returns>
    private static bool IsModuleChar(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character == '_';
    }

    /// <summary>
    /// Reads a level bracket body (<c>level]</c>) and the space or end behind it.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position of the level; behind the bracket on success</param>
    /// <param name="levels">The level names; index is the priority</param>
    /// <param name="traceName">The name of a level family <c>nameN</c> with N from 1 to 8 that has priority 7; <c>null</c> for none</param>
    /// <returns>The priority, or -1</returns>
    private static int ReadBracketLevel(string line, ref int pos, string[] levels, string? traceName = null)
    {
        var end = line.IndexOf(']', pos);

        if (end < 0)
        {
            return -1;
        }

        var name = line.AsSpan(pos, end - pos);
        var priority = -1;

        for (var index = 0; index < levels.Length && priority < 0; index++)
        {
            priority = name.SequenceEqual(levels[index]) ? index : -1;
        }

        if (priority < 0 && traceName is not null && name.Length == traceName.Length + 1 && name.StartsWith(traceName, StringComparison.Ordinal) && name[^1] is >= '1' and <= '8')
        {
            priority = 7;
        }

        pos = end + 1;

        return priority >= 0 && (pos == line.Length || line[pos] == ' ') ? priority : -1;
    }

    /// <summary>
    /// Reads an upper-case level name followed by a space or the end of the line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position of the level</param>
    /// <param name="levels">The level names; index is the priority</param>
    /// <returns>The priority, or -1</returns>
    private static int ReadLevel(string line, ref int pos, string[] levels)
    {
        var end = line.IndexOf(' ', pos);

        if (end < 0)
        {
            end = line.Length;
        }

        var name = line.AsSpan(pos, end - pos);

        for (var index = 0; index < levels.Length; index++)
        {
            if (name.SequenceEqual(levels[index]))
            {
                pos = end;

                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads <c>YYYY-MM-DD HH:MM:SS</c> or <c>YYYY/MM/DD HH:MM:SS</c> without a range check.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position of the year; behind the second on success</param>
    /// <param name="separator">The separator of the date</param>
    /// <param name="time">The time, without fraction</param>
    /// <returns><c>true</c> when the digits have the shape</returns>
    private static bool TryDateTime(string line, ref int pos, char separator, out SyslogTime time)
    {
        time = default;

        var month = 0;
        var day = 0;
        var hour = 0;
        var minute = 0;
        var second = 0;

        var ok = TryNumber(line, ref pos, YearDigits, out var year)
                 && Expect(line, ref pos, separator)
                 && TryNumber(line, ref pos, PairDigits, out month)
                 && Expect(line, ref pos, separator)
                 && TryNumber(line, ref pos, PairDigits, out day)
                 && Expect(line, ref pos, ' ')
                 && TryClock(line, ref pos, out hour, out minute, out second);

        if (ok)
        {
            time = new SyslogTime(year, month, day, hour, minute, second, 0, null);
        }

        return ok;
    }

    /// <summary>
    /// Reads <c>HH:MM:SS</c> without a range check.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position of the hour; behind the second on success</param>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <param name="second">The second</param>
    /// <returns><c>true</c> when the digits have the shape</returns>
    private static bool TryClock(string line, ref int pos, out int hour, out int minute, out int second)
    {
        minute = 0;
        second = 0;

        return TryNumber(line, ref pos, PairDigits, out hour)
               && Expect(line, ref pos, ':')
               && TryNumber(line, ref pos, PairDigits, out minute)
               && Expect(line, ref pos, ':')
               && TryNumber(line, ref pos, PairDigits, out second);
    }

    /// <summary>
    /// Reads an optional fraction (<c>.</c> and digits) and converts it to ticks of 100 ns.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position behind the second; behind the fraction on success</param>
    /// <param name="minDigits">The fewest digits of a fraction; the most is six</param>
    /// <param name="ticks">The fraction in ticks; 0 without fraction</param>
    /// <returns><c>true</c> when there is no fraction or it has the shape</returns>
    private static bool TryFraction(string line, ref int pos, int minDigits, out int ticks)
    {
        ticks = 0;

        if (pos >= line.Length || line[pos] != '.')
        {
            return true;
        }

        pos++;

        var digits = CountWhile(line, pos, char.IsAsciiDigit, MaxFractionDigits);

        if (digits >= minDigits && TryNumber(line, ref pos, digits, out var value))
        {
            for (var missing = digits; missing < TicksPerFractionDigit; missing++)
            {
                value *= 10;
            }

            ticks = value;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a fixed number of ASCII digits.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position; behind the digits on success</param>
    /// <param name="count">The number of digits</param>
    /// <param name="value">The value</param>
    /// <returns><c>true</c> when the digits are there</returns>
    private static bool TryNumber(string line, ref int pos, int count, out int value)
    {
        value = 0;

        if (count < 1 || pos + count > line.Length)
        {
            return false;
        }

        var number = 0;

        for (var index = 0; index < count; index++)
        {
            var digit = line[pos + index];

            if (char.IsAsciiDigit(digit))
            {
                number = (number * 10) + (digit - '0');
            }
            else
            {
                return false;
            }
        }

        value = number;
        pos += count;

        return true;
    }

    /// <summary>
    /// Reads one of the three-letter names of a list.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position; behind the name on success</param>
    /// <param name="names">The names</param>
    /// <returns>The index of the name, or -1</returns>
    private static int TryName(string line, ref int pos, string[] names)
    {
        if (pos + 3 > line.Length)
        {
            return -1;
        }

        var span = line.AsSpan(pos, 3);

        for (var index = 0; index < names.Length; index++)
        {
            if (span.SequenceEqual(names[index]))
            {
                pos += 3;

                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Counts the characters from a position that satisfy a predicate, at most <paramref name="max"/>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The start</param>
    /// <param name="predicate">The predicate</param>
    /// <param name="max">The most characters to count</param>
    /// <returns>The count</returns>
    private static int CountWhile(string line, int pos, Func<char, bool> predicate, int max)
    {
        var count = 0;

        while (count < max && pos + count < line.Length && predicate(line[pos + count]))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Consumes a character.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position; behind the character on success</param>
    /// <param name="expected">The character</param>
    /// <returns><c>true</c> when the line has the character at the position</returns>
    private static bool Expect(string line, ref int pos, char expected)
    {
        if (pos < line.Length && line[pos] == expected)
        {
            pos++;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Consumes a text.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="pos">The position; behind the text on success</param>
    /// <param name="expected">The text</param>
    /// <returns><c>true</c> when the line has the text at the position</returns>
    private static bool Expect(string line, ref int pos, string expected)
    {
        if (line.AsSpan(pos).StartsWith(expected, StringComparison.Ordinal))
        {
            pos += expected.Length;

            return true;
        }

        return false;
    }

    #endregion // Methods
}