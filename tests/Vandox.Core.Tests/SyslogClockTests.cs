using System.Globalization;

using NodaTime;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SyslogClock"/>
/// </summary>
[TestClass]
public class SyslogClockTests
{
    #region Constants

    private const string Outside = "time outside the storable range";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The tolerance of the repeated-hour rule is ten minutes.
    /// </summary>
    [TestMethod]
    public void SyslogClockBackwardToleranceIsTenMinutes()
    {
        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(10), SyslogClock.BackwardTolerance, "tolerance");
    }

    /// <summary>
    /// Local times are read in the zone of the server, with daylight saving time applied.
    /// </summary>
    /// <param name="zone">The zone</param>
    /// <param name="modTime">The modification time of the file</param>
    /// <param name="inputs">The year-less times, separated by a bar</param>
    /// <param name="expected">The expected instants or reasons, separated by a bar</param>
    [TestMethod]
    [DataRow("UTC", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("Europe/Berlin", "2026-07-02T00:00:00Z", "Jul 01 12:00:00", "2026-07-01T10:00:00Z")]
    [DataRow("Europe/Berlin", "2026-07-02T00:00:00Z", "Jan 15 12:00:00", "2026-01-15T11:00:00Z")]
    [DataRow("Europe/Berlin", "2026-04-01T00:00:00Z", "Mar 29 01:59:59|Mar 29 03:00:00", "2026-03-29T00:59:59Z|2026-03-29T01:00:00Z")]
    [DataRow("Europe/Berlin", "2026-04-01T00:00:00Z", "Mar 29 02:30:00", "2026-03-29T01:30:00Z")]
    [DataRow("Europe/Berlin", "2026-04-01T00:00:00Z", "Mar 29 02:00:00", "2026-03-29T01:00:00Z")]
    [DataRow("America/New_York", "2026-04-01T00:00:00Z", "Mar 08 02:30:00", "2026-03-08T07:30:00Z")]
    [DataRow("Asia/Kolkata", "2026-04-01T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T06:30:00Z")]
    public void SyslogClockResolveAppliesZoneAndDaylightSavingTime(string zone, string modTime, string inputs, string expected)
    {
        // Act
        var results = Resolve(zone, "syslog", modTime, inputs);

        // Assert
        Assert.AreEqual(expected, results, "instants in UTC");
    }

    /// <summary>
    /// A local time in the repeated hour takes the earlier offset unless that puts it more than ten minutes before the previous resolved time.
    /// </summary>
    /// <param name="inputs">The times of one day, separated by a bar</param>
    /// <param name="expected">The expected instants, separated by a bar</param>
    [TestMethod]
    [DataRow("Oct 25 01:59:59|Oct 25 03:00:00", "2026-10-24T23:59:59Z|2026-10-25T02:00:00Z")]
    [DataRow("Oct 25 02:30:00", "2026-10-25T00:30:00Z")]
    [DataRow("Oct 25 02:00:00", "2026-10-25T00:00:00Z")]
    [DataRow("Oct 25 02:59:59|Oct 25 02:00:01|Oct 25 02:30:00", "2026-10-25T00:59:59Z|2026-10-25T01:00:01Z|2026-10-25T01:30:00Z")]
    [DataRow("Oct 25 02:59:59|Oct 25 02:59:58|Oct 25 02:00:01", "2026-10-25T00:59:59Z|2026-10-25T00:59:58Z|2026-10-25T01:00:01Z")]
    [DataRow("Oct 25 02:59:59|Oct 25 02:10:00|Oct 25 02:09:58", "2026-10-25T00:59:59Z|2026-10-25T01:10:00Z|2026-10-25T01:09:58Z")]
    [DataRow("Oct 25 02:59:59|Oct 25 02:49:59", "2026-10-25T00:59:59Z|2026-10-25T00:49:59Z")]
    [DataRow("Oct 25 02:59:59|Oct 25 02:49:58", "2026-10-25T00:59:59Z|2026-10-25T01:49:58Z")]
    [DataRow("Oct 25 02:59:59|Sep 31 12:00:00|Oct 25 02:49:00", "2026-10-25T00:59:59Z|invalid date|2026-10-25T01:49:00Z")]
    [DataRow("Oct 25 02:30:00|Oct 25 02:31:00|Oct 25 02:32:00", "2026-10-25T00:30:00Z|2026-10-25T00:31:00Z|2026-10-25T00:32:00Z")]
    public void SyslogClockResolveRepeatedHourKeepsFirstPassWithinTolerance(string inputs, string expected)
    {
        // Act
        var results = Resolve("Europe/Berlin", "syslog", "2026-10-26T00:00:00Z", inputs);

        // Assert
        Assert.AreEqual(expected, results, "instants in UTC");
    }

    /// <summary>
    /// The year comes from the modification time or the date in the name, and advances when the dates run over New Year.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="modTime">The modification time, or an empty text for none</param>
    /// <param name="inputs">The year-less times, separated by a bar</param>
    /// <param name="expected">The expected instants or reasons, separated by a bar</param>
    [TestMethod]
    [DataRow("syslog", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("syslog", "2026-01-03T00:00:00Z", "Dec 30 10:00:00|Jan 02 10:00:00", "2025-12-30T10:00:00Z|2026-01-02T10:00:00Z")]
    [DataRow("syslog", "2026-03-01T12:00:00Z", "Mar 02 12:00:00", "2026-03-02T12:00:00Z")]
    [DataRow("syslog", "2026-03-01T12:00:00Z", "Mar 02 12:00:01", "2025-03-02T12:00:01Z")]
    [DataRow("syslog", "2026-03-02T00:00:00Z", "Mar 01 12:00:10|Mar 01 12:00:05", "2026-03-01T12:00:10Z|2026-03-01T12:00:05Z")]
    [DataRow("syslog", "2026-07-02T00:00:00Z", "Jul 01 00:00:00|Jan 01 00:00:00", "2026-07-01T00:00:00Z|2027-01-01T00:00:00Z")]
    [DataRow("syslog", "2026-07-02T00:00:00Z", "Jul 01 00:00:00|Mar 01 00:00:00", "2026-07-01T00:00:00Z|2026-03-01T00:00:00Z")]
    [DataRow("syslog", "2028-03-02T00:00:00Z", "Feb 29 12:00:00", "2028-02-29T12:00:00Z")]
    [DataRow("syslog", "2026-03-02T00:00:00Z", "Feb 29 12:00:00", "invalid date")]
    [DataRow("syslog-20260103", "2027-05-01T00:00:00Z", "Dec 30 10:00:00", "2025-12-30T10:00:00Z")]
    [DataRow("backup/var/log/syslog-20260103", "2027-05-01T00:00:00Z", "Dec 30 10:00:00", "2025-12-30T10:00:00Z")]
    [DataRow("syslog-20260230", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("syslog-20261301", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("syslog-20260301", "", "Mar 01 12:00:00|Mar 02 00:00:00", "2026-03-01T12:00:00Z|2026-03-02T00:00:00Z")]
    [DataRow("syslog", "", "Mar 01 12:00:00", "year unknown: the file has no usable date")]
    [DataRow("syslog", "", "Mar 01 12:00:00|Mar 02 12:00:00", "year unknown: the file has no usable date|year unknown: the file has no usable date")]
    public void SyslogClockResolveInfersTheYear(string name, string modTime, string inputs, string expected)
    {
        // Act
        var results = Resolve("UTC", name, modTime, inputs);

        // Assert
        Assert.AreEqual(expected, results, "instants in UTC or reasons");
    }

    /// <summary>
    /// An anchor outside the storable range is not used and no instant outside the range is built, and nothing throws.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="modTime">The modification time</param>
    /// <param name="inputs">The year-less times, separated by a bar</param>
    /// <param name="expected">The expected instants or reasons, separated by a bar</param>
    [TestMethod]
    [DataRow("syslog-00010101", "2026-03-02T00:00:00Z", "Dec 30 10:00:00", "2025-12-30T10:00:00Z")]
    [DataRow("syslog-99991231", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("syslog-00000101", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("syslog-16770922", "", "Sep 21 00:00:00", "time outside the storable range")]
    [DataRow("syslog-16770922", "", "Sep 21 00:13:00", "1677-09-21T00:13:00Z")]
    [DataRow("syslog-16770922", "", "Oct 01 00:00:00", "time outside the storable range")]
    [DataRow("syslog-22620410", "", "Apr 10 12:00:00", "2262-04-10T12:00:00Z")]
    [DataRow("syslog-22620412", "2026-03-02T00:00:00Z", "Mar 01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("syslog", "9000-01-01T00:00:00Z", "Mar 01 12:00:00", "year unknown: the file has no usable date")]
    [DataRow("syslog", "0001-01-01T00:00:00Z", "Mar 01 12:00:00", "year unknown: the file has no usable date")]
    [DataRow("syslog-99991231", "9000-01-01T00:00:00Z", "Mar 01 12:00:00", "year unknown: the file has no usable date")]
    public void SyslogClockResolveNeverBuildsATimeOutsideTheStorableRange(string name, string modTime, string inputs, string expected)
    {
        // Act
        var results = Resolve("UTC", name, modTime, inputs);

        // Assert
        Assert.AreEqual(expected, results, "the anchor and the instants stay inside the range");
    }

    /// <summary>
    /// The year advances at every run over New Year until it leaves the storable range, and a skipped line is still the predecessor.
    /// </summary>
    [TestMethod]
    public void SyslogClockResolveStopsAdvancingTheYearOutsideTheStorableRange()
    {
        // Arrange
        var clock = new SyslogClock(DateTimeZone.Utc, new LogFile("syslog", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero)));
        var stored = 0;
        var lastStored = default(DateTimeOffset);
        var firstSkipped = 0;
        var skipped = 0;

        // Act
        for (var line = 1; line <= 1000; line++)
        {
            var time = line % 2 == 1 ? new SyslogTime(0, 7, 1, 0, 0, 0, 0, null) : new SyslogTime(0, 1, 1, 0, 0, 0, 0, null);
            var reason = clock.Resolve(time, out var instant);

            if (reason is null)
            {
                stored++;
                lastStored = instant;
            }
            else
            {
                Assert.AreEqual(Outside, reason, $"reason of line {line}");
                firstSkipped = firstSkipped == 0 ? line : firstSkipped;
                skipped++;
            }
        }

        // Assert
        Assert.AreEqual(472, stored, "lines 1 to 472 resolve");
        Assert.AreEqual(new DateTimeOffset(2262, 1, 1, 0, 0, 0, TimeSpan.Zero), lastStored, "the last resolved line is 2262-01-01");
        Assert.AreEqual(473, firstSkipped, "line 473 is the first one outside the range");
        Assert.AreEqual(528, skipped, "lines 473 to 1000 are skipped");
    }

    /// <summary>
    /// A line that was skipped still counts as the predecessor for the year advance.
    /// </summary>
    /// <param name="inputs">The year-less times, separated by a bar</param>
    /// <param name="expected">The expected instants or reasons, separated by a bar</param>
    [TestMethod]
    [DataRow("Jul 01 00:00:00|Nov 31 12:00:00|May 01 00:00:00", "2026-07-01T00:00:00Z|invalid date|2027-05-01T00:00:00Z")]
    [DataRow("Jul 01 00:00:00|Nov 30 12:00:00|May 01 00:00:00", "2026-07-01T00:00:00Z|2026-11-30T12:00:00Z|2027-05-01T00:00:00Z")]
    [DataRow("Jul 01 00:00:00|Jun 01 00:00:00", "2026-07-01T00:00:00Z|2026-06-01T00:00:00Z")]
    public void SyslogClockResolveCountsSkippedLinesAsPredecessor(string inputs, string expected)
    {
        // Act
        var results = Resolve("UTC", "syslog", "2026-07-02T00:00:00Z", inputs);

        // Assert
        Assert.AreEqual(expected, results, "instants in UTC or reasons");
    }

    /// <summary>
    /// The first line of a file just after New Year in the zone is read in the zone's year.
    /// </summary>
    [TestMethod]
    public void SyslogClockResolveReadsNewYearInTheZoneOfTheServer()
    {
        // Act
        var results = Resolve("Europe/Berlin", "syslog", "2025-12-31T23:30:00Z", "Jan 01 00:10:00");

        // Assert
        Assert.AreEqual("2025-12-31T23:10:00Z", results, "00:10 on January 1 in Berlin");
    }

    /// <summary>
    /// A clock has an anchor when the name holds a date or the modification time is inside the storable range.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="modTime">The modification time, or an empty text for none</param>
    /// <param name="expected">Whether the clock has an anchor</param>
    [TestMethod]
    [DataRow("syslog", "2026-03-02T00:00:00Z", true)]
    [DataRow("syslog-20260301", "", true)]
    [DataRow("syslog", "", false)]
    [DataRow("syslog-20260230", "", false)]
    [DataRow("syslog-99991231", "", false)]
    [DataRow("syslog-00010101", "", false)]
    [DataRow("syslog", "9000-01-01T00:00:00Z", false)]
    [DataRow("syslog-20260301", "9000-01-01T00:00:00Z", true)]
    [DataRow("syslog-99991231", "2026-03-02T00:00:00Z", true)]
    public void SyslogClockHasAnchorTellsWhetherAYearCanBeInferred(string name, string modTime, bool expected)
    {
        // Arrange
        var clock = new SyslogClock(DateTimeZone.Utc, new LogFile(name, ModTime(modTime)));

        // Act
        var hasAnchor = clock.HasAnchor;

        // Assert
        Assert.AreEqual(expected, hasAnchor, "has anchor");
    }

    /// <summary>
    /// A local time with a year is resolved in the zone, with daylight saving time and the repeated hour applied against the previous instant.
    /// </summary>
    /// <param name="zone">The zone</param>
    /// <param name="inputs">The local times as <c>yyyy-MM-dd HH:mm:ss</c>, separated by a bar</param>
    /// <param name="expected">The expected instants or reasons, separated by a bar</param>
    [TestMethod]
    [DataRow("UTC", "2026-03-01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("Europe/Berlin", "2026-07-01 12:00:00", "2026-07-01T10:00:00Z")]
    [DataRow("Europe/Berlin", "2026-01-15 12:00:00", "2026-01-15T11:00:00Z")]
    [DataRow("Asia/Kolkata", "2026-03-01 12:00:00", "2026-03-01T06:30:00Z")]
    [DataRow("Europe/Berlin", "2026-03-29 01:59:59|2026-03-29 03:00:00", "2026-03-29T00:59:59Z|2026-03-29T01:00:00Z")]
    [DataRow("Europe/Berlin", "2026-03-29 02:30:00", "2026-03-29T01:30:00Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:30:00", "2026-10-25T00:30:00Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:00:01|2026-10-25 02:30:00", "2026-10-25T00:59:59Z|2026-10-25T01:00:01Z|2026-10-25T01:30:00Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:59:58|2026-10-25 02:00:01", "2026-10-25T00:59:59Z|2026-10-25T00:59:58Z|2026-10-25T01:00:01Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:10:00|2026-10-25 02:09:58", "2026-10-25T00:59:59Z|2026-10-25T01:10:00Z|2026-10-25T01:09:58Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:49:59", "2026-10-25T00:59:59Z|2026-10-25T00:49:59Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:49:58", "2026-10-25T00:59:59Z|2026-10-25T01:49:58Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-09-31 12:00:00|2026-10-25 02:49:00", "2026-10-25T00:59:59Z|invalid date|2026-10-25T01:49:00Z")]
    public void SyslogClockResolveLocalAppliesZoneDaylightSavingTimeAndThePreviousInstant(string zone, string inputs, string expected)
    {
        // Act
        var results = ResolveLocal(zone, inputs);

        // Assert
        Assert.AreEqual(expected, results, "instants in UTC or reasons");
    }

    /// <summary>
    /// A time that cannot be stored gives a reason and no instant, and nothing throws.
    /// </summary>
    /// <param name="input">The local time as <c>yyyy-MM-dd HH:mm:ss</c></param>
    /// <param name="expected">The expected reason, or an empty text for a stored time</param>
    [TestMethod]
    [DataRow("2026-13-01 12:00:00", "invalid date")]
    [DataRow("2026-00-10 12:00:00", "invalid date")]
    [DataRow("2026-01-32 12:00:00", "invalid date")]
    [DataRow("2026-01-00 12:00:00", "invalid date")]
    [DataRow("2026-01-10 24:00:00", "invalid date")]
    [DataRow("2026-01-10 12:60:00", "invalid date")]
    [DataRow("2026-01-10 12:00:60", "invalid date")]
    [DataRow("2026-02-29 12:00:00", "invalid date")]
    [DataRow("2026-04-31 12:00:00", "invalid date")]
    [DataRow("2028-02-29 12:00:00", "")]
    [DataRow("0000-01-01 00:00:00", Outside)]
    [DataRow("1676-12-31 23:59:59", Outside)]
    [DataRow("1677-09-21 00:12:43", Outside)]
    [DataRow("2262-04-11 23:47:17", Outside)]
    [DataRow("2263-01-01 00:00:00", Outside)]
    [DataRow("9999-12-31 23:59:59", Outside)]
    [DataRow("1677-09-21 00:12:44", "")]
    [DataRow("2262-04-11 23:47:16", "")]
    public void SyslogClockResolveLocalGivesTheReasonOfATimeThatCannotBeStored(string input, string expected)
    {
        // Arrange
        var time = ParseLocal(input);

        // Act
        var reason = SyslogClock.ResolveLocal(DateTimeZone.Utc, time, null, out var instant);

        // Assert
        Assert.AreEqual(expected.Length == 0 ? null : expected, reason, "reason");
        Assert.AreEqual(expected.Length == 0, instant != default, "the instant is set on success only");
    }

    /// <summary>
    /// An offset in the time is ignored: only the year and the clock digits count.
    /// </summary>
    [TestMethod]
    public void SyslogClockResolveLocalIgnoresTheOffsetOfTheTime()
    {
        // Arrange
        var time = new SyslogTime(2026, 7, 1, 12, 0, 0, 0, 600);

        // Act
        var reason = SyslogClock.ResolveLocal(DateTimeZoneProviders.Tzdb["Europe/Berlin"], time, null, out var instant);

        // Assert
        Assert.IsNull(reason, "reason");
        Assert.AreEqual(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), instant, "the zone decides the offset");
    }

    /// <summary>
    /// Values far outside any range never throw, whatever combination they come in.
    /// </summary>
    /// <param name="year">The year</param>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <param name="hour">The hour</param>
    [TestMethod]
    [DataRow(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [DataRow(int.MinValue, int.MinValue, int.MinValue, int.MinValue)]
    [DataRow(-1, 0, 0, -1)]
    [DataRow(2026, 99, 1, 1)]
    [DataRow(2026, 1, 99, 1)]
    [DataRow(2026, 1, 1, 99)]
    public void SyslogClockResolveLocalNeverThrows(int year, int month, int day, int hour)
    {
        // Arrange
        var time = new SyslogTime(year, month, day, hour, 0, 0, 0, null);

        // Act
        var reason = SyslogClock.ResolveLocal(DateTimeZoneProviders.Tzdb["Europe/Berlin"], time, null, out var instant);

        // Assert
        Assert.IsNotNull(reason, "a reason");
        Assert.AreEqual(default, instant, "no instant");
    }

    /// <summary>
    /// Resolves a series of local times with a year, each against the instant of the last one that was resolved.
    /// </summary>
    /// <param name="zone">The IANA name of the zone</param>
    /// <param name="inputs">The local times as <c>yyyy-MM-dd HH:mm:ss</c>, separated by a bar</param>
    /// <returns>The instants in UTC (to the second) or the reasons, separated by a bar</returns>
    private static string ResolveLocal(string zone, string inputs)
    {
        var timeZone = DateTimeZoneProviders.Tzdb[zone];
        var results = new List<string>();
        DateTimeOffset? previous = null;

        foreach (var input in inputs.Split('|'))
        {
            var reason = SyslogClock.ResolveLocal(timeZone, ParseLocal(input), previous, out var instant);

            if (reason is null)
            {
                previous = instant;
            }

            results.Add(reason ?? instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        return string.Join('|', results);
    }

    /// <summary>
    /// Reads a local time such as <c>2026-10-25 02:59:59</c> without checking its ranges.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The time, with a year and without an offset</returns>
    private static SyslogTime ParseLocal(string text)
    {
        var parts = text.Split(' ', ':', '-');

        return new SyslogTime(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture), int.Parse(parts[4], CultureInfo.InvariantCulture), int.Parse(parts[5], CultureInfo.InvariantCulture), 0, null);
    }

    /// <summary>
    /// Resolves a series of year-less times and describes the results.
    /// </summary>
    /// <param name="zone">The IANA name of the zone</param>
    /// <param name="name">The file name</param>
    /// <param name="modTime">The modification time, or an empty text for none</param>
    /// <param name="inputs">The year-less times as <c>Mmm dd HH:MM:SS</c>, separated by a bar</param>
    /// <returns>The instants in UTC (to the second) or the reasons, separated by a bar</returns>
    private static string Resolve(string zone, string name, string modTime, string inputs)
    {
        var clock = new SyslogClock(DateTimeZoneProviders.Tzdb[zone], new LogFile(name, ModTime(modTime)));
        var results = new List<string>();

        foreach (var input in inputs.Split('|'))
        {
            var reason = clock.Resolve(ParseTime(input), out var instant);

            results.Add(reason ?? instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        return string.Join('|', results);
    }

    /// <summary>
    /// Reads a modification time.
    /// </summary>
    /// <param name="text">The time as ISO 8601, or an empty text</param>
    /// <returns>The time, or <c>null</c></returns>
    private static DateTimeOffset? ModTime(string text)
    {
        return text.Length == 0 ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    /// <summary>
    /// Reads a traditional time such as <c>Oct 25 02:59:59</c>.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The time</returns>
    private static SyslogTime ParseTime(string text)
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        var parts = text.Split(' ');
        var clock = parts[2].Split(':');

        return new SyslogTime(0, Array.IndexOf(months, parts[0]) + 1, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(clock[0], CultureInfo.InvariantCulture), int.Parse(clock[1], CultureInfo.InvariantCulture), int.Parse(clock[2], CultureInfo.InvariantCulture), 0, null);
    }

    #endregion // Methods
}