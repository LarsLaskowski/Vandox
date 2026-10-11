using System.Text;

using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The header line of a snapshot: <c>===== Fri Oct  2 09:15:01 CEST 2026 =====</c>.
/// </summary>
/// <param name="Year">The year</param>
/// <param name="Month">The month, 1 to 12</param>
/// <param name="Day">The day of the month</param>
/// <param name="Hour">The hour</param>
/// <param name="Minute">The minute</param>
/// <param name="Second">The second</param>
/// <param name="Zone">The zone abbreviation as printed</param>
internal readonly record struct LegacyMonitorHeader(int Year, int Month, int Day, int Hour, int Minute, int Second, string Zone)
{
    #region Constants

    /// <summary>
    /// The skip reason for a zone that does not match the configured time zone.
    /// </summary>
    internal const string ZoneMismatch = "time zone does not match import.time_zone";

    private const int FirstStorableYear = 1677;
    private const int LastStorableYear = 2262;
    private const int MinZoneLength = 1;
    private const int MaxZoneLength = 16;
    private const int FixedLength = 20;
    private const int YearLength = 4;
    private const int MarginDays = 1;

    #endregion // Constants

    #region Fields

    private static readonly byte[] _marker = "===== "u8.ToArray();
    private static readonly byte[] _closing = " ====="u8.ToArray();
    private static readonly string[] _weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] _months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Tells whether a line is a header line: it begins with <c>===== </c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for a header line</returns>
    internal static bool IsHeaderLine(ReadOnlySpan<byte> line)
    {
        return line.StartsWith(_marker);
    }

    /// <summary>
    /// Reads a header line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The header; <c>null</c> when the line is no valid header</returns>
    internal static LegacyMonitorHeader? TryParse(ReadOnlySpan<byte> line)
    {
        var minimum = _marker.Length + FixedLength + MinZoneLength + 1 + YearLength + _closing.Length;
        var maximum = _marker.Length + FixedLength + MaxZoneLength + 1 + YearLength + _closing.Length;

        if (line.Length >= minimum && line.Length <= maximum && line.StartsWith(_marker) && line.EndsWith(_closing))
        {
            var middle = line[_marker.Length..^_closing.Length];

            return IsPrintableAscii(middle) ? ParseFields(Encoding.ASCII.GetString(middle)) : null;
        }

        return null;
    }

    /// <summary>
    /// Resolves the header in a time zone with the offset of the zone interval named like the header's zone.
    /// </summary>
    /// <param name="timeZone">The zone</param>
    /// <param name="instant">The instant when resolved</param>
    /// <returns><c>null</c> when resolved, else the skip reason</returns>
    internal string? Resolve(DateTimeZone timeZone, out DateTimeOffset instant)
    {
        instant = default;

        var clock = new SyslogTime(Year, Month, Day, Hour, Minute, Second, 0, 0);

        if (clock.HasValidClock() && Day <= SyslogTime.DaysInMonth(Year, Month))
        {
            return ResolveInRange(timeZone, out instant);
        }

        return SyslogTime.InvalidDate;
    }

    /// <summary>
    /// Reads the printed fields of a header: weekday, month, day, time, zone and year.
    /// </summary>
    /// <param name="text">The ASCII text between the markers</param>
    /// <returns>The header; <c>null</c> when a field is not valid</returns>
    private static LegacyMonitorHeader? ParseFields(string text)
    {
        var zone = text[FixedLength..^(YearLength + 1)];
        var day = text.Substring(8, 2);
        var layout = Array.IndexOf(_weekdays, text[..3]) >= 0
                     && Array.IndexOf(_months, text.Substring(4, 3)) >= 0
                     && text[3] == ' '
                     && text[7] == ' '
                     && text[10] == ' '
                     && text[13] == ':'
                     && text[16] == ':'
                     && text[19] == ' '
                     && text[^(YearLength + 1)] == ' ';

        if (layout
            && zone.Length is >= MinZoneLength and <= MaxZoneLength
            && zone.All(IsZoneCharacter)
            && char.IsAsciiDigit(day[1])
            && (day[0] == ' ' || char.IsAsciiDigit(day[0]))
            && LegacyTopLine.IsDigits(text.Substring(11, 2), 2, 2)
            && LegacyTopLine.IsDigits(text.Substring(14, 2), 2, 2)
            && LegacyTopLine.IsDigits(text.Substring(17, 2), 2, 2)
            && LegacyTopLine.IsDigits(text[^YearLength..], YearLength, YearLength))
        {
            return Build(text, day, zone);
        }

        return null;
    }

    /// <summary>
    /// Tells whether all bytes are printable ASCII.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <returns><c>true</c> when every byte is from space to tilde</returns>
    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        return bytes.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0;
    }

    /// <summary>
    /// Builds the header from validated text.
    /// </summary>
    /// <param name="text">The text between the markers</param>
    /// <param name="day">The two characters of the day</param>
    /// <param name="zone">The zone</param>
    /// <returns>The header</returns>
    private static LegacyMonitorHeader Build(string text, string day, string zone)
    {
        return new LegacyMonitorHeader(int.Parse(text[^YearLength..], System.Globalization.CultureInfo.InvariantCulture),
                                       Array.IndexOf(_months, text.Substring(4, 3)) + 1,
                                       int.Parse(day.Trim(' '), System.Globalization.CultureInfo.InvariantCulture),
                                       int.Parse(text.AsSpan(11, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture),
                                       int.Parse(text.AsSpan(14, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture),
                                       int.Parse(text.AsSpan(17, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture),
                                       zone);
    }

    /// <summary>
    /// Tells whether a character may be part of the zone of a header.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> for an ASCII letter or digit, <c>+</c> or <c>-</c></returns>
    private static bool IsZoneCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is '+' or '-';
    }

    /// <summary>
    /// Resolves a header whose digits are a valid date and time of day.
    /// </summary>
    /// <param name="timeZone">The zone</param>
    /// <param name="instant">The instant when resolved</param>
    /// <returns><c>null</c> when resolved, else the skip reason</returns>
    private string? ResolveInRange(DateTimeZone timeZone, out DateTimeOffset instant)
    {
        instant = default;

        if (Year < FirstStorableYear || Year > LastStorableYear)
        {
            return SyslogTime.OutsideRange;
        }

        var local = new LocalDateTime(Year, Month, Day, Hour, Minute, Second);
        var asUtc = new DateTimeOffset(local.ToDateTimeUnspecified().Ticks, TimeSpan.Zero);

        if (asUtc < StorableTime.Min.AddDays(-MarginDays) || asUtc > StorableTime.Max.AddDays(MarginDays))
        {
            return SyslogTime.OutsideRange;
        }

        var mapping = timeZone.MapLocal(local);

        if (mapping.Count == 0)
        {
            return SyslogTime.InvalidDate;
        }

        var interval = mapping.EarlyInterval.Name == Zone ? mapping.EarlyInterval : mapping.LateInterval;

        if (interval.Name != Zone)
        {
            return ZoneMismatch;
        }

        var utc = new OffsetDateTime(local, interval.WallOffset).ToInstant().ToDateTimeOffset();

        if (StorableTime.Contains(utc))
        {
            instant = utc;

            return null;
        }

        return SyslogTime.OutsideRange;
    }

    #endregion // Methods
}