using System.Globalization;
using System.Text;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the summary lines, the table header and the rows of top.
/// </summary>
internal static class LegacyTopLine
{
    #region Constants

    private const string LoadMarker = "load average:";
    private const string TasksPrefix = "Tasks:";
    private const string CpuPrefix = "%Cpu(s):";
    private const string MemorySuffix = " Mem :";
    private const string SwapSuffix = " Swap:";
    private const string UsedLabel = "used.";
    private const int MaxIntegerDigits = 15;
    private const int MaxFractionDigits = 6;
    private const int MaxMemoryIntegerDigits = 20;
    private const int MaxMemoryFractionDigits = 3;
    private const int MaxRowTokenBytes = 64;
    private const int RowColumns = 11;
    private const int MaxPidDigits = 10;
    private const int UnitLength = 3;
    private const int BitsPerPower = 10;
    private const int MaxScaleDigits = 30;

    #endregion // Constants

    #region Fields

    private static readonly string[] _units = ["KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] _cpuLabels = ["us", "sy", "ni", "id", "wa", "hi", "si", "st"];
    private static readonly string[] _memoryLabels = ["total", "free", "used", "buff/cache"];
    private static readonly string[] _swapLabels = ["total", "free", "used", "avail Mem"];
    private static readonly string[] _tableHeader = ["PID", "USER", "PR", "NI", "VIRT", "RES", "SHR", "S", "%CPU", "%MEM", "TIME+", "COMMAND"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Reads the load average line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="load">The load over 1, 5 and 15 minutes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseLoad(string line, out double[] load)
    {
        load = [];

        var index = line.StartsWith("top - ", StringComparison.Ordinal) ? line.IndexOf(LoadMarker, StringComparison.Ordinal) : -1;

        if (index < 0)
        {
            return false;
        }

        var pieces = line[(index + LoadMarker.Length)..].Split(',');

        if (pieces.Length != 3)
        {
            return false;
        }

        var values = new double[3];

        for (var position = 0; position < values.Length; position++)
        {
            if (TryParseNumber(pieces[position].Trim(' '), out values[position]))
            {
                continue;
            }

            return false;
        }

        load = values;

        return true;
    }

    /// <summary>
    /// Reads the tasks line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="total">The total number of tasks</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseTasks(string line, out uint total)
    {
        total = 0;

        var rest = line.StartsWith(TasksPrefix, StringComparison.Ordinal) ? line[TasksPrefix.Length..] : string.Empty;

        foreach (var piece in rest.Split(','))
        {
            if (TrySplitPiece(piece, out var number, out var label) && label == "total")
            {
                return IsDigits(number, 1, MaxPidDigits) && uint.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out total);
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the CPU line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="percents">The values of us, sy, ni, id, wa, hi, si and st</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseCpu(string line, out double[] percents)
    {
        percents = [];

        var pieces = line.StartsWith(CpuPrefix, StringComparison.Ordinal) ? line[CpuPrefix.Length..].Split(',') : [];

        if (pieces.Length != _cpuLabels.Length)
        {
            return false;
        }

        var values = new double[_cpuLabels.Length];
        var seen = 0;

        foreach (var piece in pieces)
        {
            var slot = TrySplitPiece(piece, out var number, out var label) ? Array.IndexOf(_cpuLabels, label) : -1;

            if (slot >= 0 && (seen & (1 << slot)) == 0 && TryParseNumber(number, out values[slot]))
            {
                seen |= 1 << slot;

                continue;
            }

            return false;
        }

        percents = values;

        return true;
    }

    /// <summary>
    /// Reads the memory line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="memory">Total, free, used and buff/cache in bytes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseMemory(string line, out ulong[] memory)
    {
        return TryParseByteLine(line, MemorySuffix, _memoryLabels, false, out memory);
    }

    /// <summary>
    /// Reads the swap line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="swap">Total, free, used and avail Mem in bytes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseSwap(string line, out ulong[] swap)
    {
        return TryParseByteLine(line, SwapSuffix, _swapLabels, true, out swap);
    }

    /// <summary>
    /// Tells whether a line is the header of the process table.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for the table header</returns>
    internal static bool IsTableHeader(string line)
    {
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(_tableHeader, StringComparer.Ordinal);
    }

    /// <summary>
    /// Tells whether a line begins like one of the summary lines of top.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> when the line begins like a summary line</returns>
    internal static bool IsSummaryStart(string line)
    {
        if (line.StartsWith("top - ", StringComparison.Ordinal) || line.StartsWith(TasksPrefix, StringComparison.Ordinal) || line.StartsWith(CpuPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        return line.Length > UnitLength
               && Array.IndexOf(_units, line[..UnitLength]) >= 0
               && (line.AsSpan(UnitLength).StartsWith(" Mem ", StringComparison.Ordinal) || line.AsSpan(UnitLength).StartsWith(SwapSuffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads a row of the process table.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The row; <c>null</c> when not read</returns>
    internal static LegacyTopRow? TryParseRow(string line)
    {
        var tokens = new string[RowColumns];
        var position = 0;

        for (var column = 0; column < RowColumns; column++)
        {
            while (position < line.Length && line[position] == ' ')
            {
                position++;
            }

            if (position >= line.Length)
            {
                return null;
            }

            var end = line.IndexOf(' ', position);

            end = end < 0 ? line.Length : end;
            tokens[column] = line[position..end];
            position = end;

            if (tokens[column].Length > MaxRowTokenBytes || Encoding.UTF8.GetByteCount(tokens[column]) > MaxRowTokenBytes)
            {
                return null;
            }
        }

        var command = line[position..].Trim(' ');

        return command.Length == 0 ? null : BuildRow(tokens, command);
    }

    /// <summary>
    /// Reads a RES value with its optional suffix.
    /// </summary>
    /// <param name="token">The token</param>
    /// <param name="bytes">The value in bytes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseMemoryValue(string token, out ulong bytes)
    {
        bytes = 0;

        var power = 1;
        var body = token;

        if (token.Length > 0 && char.IsAsciiLetter(token[^1]))
        {
            power = "kmgtpe".IndexOf(token[^1], StringComparison.Ordinal) + 1;
            body = token[..^1];
        }

        return power > 0
               && TrySplitDecimal(body, MaxMemoryIntegerDigits, MaxMemoryFractionDigits, out var integer, out var fraction)
               && TryScaleBinary(integer, fraction, power, out bytes);
    }

    /// <summary>
    /// Scales a decimal number by a power of 1024 in exact integer arithmetic.
    /// </summary>
    /// <param name="integerDigits">The validated ASCII digits of the integer part</param>
    /// <param name="fractionDigits">The validated ASCII digits of the fraction, at most 6 digits for the callers; may be empty</param>
    /// <param name="power">The power of 1024, 0 to 6</param>
    /// <param name="bytes">The value rounded half away from zero</param>
    /// <returns><c>true</c> when the value fits 64 bits</returns>
    internal static bool TryScaleBinary(string integerDigits, string fractionDigits, int power, out ulong bytes)
    {
        bytes = 0;

        if (integerDigits.Length > MaxScaleDigits || fractionDigits.Length > MaxScaleDigits)
        {
            return false;
        }

        var shift = BitsPerPower * power;
        var integer = ToUInt128(integerDigits);

        if (integer > (UInt128)(ulong.MaxValue >> shift))
        {
            return false;
        }

        var scale = UInt128.One;

        for (var index = 0; index < fractionDigits.Length; index++)
        {
            scale *= 10;
        }

        var scaled = ((integer * scale) + ToUInt128(fractionDigits)) << shift;
        var result = (scaled + (scale / 2)) / scale;

        if (result > ulong.MaxValue)
        {
            return false;
        }

        bytes = (ulong)result;

        return true;
    }

    /// <summary>
    /// Reads a plain decimal number: 1 to 15 ASCII digits and an optional point with 1 to 6 ASCII digits.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="value">The value; finite and below 10^15 when read</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseNumber(string text, out double value)
    {
        value = 0;

        if (TrySplitDecimal(text, MaxIntegerDigits, MaxFractionDigits, out _, out _))
        {
            value = double.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Tells whether a text consists of ASCII digits only.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="minimum">The least number of digits</param>
    /// <param name="maximum">The most digits</param>
    /// <returns><c>true</c> for 1 to the maximum ASCII digits</returns>
    internal static bool IsDigits(string text, int minimum, int maximum)
    {
        return text.Length >= minimum && text.Length <= maximum && text.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Splits a decimal number into the digits before and after the point.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="maxInteger">The most digits before the point</param>
    /// <param name="maxFraction">The most digits after the point</param>
    /// <param name="integer">The digits before the point</param>
    /// <param name="fraction">The digits after the point; empty without a point</param>
    /// <returns><c>true</c> when the text is ASCII digits with an optional point and fraction within the bounds</returns>
    private static bool TrySplitDecimal(string text, int maxInteger, int maxFraction, out string integer, out string fraction)
    {
        var point = text.IndexOf('.', StringComparison.Ordinal);

        integer = point < 0 ? text : text[..point];
        fraction = point < 0 ? string.Empty : text[(point + 1)..];

        return IsDigits(integer, 1, maxInteger) && (point < 0 || IsDigits(fraction, 1, maxFraction));
    }

    /// <summary>
    /// Splits a piece of a summary line into its number text and its label.
    /// </summary>
    /// <param name="piece">The piece between two commas</param>
    /// <param name="number">The text before the first space</param>
    /// <param name="label">The text after the spaces, without trailing spaces</param>
    /// <returns><c>true</c> when the piece has a number text and a label</returns>
    private static bool TrySplitPiece(string piece, out string number, out string label)
    {
        var trimmed = piece.Trim(' ');
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);

        number = space < 0 ? trimmed : trimmed[..space];
        label = space < 0 ? string.Empty : trimmed[(space + 1)..].TrimStart(' ');

        return space > 0 && label.Length > 0;
    }

    /// <summary>
    /// Reads a memory or swap line into bytes.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="suffix">The text after the unit: the label of the line</param>
    /// <param name="labels">The four labels in result order</param>
    /// <param name="swap"><c>true</c> to split the line at the point after the used label</param>
    /// <param name="values">The four values in bytes</param>
    /// <returns><c>true</c> when read</returns>
    private static bool TryParseByteLine(string line, string suffix, string[] labels, bool swap, out ulong[] values)
    {
        values = [];

        var power = line.Length > UnitLength && line.AsSpan(UnitLength).StartsWith(suffix, StringComparison.Ordinal) ? Array.IndexOf(_units, line[..UnitLength]) + 1 : 0;

        if (power == 0)
        {
            return false;
        }

        var rest = line[(UnitLength + suffix.Length)..];
        var used = swap ? rest.IndexOf(UsedLabel, StringComparison.Ordinal) : -1;

        if (swap && used >= 0)
        {
            rest = string.Concat(rest.AsSpan(0, used + UsedLabel.Length - 1), ",", rest.AsSpan(used + UsedLabel.Length));
        }

        var pieces = rest.Split(',');

        if (pieces.Length != labels.Length)
        {
            return false;
        }

        var result = new ulong[labels.Length];
        var seen = 0;

        foreach (var piece in pieces)
        {
            var slot = TrySplitPiece(piece, out var number, out var label) ? Array.IndexOf(labels, label) : -1;

            if (slot >= 0 && (seen & (1 << slot)) == 0 && TryScaleNumber(number, power, out result[slot]))
            {
                seen |= 1 << slot;

                continue;
            }

            return false;
        }

        values = result;

        return true;
    }

    /// <summary>
    /// Reads a number of a summary line and scales it by a power of 1024.
    /// </summary>
    /// <param name="number">The number text</param>
    /// <param name="power">The power of 1024</param>
    /// <param name="bytes">The value in bytes</param>
    /// <returns><c>true</c> when the number is a plain decimal number whose value fits 64 bits</returns>
    private static bool TryScaleNumber(string number, int power, out ulong bytes)
    {
        bytes = 0;

        return TrySplitDecimal(number, MaxIntegerDigits, MaxFractionDigits, out var integer, out var fraction) && TryScaleBinary(integer, fraction, power, out bytes);
    }

    /// <summary>
    /// Builds a row from its columns.
    /// </summary>
    /// <param name="tokens">The eleven columns before the command</param>
    /// <param name="command">The command column</param>
    /// <returns>The row; <c>null</c> when a column is not valid</returns>
    private static LegacyTopRow? BuildRow(string[] tokens, string command)
    {
        if (IsDigits(tokens[0], 1, MaxPidDigits) && int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 && tokens[7].Length == 1 && char.IsAsciiLetter(tokens[7][0]))
        {
            return TryParseMemoryValue(tokens[5], out var res) && TryParseNumber(tokens[8], out var cpu) ? new LegacyTopRow(pid, tokens[1], tokens[7], cpu, res, command) : null;
        }

        return null;
    }

    /// <summary>
    /// Converts ASCII digits to a number.
    /// </summary>
    /// <param name="digits">The digits; at most 30</param>
    /// <returns>The number</returns>
    private static UInt128 ToUInt128(string digits)
    {
        var result = UInt128.Zero;

        foreach (var digit in digits)
        {
            result = (result * 10) + (uint)(digit - '0');
        }

        return result;
    }

    #endregion // Methods
}