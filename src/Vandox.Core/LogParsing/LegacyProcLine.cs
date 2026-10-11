using System.Globalization;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the lines of the meminfo, pressure and vmstat sections.
/// </summary>
internal static class LegacyProcLine
{
    #region Constants

    private const int MaxNameCharacters = 64;
    private const int MaxIntegerDigits = 20;
    private const int KilobyteShift = 10;
    private const int PressureGroupTokens = 5;
    private const string KilobyteSuffix = " kB";
    private const string NotAvailable = "n/a";

    #endregion // Constants

    #region Fields

    private static readonly string[] _resources = ["memory", "cpu", "io"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Reads a meminfo line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="field">The field name</param>
    /// <param name="value">The byte count for kB, else the number</param>
    /// <param name="kilobytes">Whether the line had the unit kB</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseMeminfo(string line, out string field, out ulong value, out bool kilobytes)
    {
        field = string.Empty;
        value = 0;
        kilobytes = false;

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        var name = colon < 0 ? string.Empty : line[..colon];
        var rest = colon < 0 ? string.Empty : line[(colon + 1)..].Trim(' ');
        var unit = rest.EndsWith(KilobyteSuffix, StringComparison.Ordinal);
        var digits = unit ? rest[..^KilobyteSuffix.Length].TrimEnd(' ') : rest;
        var limit = unit ? ulong.MaxValue >> KilobyteShift : ulong.MaxValue;

        if (name.Length is >= 1 and <= MaxNameCharacters && name.All(IsMeminfoCharacter) && TryParseInteger(digits, out var number) && number <= limit)
        {
            field = name;
            kilobytes = unit;
            value = unit ? number << KilobyteShift : number;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Tells whether a pressure line says that the pressure is not available.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for <c>n/a</c> or <c>&lt;res&gt;: n/a</c></returns>
    internal static bool IsNotAvailable(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return (tokens.Length == 1 && tokens[0] == NotAvailable)
               || (tokens.Length == 2 && tokens[1] == NotAvailable && tokens[0].EndsWith(':') && Array.IndexOf(_resources, tokens[0][..^1]) >= 0);
    }

    /// <summary>
    /// Reads a pressure line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="previousResource">The resource of the previous line that had only a some part; <c>null</c> when none</param>
    /// <returns>The pressure entries of the line; <c>null</c> when not read</returns>
    internal static IReadOnlyList<LegacyPressure>? TryParsePressure(string line, string? previousResource)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var prefixed = tokens.Length > 0 && tokens[0].EndsWith(':');
        var resource = prefixed ? tokens[0][..^1] : previousResource;
        var first = prefixed ? 1 : 0;
        var groups = (tokens.Length - first) / PressureGroupTokens;

        if (resource is null || Array.IndexOf(_resources, resource) < 0 || (tokens.Length - first) % PressureGroupTokens != 0)
        {
            return null;
        }

        return groups == 1 || (groups == 2 && prefixed) ? ReadPressureGroups(tokens, first, resource, groups, prefixed) : null;
    }

    /// <summary>
    /// Reads a vmstat line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="counter">The counter name</param>
    /// <param name="value">The value</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseVmstat(string line, out string counter, out ulong value)
    {
        counter = string.Empty;
        value = 0;

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 2 && tokens[0].Length <= MaxNameCharacters && tokens[0].All(IsVmstatCharacter) && TryParseInteger(tokens[1], out value))
        {
            counter = tokens[0];

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the groups of a pressure line.
    /// </summary>
    /// <param name="tokens">The tokens of the line</param>
    /// <param name="first">The index of the first kind token</param>
    /// <param name="resource">The resource</param>
    /// <param name="groups">The number of groups, 1 or 2</param>
    /// <param name="prefixed">Whether the line began with the resource</param>
    /// <returns>The entries; <c>null</c> when a group is not read</returns>
    private static List<LegacyPressure>? ReadPressureGroups(string[] tokens, int first, string resource, int groups, bool prefixed)
    {
        var entries = new List<LegacyPressure>(groups);

        for (var group = 0; group < groups; group++)
        {
            var expectedKind = prefixed && group == 0 ? "some" : "full";
            var entry = ReadPressureGroup(tokens, first + (group * PressureGroupTokens), resource, expectedKind);

            if (entry is null)
            {
                return null;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Reads one <c>some</c> or <c>full</c> group of a pressure line.
    /// </summary>
    /// <param name="tokens">The tokens of the line</param>
    /// <param name="start">The index of the kind token</param>
    /// <param name="resource">The resource</param>
    /// <param name="kind">The kind the group must have</param>
    /// <returns>The entry; <c>null</c> when the group is not read</returns>
    private static LegacyPressure? ReadPressureGroup(string[] tokens, int start, string resource, string kind)
    {
        if (tokens[start] == kind
            && TryReadAverage(tokens[start + 1], "avg10=", out var avg10)
            && TryReadAverage(tokens[start + 2], "avg60=", out var avg60)
            && TryReadAverage(tokens[start + 3], "avg300=", out var avg300)
            && tokens[start + 4].StartsWith("total=", StringComparison.Ordinal)
            && TryParseInteger(tokens[start + 4]["total=".Length..], out var total))
        {
            return new LegacyPressure(resource, kind, avg10, avg60, avg300, total);
        }

        return null;
    }

    /// <summary>
    /// Reads a <c>name=number</c> token.
    /// </summary>
    /// <param name="token">The token</param>
    /// <param name="prefix">The name with the equals sign</param>
    /// <param name="value">The value</param>
    /// <returns><c>true</c> when read</returns>
    private static bool TryReadAverage(string token, string prefix, out double value)
    {
        value = 0;

        return token.StartsWith(prefix, StringComparison.Ordinal) && LegacyTopLine.TryParseNumber(token[prefix.Length..], out value);
    }

    /// <summary>
    /// Reads 1 to 20 ASCII digits as an unsigned number.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="value">The number</param>
    /// <returns><c>true</c> when the text is digits whose value fits 64 bits</returns>
    private static bool TryParseInteger(string text, out ulong value)
    {
        value = 0;

        return LegacyTopLine.IsDigits(text, 1, MaxIntegerDigits) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Tells whether a character may be part of a meminfo field name.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for an ASCII letter or digit, <c>_</c>, <c>(</c> or <c>)</c></returns>
    private static bool IsMeminfoCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is '_' or '(' or ')';
    }

    /// <summary>
    /// Tells whether a character may be part of a vmstat counter name.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for an ASCII letter or digit or <c>_</c></returns>
    private static bool IsVmstatCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character == '_';
    }

    #endregion // Methods
}