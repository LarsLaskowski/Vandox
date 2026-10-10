using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SyslogTime"/>
/// </summary>
[TestClass]
public class SyslogTimeTests
{
    #region Constants

    private const string OutsideRange = "time outside the storable range";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A time with a year and an offset becomes the UTC instant.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="expected">The expected instant in UTC</param>
    [TestMethod]
    [DataRow("2026-03-01T12:00:00+01:00", "2026-03-01T11:00:00.0000000Z")]
    [DataRow("2026-03-01T12:00:00Z", "2026-03-01T12:00:00.0000000Z")]
    [DataRow("2026-03-01T23:30:00-05:30", "2026-03-02T05:00:00.0000000Z")]
    [DataRow("2028-02-29T12:00:00Z", "2028-02-29T12:00:00.0000000Z")]
    [DataRow("2026-03-01T12:00:00.1234567Z", "2026-03-01T12:00:00.1234567Z")]
    [DataRow("1677-09-21T00:12:44Z", "1677-09-21T00:12:44.0000000Z")]
    [DataRow("2262-04-11T23:47:16Z", "2262-04-11T23:47:16.0000000Z")]
    [DataRow("2262-04-12T13:47:16+14:00", "2262-04-11T23:47:16.0000000Z")]
    public void SyslogTimeTryGetInstantConvertsToUtc(string time, string expected)
    {
        // Arrange
        var parsed = SyslogLine.TryParse($"{time} web-1 sshd[1]: x");

        // Act
        var reason = parsed!.Time.TryGetInstant(out var instant);

        // Assert
        Assert.IsNull(reason, "the time is valid");
        Assert.AreEqual(expected, instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture), "instant");
        Assert.AreEqual(TimeSpan.Zero, instant.Offset, "UTC");
    }

    /// <summary>
    /// A day beyond its month is an invalid date.
    /// </summary>
    /// <param name="year">The year</param>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    [TestMethod]
    [DataRow(2026, 2, 30)]
    [DataRow(2026, 2, 29)]
    [DataRow(2100, 2, 29)]
    [DataRow(2026, 4, 31)]
    [DataRow(2026, 6, 31)]
    [DataRow(2026, 9, 31)]
    [DataRow(2026, 11, 31)]
    public void SyslogTimeTryGetInstantRefusesDayBeyondTheMonth(int year, int month, int day)
    {
        // Arrange
        var time = new SyslogTime(year, month, day, 12, 0, 0, 0, 0);

        // Act
        var reason = time.TryGetInstant(out _);

        // Assert
        Assert.AreEqual("invalid date", reason, "reason");
    }

    /// <summary>
    /// An instant outside the range storage can hold is refused without an exception.
    /// </summary>
    /// <param name="year">The year</param>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <param name="second">The second</param>
    /// <param name="offset">The offset in minutes</param>
    [TestMethod]
    [DataRow(0, 1, 1, 0, 0, 0, 0)]
    [DataRow(1, 1, 1, 0, 0, 0, 60)]
    [DataRow(1, 1, 1, 0, 0, 0, 0)]
    [DataRow(1676, 12, 31, 23, 59, 59, 0)]
    [DataRow(1677, 9, 21, 0, 12, 43, 0)]
    [DataRow(2262, 4, 11, 23, 47, 17, 0)]
    [DataRow(2263, 1, 1, 0, 0, 0, 0)]
    [DataRow(9999, 12, 31, 23, 59, 59, -60)]
    [DataRow(9999, 12, 31, 23, 59, 59, 0)]
    public void SyslogTimeTryGetInstantRefusesInstantOutsideTheStorableRange(int year, int month, int day, int hour, int minute, int second, int offset)
    {
        // Arrange
        var time = new SyslogTime(year, month, day, hour, minute, second, 0, offset);

        // Act
        var reason = time.TryGetInstant(out _);

        // Assert
        Assert.AreEqual(OutsideRange, reason, "reason");
    }

    /// <summary>
    /// A time that has a year is told apart from a traditional one by its offset.
    /// </summary>
    [TestMethod]
    public void SyslogTimeHasYearFollowsTheOffset()
    {
        // Arrange
        var traditional = new SyslogTime(0, 3, 1, 12, 0, 0, 0, null);
        var rfc3339 = new SyslogTime(2026, 3, 1, 12, 0, 0, 0, 0);

        // Act and Assert
        Assert.IsFalse(traditional.HasYear, "traditional");
        Assert.IsTrue(rfc3339.HasYear, "RFC 3339");
    }

    #endregion // Methods
}