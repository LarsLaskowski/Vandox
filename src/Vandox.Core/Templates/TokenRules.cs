using System.Buffers;

namespace Vandox.Core.Templates;

/// <summary>
/// The token rules a to j of the template normalization (<c>docs/areas/detection-and-reports.md</c>, <em>Log templates</em>): each
/// rule is one scan of the token that stops at its length, and only ordinal ASCII comparisons are made.
/// </summary>
internal static class TokenRules
{
    #region Constants

    private const int UuidLength = 36;
    private const int MaxOctet = 255;
    private const int MaxPrefix = 32;
    private const int MaxPort = 5;
    private const int MaxIpv6Colons = 7;
    private const int MaxGroupDigits = 4;
    private const int MaxUnitLetters = 4;

    #endregion // Constants

    #region Fields

    private static readonly SearchValues<char> _localPartCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._%+-");
    private static readonly SearchValues<char> _hexCharacters = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly SearchValues<char> _ipv6Characters = SearchValues.Create("0123456789abcdefABCDEF:.");
    private static readonly SearchValues<char> _numberCharacters = SearchValues.Create("0123456789.:/-+%");
    private static readonly SearchValues<char> _letters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Writes the replacement of a token: trailing dots and colons are split off and kept after it.
    /// </summary>
    /// <param name="token">The token, which holds no separator and no delimiter</param>
    /// <param name="writer">Receives the replacement</param>
    internal static void Write(ReadOnlySpan<char> token, ref TemplateWriter writer)
    {
        var length = token.Length;

        while (length > 0 && token[length - 1] is '.' or ':')
        {
            length--;
        }

        WriteCore(token[..length], true, ref writer);
        writer.Append(token[length..]);
    }

    /// <summary>
    /// Tells whether a token is a path (rule a).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is a path</returns>
    internal static bool IsPath(ReadOnlySpan<char> text)
    {
        return (text.Length >= 2 && text[0] == '/') || text.StartsWith("./") || text.StartsWith("../") || text.StartsWith("~/");
    }

    /// <summary>
    /// Tells whether a token is an e-mail address (rule b).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is an e-mail address</returns>
    internal static bool IsEmail(ReadOnlySpan<char> text)
    {
        var at = text.IndexOf('@');

        if (at <= 0 || text.LastIndexOf('@') != at)
        {
            return false;
        }

        return text[..at].IndexOfAnyExcept(_localPartCharacters) < 0 && IsDomain(text[(at + 1)..]);
    }

    /// <summary>
    /// Tells whether a token is an IPv4 address, optionally with a prefix length or a port (rule c).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is an IPv4 address</returns>
    internal static bool IsIpv4(ReadOnlySpan<char> text)
    {
        return IsIpv4(text, true);
    }

    /// <summary>
    /// Tells whether a token is an IPv6 address (rule d).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is an IPv6 address</returns>
    internal static bool IsIpv6(ReadOnlySpan<char> text)
    {
        if (text.IndexOfAnyExcept(_ipv6Characters) >= 0)
        {
            return false;
        }

        var colons = text.Count(':');

        if (colons == 0 || colons > MaxIpv6Colons)
        {
            return false;
        }

        var gap = text.IndexOf("::");

        if (gap < 0)
        {
            return colons == MaxIpv6Colons && AreGroups(text, true);
        }

        if (text[(gap + 1)..].IndexOf("::") >= 0)
        {
            return false;
        }

        return AreGroups(text[..gap], false) && AreGroups(text[(gap + 2)..], true);
    }

    /// <summary>
    /// Tells whether a token is a hex number with a prefix (rule e).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is a hex number with a prefix</returns>
    internal static bool IsPrefixedHex(ReadOnlySpan<char> text)
    {
        return text.Length >= 3 && text[0] == '0' && (text[1] is 'x' or 'X') && IsHex(text[2..]);
    }

    /// <summary>
    /// Tells whether a token is a UUID (rule f).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is a UUID</returns>
    internal static bool IsUuid(ReadOnlySpan<char> text)
    {
        if (text.Length != UuidLength)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (IsUuidCharacter(text[index], index))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells whether a token is a number (rule g).
    /// </summary>
    /// <param name="text">The token</param>
    /// <returns><c>true</c> when the token is a number</returns>
    internal static bool IsNumber(ReadOnlySpan<char> text)
    {
        return text.IndexOfAnyExcept(_numberCharacters) < 0 && text.IndexOfAnyInRange('0', '9') >= 0;
    }

    /// <summary>
    /// Tells whether a token is a number followed by a unit of one to four letters (rule h).
    /// </summary>
    /// <param name="text">The token</param>
    /// <param name="unitStart">The index of the first letter of the unit</param>
    /// <returns><c>true</c> when the token is a number with a unit</returns>
    internal static bool IsNumberWithUnit(ReadOnlySpan<char> text, out int unitStart)
    {
        var position = SkipDigits(text, 0);

        unitStart = -1;

        if (position == 0)
        {
            return false;
        }

        if (position < text.Length && text[position] == '.')
        {
            var fraction = SkipDigits(text, position + 1);

            if (fraction == position + 1)
            {
                return false;
            }

            position = fraction;
        }

        var unit = text[position..];

        if (unit.Length is < 1 or > MaxUnitLetters || unit.IndexOfAnyExcept(_letters) >= 0)
        {
            return false;
        }

        unitStart = position;

        return true;
    }

    /// <summary>
    /// Writes the replacement of a token without its trailing dots and colons.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="splitColons">Whether rule i applies, which is not the case for the parts of a token</param>
    /// <param name="writer">Receives the replacement</param>
    private static void WriteCore(ReadOnlySpan<char> text, bool splitColons, ref TemplateWriter writer)
    {
        if (text.IsEmpty)
        {
            return;
        }

        var placeholder = Classify(text, out var unitStart);

        if (placeholder.Length > 0)
        {
            writer.Append(placeholder);

            if (unitStart >= 0)
            {
                writer.Append(text[unitStart..]);
            }
        }
        else if (splitColons && text.Contains(':'))
        {
            WriteParts(text, ref writer);
        }
        else if (text.IndexOfAnyInRange('0', '9') >= 0)
        {
            writer.Append(LogTemplateNormalizer.IdPlaceholder);
        }
        else
        {
            writer.AppendText(text);
        }
    }

    /// <summary>
    /// Writes a token that holds a colon part by part (rule i).
    /// </summary>
    /// <param name="text">The token</param>
    /// <param name="writer">Receives the replacement</param>
    private static void WriteParts(ReadOnlySpan<char> text, ref TemplateWriter writer)
    {
        var rest = text;

        while (true)
        {
            var colon = rest.IndexOf(':');

            if (colon < 0)
            {
                WriteCore(rest, false, ref writer);

                return;
            }

            WriteCore(rest[..colon], false, ref writer);
            writer.Append(':');
            rest = rest[(colon + 1)..];
        }
    }

    /// <summary>
    /// Finds the first rule of a to h that applies to a token.
    /// </summary>
    /// <param name="text">The token</param>
    /// <param name="unitStart">The index of the unit of a number with a unit, otherwise -1</param>
    /// <returns>The placeholder, or an empty text when no rule applies</returns>
    private static string Classify(ReadOnlySpan<char> text, out int unitStart)
    {
        unitStart = -1;

        if (IsPath(text))
        {
            return LogTemplateNormalizer.PathPlaceholder;
        }

        if (IsEmail(text))
        {
            return LogTemplateNormalizer.EmailPlaceholder;
        }

        if (IsIpv4(text) || IsIpv6(text))
        {
            return LogTemplateNormalizer.IpPlaceholder;
        }

        if (IsPrefixedHex(text))
        {
            return LogTemplateNormalizer.HexPlaceholder;
        }

        if (IsUuid(text))
        {
            return LogTemplateNormalizer.IdPlaceholder;
        }

        if (IsNumber(text))
        {
            return LogTemplateNormalizer.NumberPlaceholder;
        }

        return IsNumberWithUnit(text, out unitStart) ? LogTemplateNormalizer.NumberPlaceholder : string.Empty;
    }

    /// <summary>
    /// Tells whether a text is one or more labels of letters, digits and hyphens, separated by single dots.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is a domain</returns>
    private static bool IsDomain(ReadOnlySpan<char> text)
    {
        var label = 0;

        foreach (var value in text)
        {
            if (value == '.')
            {
                if (label == 0)
                {
                    return false;
                }

                label = 0;
            }
            else if (char.IsAsciiLetterOrDigit(value) || value == '-')
            {
                label++;
            }
            else
            {
                return false;
            }
        }

        return label > 0;
    }

    /// <summary>
    /// Tells whether a text is an IPv4 address.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="allowSuffix">Whether a prefix length or a port may follow</param>
    /// <returns><c>true</c> when the text is an IPv4 address</returns>
    private static bool IsIpv4(ReadOnlySpan<char> text, bool allowSuffix)
    {
        var position = 0;

        for (var group = 0; group < 4; group++)
        {
            if (TryReadOctet(text, ref position) && (group == 3 || TryReadDot(text, ref position)))
            {
                continue;
            }

            return false;
        }

        if (position == text.Length)
        {
            return true;
        }

        return allowSuffix && IsSuffix(text[position], text[(position + 1)..]);
    }

    /// <summary>
    /// Tells whether the rest of an IPv4 address is a prefix length or a port.
    /// </summary>
    /// <param name="marker">The character that starts the rest</param>
    /// <param name="digits">The rest after the marker</param>
    /// <returns><c>true</c> when the rest is a prefix length or a port</returns>
    private static bool IsSuffix(char marker, ReadOnlySpan<char> digits)
    {
        if (digits.IsEmpty || SkipDigits(digits, 0) != digits.Length)
        {
            return false;
        }

        if (marker == ':')
        {
            return digits.Length <= MaxPort;
        }

        if (marker != '/' || digits.Length > 2)
        {
            return false;
        }

        var prefix = digits.Length == 2 ? ((digits[0] - '0') * 10) + (digits[1] - '0') : digits[0] - '0';

        return prefix <= MaxPrefix;
    }

    /// <summary>
    /// Reads one to three digits that are 0 to 255.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="position">The position, advanced past the digits</param>
    /// <returns><c>true</c> when an octet was read</returns>
    private static bool TryReadOctet(ReadOnlySpan<char> text, ref int position)
    {
        var start = position;
        var value = 0;

        while (position < text.Length && position - start < 3 && char.IsAsciiDigit(text[position]))
        {
            value = (value * 10) + (text[position] - '0');
            position++;
        }

        return position > start && value <= MaxOctet;
    }

    /// <summary>
    /// Reads a dot.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="position">The position, advanced past the dot</param>
    /// <returns><c>true</c> when a dot was read</returns>
    private static bool TryReadDot(ReadOnlySpan<char> text, ref int position)
    {
        if (position < text.Length && text[position] == '.')
        {
            position++;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Tells whether a text is groups of one to four hex digits separated by colons; the last group may be an IPv4 address.
    /// An empty text is accepted.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="lastMayBeIpv4">Whether the last group may be an IPv4 address</param>
    /// <returns><c>true</c> when the text is valid</returns>
    private static bool AreGroups(ReadOnlySpan<char> text, bool lastMayBeIpv4)
    {
        var rest = text;

        while (rest.Length > 0)
        {
            var colon = rest.IndexOf(':');
            var last = colon < 0;

            if (IsGroup(last ? rest : rest[..colon], last && lastMayBeIpv4))
            {
                if (last)
                {
                    return true;
                }

                rest = rest[(colon + 1)..];

                if (rest.IsEmpty)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells whether a text is one to four hex digits, or an IPv4 address when that is allowed.
    /// </summary>
    /// <param name="group">The group</param>
    /// <param name="mayBeIpv4">Whether an IPv4 address is allowed</param>
    /// <returns><c>true</c> when the group is valid</returns>
    private static bool IsGroup(ReadOnlySpan<char> group, bool mayBeIpv4)
    {
        if (group.Length is >= 1 and <= MaxGroupDigits && IsHex(group))
        {
            return true;
        }

        return mayBeIpv4 && IsIpv4(group, false);
    }

    /// <summary>
    /// Tells whether a text holds only hex digits; an empty text does.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when every character is a hex digit</returns>
    private static bool IsHex(ReadOnlySpan<char> text)
    {
        return text.IndexOfAnyExcept(_hexCharacters) < 0;
    }

    /// <summary>
    /// Tells whether a character fits its position in a UUID: a hyphen at the group borders, otherwise a hex digit.
    /// </summary>
    /// <param name="value">The character</param>
    /// <param name="index">The position</param>
    /// <returns><c>true</c> when the character fits</returns>
    private static bool IsUuidCharacter(char value, int index)
    {
        return index is 8 or 13 or 18 or 23 ? value == '-' : char.IsAsciiHexDigit(value);
    }

    /// <summary>
    /// Returns the index after the digits that start at a position.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="start">The start</param>
    /// <returns>The index of the first character that is no digit</returns>
    private static int SkipDigits(ReadOnlySpan<char> text, int start)
    {
        var position = start;

        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
        }

        return position;
    }

    #endregion // Methods
}