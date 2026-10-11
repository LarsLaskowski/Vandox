using System.Text.Unicode;

namespace Vandox.Core.Wire;

/// <summary>
/// Checks the raw bytes of a wire line for valid UTF-8 and for surrogate escapes that are not a high half directly followed by an escaped low half.
/// </summary>
internal static class WireTextCheck
{
    #region Constants

    /// <summary>
    /// The reason for a line that is not valid UTF-8.
    /// </summary>
    internal const string NotUtf8 = "line not valid UTF-8";

    /// <summary>
    /// The reason for a line with an unpaired surrogate escape.
    /// </summary>
    internal const string UnpairedSurrogate = "unpaired surrogate escape";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Finds the first violation of the text rule in a line.
    /// </summary>
    /// <param name="line">The bytes of the line</param>
    /// <returns><c>null</c> when the line keeps the rule, otherwise <see cref="NotUtf8"/> or <see cref="UnpairedSurrogate"/></returns>
    internal static string? FindViolation(ReadOnlySpan<byte> line)
    {
        if (Utf8.IsValid(line) is false)
        {
            return NotUtf8;
        }

        var index = 0;

        while (index < line.Length)
        {
            if (line[index] != (byte)'\\')
            {
                index++;

                continue;
            }

            if (index + 1 >= line.Length || line[index + 1] != (byte)'u')
            {
                index += 2;

                continue;
            }

            if (TryReadEscape(line, index, out var unit) is false)
            {
                index++;

                continue;
            }

            if (unit is >= 0xDC00 and <= 0xDFFF)
            {
                return UnpairedSurrogate;
            }

            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                if (TryReadEscape(line, index + 6, out var low) is false || low is < 0xDC00 or > 0xDFFF)
                {
                    return UnpairedSurrogate;
                }

                index += 12;

                continue;
            }

            index += 6;
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>\uXXXX</c> escape that starts at <paramref name="index"/>.
    /// </summary>
    /// <param name="line">The bytes of the line</param>
    /// <param name="index">The position of the backslash</param>
    /// <param name="codeUnit">The UTF-16 code unit of the escape</param>
    /// <returns><c>true</c> when six bytes of a complete escape with four hex digits start there</returns>
    private static bool TryReadEscape(ReadOnlySpan<byte> line, int index, out int codeUnit)
    {
        codeUnit = 0;

        if (index < 0 || line.Length - index < 6 || line[index] != (byte)'\\' || line[index + 1] != (byte)'u')
        {
            return false;
        }

        var value = 0;

        for (var offset = 2; offset < 6; offset++)
        {
            var digit = line[index + offset];
            int nibble;

            if (digit is >= (byte)'0' and <= (byte)'9')
            {
                nibble = digit - '0';
            }
            else if (digit is >= (byte)'a' and <= (byte)'f')
            {
                nibble = digit - 'a' + 10;
            }
            else if (digit is >= (byte)'A' and <= (byte)'F')
            {
                nibble = digit - 'A' + 10;
            }
            else
            {
                return false;
            }

            value = (value << 4) | nibble;
        }

        codeUnit = value;

        return true;
    }

    #endregion // Methods
}
