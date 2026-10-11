namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

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

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Tells whether a line is a header line: it begins with <c>===== </c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for a header line</returns>
    internal static bool IsHeaderLine(ReadOnlySpan<byte> line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a header line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The header; <c>null</c> when the line is no valid header</returns>
    internal static LegacyMonitorHeader? TryParse(ReadOnlySpan<byte> line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Resolves the header in a time zone with the offset of the zone interval named like the header's zone.
    /// </summary>
    /// <param name="timeZone">The zone</param>
    /// <param name="instant">The instant when resolved</param>
    /// <returns><c>null</c> when resolved, else the skip reason</returns>
    internal string? Resolve(NodaTime.DateTimeZone timeZone, out DateTimeOffset instant)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}