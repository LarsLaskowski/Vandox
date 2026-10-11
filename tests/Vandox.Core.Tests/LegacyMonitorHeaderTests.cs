using System.Text;

using NodaTime;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyMonitorHeader"/>
/// </summary>
[TestClass]
public class LegacyMonitorHeaderTests
{
    #region Constants

    private const string Invalid = "invalid date";
    private const string Outside = "time outside the storable range";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeZone _berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The output of date in the header is read into its fields.
    /// </summary>
    /// <param name="line">The header line</param>
    /// <param name="year">The expected year</param>
    /// <param name="month">The expected month</param>
    /// <param name="day">The expected day</param>
    /// <param name="hour">The expected hour</param>
    /// <param name="minute">The expected minute</param>
    /// <param name="second">The expected second</param>
    /// <param name="zone">The expected zone</param>
    [TestMethod]
    [DataRow("===== Fri Oct  2 09:15:01 CEST 2026 =====", 2026, 10, 2, 9, 15, 1, "CEST")]
    [DataRow("===== Sun Oct 25 02:30:00 CET 2026 =====", 2026, 10, 25, 2, 30, 0, "CET")]
    [DataRow("===== Mon Jan 05 00:00:00 UTC 2026 =====", 2026, 1, 5, 0, 0, 0, "UTC")]
    [DataRow("===== Wed Jul  1 23:59:59 +03 2026 =====", 2026, 7, 1, 23, 59, 59, "+03")]
    [DataRow("===== Sat Dec 31 12:00:00 -0330 2026 =====", 2026, 12, 31, 12, 0, 0, "-0330")]
    [DataRow("===== Tue Feb 10 08:09:10 ABCDEFGHIJKLMNOP 2026 =====", 2026, 2, 10, 8, 9, 10, "ABCDEFGHIJKLMNOP")]
    public void LegacyMonitorHeaderTryParseReadsDateOutput(string line, int year, int month, int day, int hour, int minute, int second, string zone)
    {
        // Act
        var header = LegacyMonitorHeader.TryParse(Encoding.UTF8.GetBytes(line));

        // Assert
        Assert.IsNotNull(header, "a header");
        Assert.AreEqual(new LegacyMonitorHeader(year, month, day, hour, minute, second, zone), header.Value, "the fields of the header");
    }

    /// <summary>
    /// Text that is not exactly the output of date between the markers is no header.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("===== Fri oct  2 09:15:01 CEST 2026 =====")]
    [DataRow("===== Fri Oct 2 09:15:01 CEST 2026 =====")]
    [DataRow("===== Fri Oct 123 09:15:01 CEST 2026 =====")]
    [DataRow("===== Fri Oct  2 9:15:01 CEST 2026 =====")]
    [DataRow("===== Fri Oct  2 09:15:01 CEST =====")]
    [DataRow("===== Fri Oct  2 09:15:01 ABCDEFGHIJKLMNOPQ 2026 =====")]
    [DataRow("===== Fri Oct  2 09:15:01 CE_ST 2026 =====")]
    [DataRow("===== Fri Oct  2 09:15:01 CEST 2026 ===== ")]
    [DataRow("===== Fri Oct  2 09:15:01 CEST 2026")]
    [DataRow("===== Fri Oct  2 09:15:01 CEST 2026 ====")]
    [DataRow("===== Fr 2. Okt 09:15:01 CEST 2026 =====")]
    [DataRow("===== Fri Oct  2 09:15:01 CEST ２０２６ =====")]
    [DataRow("===== Fri Oct  2 09:15:01 CEST 26 =====")]
    [DataRow("===== Fri Oct  2 09:15:01  2026 =====")]
    [DataRow("===== Fri Oct  2 09:15 CEST 2026 =====")]
    [DataRow("===== fri Oct  2 09:15:01 CEST 2026 =====")]
    [DataRow("===== Xyz Oct  2 09:15:01 CEST 2026 =====")]
    [DataRow(" ===== Fri Oct  2 09:15:01 CEST 2026 =====")]
    [DataRow("====== Fri Oct  2 09:15:01 CEST 2026 =====")]
    [DataRow("=====Fri Oct  2 09:15:01 CEST 2026 =====")]
    [DataRow("")]
    public void LegacyMonitorHeaderTryParseRefusesOtherText(string line)
    {
        // Act
        var header = LegacyMonitorHeader.TryParse(Encoding.UTF8.GetBytes(line));

        // Assert
        Assert.IsNull(header, "no header");
    }

    /// <summary>
    /// A header line is any line that begins with the marker and a space, whatever follows.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">Whether the line is a header line</param>
    [TestMethod]
    [DataRow("===== Fri Oct  2 09:15:01 CEST 2026 =====", true)]
    [DataRow("===== anything at all", true)]
    [DataRow("===== ", true)]
    [DataRow("=====", false)]
    [DataRow("=====x", false)]
    [DataRow(" ===== x", false)]
    [DataRow("----- lsof -----", false)]
    [DataRow("top - 09:15:01", false)]
    [DataRow("", false)]
    public void LegacyMonitorHeaderIsHeaderLineMatchesPrefixOnly(string line, bool expected)
    {
        // Act
        var actual = LegacyMonitorHeader.IsHeaderLine(Encoding.UTF8.GetBytes(line));

        // Assert
        Assert.AreEqual(expected, actual, "whether the line begins like a header");
    }

    /// <summary>
    /// A header resolves with the offset of the zone interval whose name it carries, also in the repeated hour and next to the skipped hour.
    /// </summary>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <param name="zone">The zone name of the header</param>
    /// <param name="expected">The expected instant in UTC</param>
    [TestMethod]
    [DataRow(10, 25, 2, 30, "CEST", "2026-10-25T00:30:00Z")]
    [DataRow(10, 25, 2, 30, "CET", "2026-10-25T01:30:00Z")]
    [DataRow(3, 29, 1, 59, "CET", "2026-03-29T00:59:00Z")]
    [DataRow(3, 29, 3, 0, "CEST", "2026-03-29T01:00:00Z")]
    [DataRow(10, 2, 9, 15, "CEST", "2026-10-02T07:15:00Z")]
    [DataRow(1, 15, 9, 15, "CET", "2026-01-15T08:15:00Z")]
    public void LegacyMonitorHeaderResolveUsesOffsetOfNamedInterval(int month, int day, int hour, int minute, string zone, string expected)
    {
        // Arrange
        var header = new LegacyMonitorHeader(2026, month, day, hour, minute, 0, zone);

        // Act
        var reason = header.Resolve(_berlin, out var instant);

        // Assert
        Assert.IsNull(reason, "no reason");
        Assert.AreEqual(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), instant, "the instant");
        Assert.AreEqual(TimeSpan.Zero, instant.Offset, "the instant is in UTC");
    }

    /// <summary>
    /// A date that does not exist, or a local time in the skipped hour, is an invalid date.
    /// </summary>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <param name="second">The second</param>
    /// <param name="zone">The zone name</param>
    [TestMethod]
    [DataRow(2, 30, 9, 15, 0, "CET")]
    [DataRow(2, 29, 9, 15, 0, "CET")]
    [DataRow(4, 31, 9, 15, 0, "CEST")]
    [DataRow(10, 2, 24, 0, 0, "CEST")]
    [DataRow(10, 2, 9, 60, 0, "CEST")]
    [DataRow(10, 2, 9, 15, 60, "CEST")]
    [DataRow(13, 2, 9, 15, 0, "CEST")]
    [DataRow(0, 2, 9, 15, 0, "CEST")]
    [DataRow(10, 0, 9, 15, 0, "CEST")]
    [DataRow(10, 32, 9, 15, 0, "CEST")]
    [DataRow(3, 29, 2, 30, 0, "CET")]
    [DataRow(3, 29, 2, 30, 0, "CEST")]
    public void LegacyMonitorHeaderResolveRefusesInvalidDates(int month, int day, int hour, int minute, int second, string zone)
    {
        // Arrange
        var header = new LegacyMonitorHeader(2026, month, day, hour, minute, second, zone);

        // Act
        var reason = header.Resolve(_berlin, out _);

        // Assert
        Assert.AreEqual(Invalid, reason, "the reason");
    }

    /// <summary>
    /// A year outside the storable range, or an instant outside it, is refused without an exception.
    /// </summary>
    /// <param name="year">The year</param>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <param name="zoneId">The time zone ID</param>
    /// <param name="zone">The zone name of the header</param>
    [TestMethod]
    [DataRow(1, 1, 1, "Europe/Berlin", "CET")]
    [DataRow(1677, 1, 1, "Europe/Berlin", "CET")]
    [DataRow(9999, 12, 31, "America/Los_Angeles", "PST")]
    [DataRow(9999, 12, 31, "Europe/Berlin", "CET")]
    [DataRow(2263, 1, 1, "Europe/Berlin", "CET")]
    [DataRow(2262, 12, 31, "Pacific/Honolulu", "HST")]
    [DataRow(0, 1, 1, "Etc/UTC", "UTC")]
    [DataRow(2147483647, 12, 31, "Etc/UTC", "UTC")]
    public void LegacyMonitorHeaderResolveRefusesOutsideStorableRange(int year, int month, int day, string zoneId, string zone)
    {
        // Arrange
        var header = new LegacyMonitorHeader(year, month, day, 23, 59, 59, zone);

        // Act
        var reason = header.Resolve(DateTimeZoneProviders.Tzdb[zoneId], out _);

        // Assert
        Assert.AreEqual(Outside, reason, "the reason");
    }

    /// <summary>
    /// A zone name that no interval at that local time carries is a mismatch, also when it is the name of a zone interval of another season.
    /// </summary>
    /// <param name="month">The month</param>
    /// <param name="zone">The zone name</param>
    [TestMethod]
    [DataRow(1, "CEST")]
    [DataRow(7, "CET")]
    [DataRow(7, "EST")]
    [DataRow(1, "UTC")]
    [DataRow(1, "cet")]
    [DataRow(1, "+01")]
    [DataRow(10, "CEST ")]
    public void LegacyMonitorHeaderResolveReportsZoneMismatch(int month, string zone)
    {
        // Arrange
        var header = new LegacyMonitorHeader(2026, month, 15, 9, 15, 0, zone);

        // Act
        var reason = header.Resolve(_berlin, out _);

        // Assert
        Assert.AreEqual("time zone does not match import.time_zone", reason, "the fixed reason");
    }

    /// <summary>
    /// A zone whose intervals are named by their offset resolves a header that carries that name.
    /// </summary>
    /// <param name="zoneId">The time zone ID</param>
    /// <param name="zone">The zone name of the header</param>
    /// <param name="expected">The expected instant in UTC</param>
    [TestMethod]
    [DataRow("Etc/UTC", "UTC", "2026-10-02T09:15:00Z")]
    [DataRow("Asia/Istanbul", "+03", "2026-10-02T06:15:00Z")]
    public void LegacyMonitorHeaderResolveReadsZonesNamedByOffsetOrUtc(string zoneId, string zone, string expected)
    {
        // Arrange
        var header = new LegacyMonitorHeader(2026, 10, 2, 9, 15, 0, zone);

        // Act
        var reason = header.Resolve(DateTimeZoneProviders.Tzdb[zoneId], out var instant);

        // Assert
        Assert.IsNull(reason, "no reason");
        Assert.AreEqual(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), instant, "the instant");
    }

    #endregion // Methods
}