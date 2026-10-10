using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SyslogLine"/>
/// </summary>
[TestClass]
public class SyslogLineTests
{
    #region Methods

    /// <summary>
    /// Real lines in both time formats give host, program, process ID, priority and message.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="priority">The expected priority, or -1 for none</param>
    /// <param name="host">The expected host</param>
    /// <param name="program">The expected program</param>
    /// <param name="pid">The expected process ID</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1234]: Accepted publickey for root from 192.0.2.7 port 51234 ssh2", -1, "web-1", "sshd", 1234, "Accepted publickey for root from 192.0.2.7 port 51234 ssh2")]
    [DataRow("Mar 21 06:25:01 web-1 CRON[3321]: (root) CMD (command -v debian-sa1 > /dev/null && debian-sa1 1 1)", -1, "web-1", "CRON", 3321, "(root) CMD (command -v debian-sa1 > /dev/null && debian-sa1 1 1)")]
    [DataRow("Mar  1 12:00:05 web-1 kernel: [123456.789012] CPU: 1 PID: 4242 Comm: mariadbd", -1, "web-1", "kernel", 0, "[123456.789012] CPU: 1 PID: 4242 Comm: mariadbd")]
    [DataRow("Mar  1 12:00:09 web-1 postfix/smtpd[2210]: connect from unknown[192.0.2.9]", -1, "web-1", "postfix/smtpd", 2210, "connect from unknown[192.0.2.9]")]
    [DataRow("Mar  1 12:00:10 web-1 systemd[1]: Started Daily apt upgrade and clean activities.", -1, "web-1", "systemd", 1, "Started Daily apt upgrade and clean activities.")]
    [DataRow("Mar 01 12:00:10 web-1 systemd[1]: zero-padded day", -1, "web-1", "systemd", 1, "zero-padded day")]
    [DataRow("Mar  1 12:00:10 web-1 ntpd: tag without a process ID", -1, "web-1", "ntpd", 0, "tag without a process ID")]
    [DataRow("Mar  1 12:00:11 web-1 last message repeated 3 times", -1, "web-1", "", 0, "last message repeated 3 times")]
    [DataRow("2026-03-01T12:00:00.123456+01:00 web-1 sshd[1234]: Failed password for invalid user admin", -1, "web-1", "sshd", 1234, "Failed password for invalid user admin")]
    [DataRow("2026-03-01T12:00:00Z web-1 kernel: [1.5] Out of memory: Killed process 4242 (mariadbd)", -1, "web-1", "kernel", 0, "[1.5] Out of memory: Killed process 4242 (mariadbd)")]
    [DataRow("<13>Mar  1 12:00:00 web-1 sshd[1]: with priority", 5, "web-1", "sshd", 1, "with priority")]
    [DataRow("<0>Mar  1 12:00:00 web-1 kernel: emergency", 0, "web-1", "kernel", 0, "emergency")]
    [DataRow("<191>2026-03-01T12:00:00+01:00 web-1 sshd[1]: highest", 7, "web-1", "sshd", 1, "highest")]
    [DataRow("<30>2026-03-01T12:00:00+01:00 web-1 systemd[1]: daemon info", 6, "web-1", "systemd", 1, "daemon info")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[2147483647]: largest process ID", -1, "web-1", "sshd", 2147483647, "largest process ID")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[2147483648]: process ID above the range", -1, "web-1", "sshd", 0, "process ID above the range")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[9999999999]: ten digits above the range", -1, "web-1", "sshd", 0, "ten digits above the range")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1]:  two spaces keep one", -1, "web-1", "sshd", 1, " two spaces keep one")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1]:no space", -1, "web-1", "sshd", 1, "no space")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1]:", -1, "web-1", "sshd", 1, "")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1]: ", -1, "web-1", "sshd", 1, "")]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1]: tab\tand ünïcode and \u0001 control", -1, "web-1", "sshd", 1, "tab\tand ünïcode and \u0001 control")]
    public void SyslogLineTryParseReadsFields(string line, int priority, string host, string program, int pid, string message)
    {
        // Act
        var parsed = SyslogLine.TryParse(line);

        // Assert
        Assert.IsNotNull(parsed, "a syslog line");
        Assert.AreEqual(priority < 0 ? null : (byte)priority, parsed.Priority, "priority");
        Assert.AreEqual(host, parsed.Host, "host");
        Assert.AreEqual(program, parsed.Program, "program");
        Assert.AreEqual(pid, parsed.Pid, "process ID");
        Assert.AreEqual(message, parsed.Message, "message");
    }

    /// <summary>
    /// The traditional time stamp is kept as digits without a year or an offset.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="month">The expected month</param>
    /// <param name="day">The expected day</param>
    /// <param name="hour">The expected hour</param>
    /// <param name="minute">The expected minute</param>
    /// <param name="second">The expected second</param>
    [TestMethod]
    [DataRow("Jan  1 00:00:00 h a: b", 1, 1, 0, 0, 0)]
    [DataRow("Feb 29 12:34:56 h a: b", 2, 29, 12, 34, 56)]
    [DataRow("Feb 31 12:34:56 h a: b", 2, 31, 12, 34, 56)]
    [DataRow("Mar  9 06:07:08 h a: b", 3, 9, 6, 7, 8)]
    [DataRow("Apr 10 23:59:59 h a: b", 4, 10, 23, 59, 59)]
    [DataRow("May 31 01:02:03 h a: b", 5, 31, 1, 2, 3)]
    [DataRow("Jun 30 10:11:12 h a: b", 6, 30, 10, 11, 12)]
    [DataRow("Jul 01 10:11:12 h a: b", 7, 1, 10, 11, 12)]
    [DataRow("Aug  2 10:11:12 h a: b", 8, 2, 10, 11, 12)]
    [DataRow("Sep  3 10:11:12 h a: b", 9, 3, 10, 11, 12)]
    [DataRow("Oct  4 10:11:12 h a: b", 10, 4, 10, 11, 12)]
    [DataRow("Nov  5 10:11:12 h a: b", 11, 5, 10, 11, 12)]
    [DataRow("Dec 25 10:11:12 h a: b", 12, 25, 10, 11, 12)]
    public void SyslogLineTryParseReadsTraditionalTimeStamp(string line, int month, int day, int hour, int minute, int second)
    {
        // Act
        var parsed = SyslogLine.TryParse(line);

        // Assert
        Assert.IsNotNull(parsed, "a syslog line");
        Assert.IsFalse(parsed.Time.HasYear, "no year in the traditional form");
        Assert.AreEqual(0, parsed.Time.Year, "year");
        Assert.AreEqual(month, parsed.Time.Month, "month");
        Assert.AreEqual(day, parsed.Time.Day, "day");
        Assert.AreEqual(hour, parsed.Time.Hour, "hour");
        Assert.AreEqual(minute, parsed.Time.Minute, "minute");
        Assert.AreEqual(second, parsed.Time.Second, "second");
        Assert.AreEqual(0, parsed.Time.FractionTicks, "fraction");
        Assert.IsNull(parsed.Time.OffsetMinutes, "offset");
    }

    /// <summary>
    /// The RFC 3339 time stamp is kept as digits with the fraction cut to 100 ns and the offset in minutes.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="year">The expected year</param>
    /// <param name="month">The expected month</param>
    /// <param name="day">The expected day</param>
    /// <param name="clock">The expected time of day</param>
    /// <param name="ticks">The expected fraction in ticks of 100 ns</param>
    /// <param name="offset">The expected offset in minutes</param>
    [TestMethod]
    [DataRow("2026-03-01T12:00:00.123456+01:00 h a: b", 2026, 3, 1, "12:00:00", 1234560, 60)]
    [DataRow("2026-03-01T12:00:00Z h a: b", 2026, 3, 1, "12:00:00", 0, 0)]
    [DataRow("2026-03-01T12:00:00.5Z h a: b", 2026, 3, 1, "12:00:00", 5000000, 0)]
    [DataRow("2026-12-31T23:59:59.999999999-05:30 h a: b", 2026, 12, 31, "23:59:59", 9999999, -330)]
    [DataRow("2026-12-31T23:59:59.123456789Z h a: b", 2026, 12, 31, "23:59:59", 1234567, 0)]
    [DataRow("2026-03-01T00:00:00.0000001+14:00 h a: b", 2026, 3, 1, "00:00:00", 1, 840)]
    [DataRow("2026-03-01T00:00:00-14:00 h a: b", 2026, 3, 1, "00:00:00", 0, -840)]
    [DataRow("0001-01-01T00:00:00Z h a: b", 1, 1, 1, "00:00:00", 0, 0)]
    [DataRow("9999-12-31T23:59:59Z h a: b", 9999, 12, 31, "23:59:59", 0, 0)]
    public void SyslogLineTryParseReadsRfc3339TimeStamp(string line, int year, int month, int day, string clock, int ticks, int offset)
    {
        // Act
        var parsed = SyslogLine.TryParse(line);

        // Assert
        Assert.IsNotNull(parsed, "a syslog line");
        Assert.IsTrue(parsed.Time.HasYear, "the RFC 3339 form carries a year");
        Assert.AreEqual(year, parsed.Time.Year, "year");
        Assert.AreEqual(month, parsed.Time.Month, "month");
        Assert.AreEqual(day, parsed.Time.Day, "day");
        Assert.AreEqual(clock, $"{parsed.Time.Hour:00}:{parsed.Time.Minute:00}:{parsed.Time.Second:00}", "time of day");
        Assert.AreEqual(ticks, parsed.Time.FractionTicks, "fraction in ticks");
        Assert.AreEqual(offset, parsed.Time.OffsetMinutes, "offset in minutes");
    }

    /// <summary>
    /// Text that has no valid header is not a syslog line.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("garbage")]
    [DataRow("Foo  1 12:00:00 web-1 a: b")]
    [DataRow("mar  1 12:00:00 web-1 a: b")]
    [DataRow("MAR  1 12:00:00 web-1 a: b")]
    [DataRow("Mar  1 24:00:00 web-1 a: b")]
    [DataRow("Mar  1 12:60:00 web-1 a: b")]
    [DataRow("Mar  1 12:00:60 web-1 a: b")]
    [DataRow("Mar 1 12:00:00 web-1 a: b")]
    [DataRow("Mar 32 12:00:00 web-1 a: b")]
    [DataRow("Mar 00 12:00:00 web-1 a: b")]
    [DataRow("Mar  0 12:00:00 web-1 a: b")]
    [DataRow("Mar  1 12:00:00web-1 a: b")]
    [DataRow("Mar  1 12:00:00  web-1 a: b")]
    [DataRow("Mar  1 12:00:00")]
    [DataRow("Mar  1 12:00:00 ")]
    [DataRow("Mar  1 2:00:00 web-1 a: b")]
    [DataRow("<192>Mar  1 12:00:00 web-1 a: b")]
    [DataRow("<>Mar  1 12:00:00 web-1 a: b")]
    [DataRow("<1234>Mar  1 12:00:00 web-1 a: b")]
    [DataRow("<-1>Mar  1 12:00:00 web-1 a: b")]
    [DataRow("<13Mar  1 12:00:00 web-1 a: b")]
    [DataRow("<a>Mar  1 12:00:00 web-1 a: b")]
    [DataRow("2026-03-01t12:00:00Z web-1 a: b")]
    [DataRow("2026-03-01T12:00:00z web-1 a: b")]
    [DataRow("2026-03-01T12:00:00+14:01 web-1 a: b")]
    [DataRow("2026-03-01T12:00:00-14:01 web-1 a: b")]
    [DataRow("2026-03-01T12:00:00+15:00 web-1 a: b")]
    [DataRow("2026-03-01T12:00:00+0100 web-1 a: b")]
    [DataRow("2026-03-01T25:00:00Z web-1 a: b")]
    [DataRow("2026-03-01T12:00:00 web-1 a: b")]
    [DataRow("2026-03-01 12:00:00Z web-1 a: b")]
    [DataRow("2026-03-01T12:00:00.Z web-1 a: b")]
    [DataRow("2026-03-01T12:00:00.1234567890Z web-1 a: b")]
    [DataRow("2026-03-01T12:00:00Zweb-1 a: b")]
    [DataRow("26-03-01T12:00:00Z web-1 a: b")]
    [DataRow("<34>1 2026-03-01T12:00:00Z web-1 su - - - msg")]
    [DataRow("2026-03-01 12:00:00 0 [Note] InnoDB: Buffer pool(s) load completed")]
    [DataRow("__REALTIME_TIMESTAMP=1772368215123456")]
    public void SyslogLineTryParseReturnsNullForOtherText(string line)
    {
        // Act
        var parsed = SyslogLine.TryParse(line);

        // Assert
        Assert.IsNull(parsed, "not a syslog line");
    }

    /// <summary>
    /// The host is limited to 255 UTF-8 bytes of the decoded line and is never cut.
    /// </summary>
    /// <param name="unit">The repeated character</param>
    /// <param name="count">How often it is repeated</param>
    /// <param name="accepted">Whether the line is accepted</param>
    [TestMethod]
    [DataRow("a", 255, true)]
    [DataRow("a", 256, false)]
    [DataRow("�", 85, true)]
    [DataRow("�", 86, false)]
    [DataRow("�", 255, false)]
    [DataRow("é", 127, true)]
    [DataRow("é", 128, false)]
    public void SyslogLineTryParseLimitsHostToDecodedBytes(string unit, int count, bool accepted)
    {
        // Arrange
        var host = string.Concat(Enumerable.Repeat(unit, count));

        // Act
        var parsed = SyslogLine.TryParse($"Mar  1 12:00:00 {host} sshd[1]: message");

        // Assert
        Assert.AreEqual(accepted, parsed is not null, "the line is accepted only within the limit");

        if (accepted)
        {
            Assert.AreEqual(host, parsed!.Host, "the host is kept whole");
        }
    }

    /// <summary>
    /// The program of a tag is limited to 128 UTF-8 bytes of the decoded line; beyond it the line has no tag.
    /// </summary>
    /// <param name="unit">The repeated character</param>
    /// <param name="count">How often it is repeated</param>
    /// <param name="suffix">Text that follows the repeated characters in the program</param>
    /// <param name="tagged">Whether the tag is recognized</param>
    [TestMethod]
    [DataRow("a", 128, "", true)]
    [DataRow("a", 129, "", false)]
    [DataRow("�", 42, "ab", true)]
    [DataRow("�", 43, "", false)]
    [DataRow("�", 128, "", false)]
    public void SyslogLineTryParseLimitsProgramToDecodedBytes(string unit, int count, string suffix, bool tagged)
    {
        // Arrange
        var program = string.Concat(Enumerable.Repeat(unit, count)) + suffix;

        // Act
        var parsed = SyslogLine.TryParse($"Mar  1 12:00:00 web-1 {program}[7]: text");

        // Assert
        Assert.IsNotNull(parsed, "still a syslog line");
        Assert.AreEqual("web-1", parsed.Host, "host");

        if (tagged)
        {
            Assert.AreEqual(program, parsed.Program, "the program is kept whole");
            Assert.AreEqual(7, parsed.Pid, "process ID");
            Assert.AreEqual("text", parsed.Message, "message");
        }
        else
        {
            Assert.AreEqual(string.Empty, parsed.Program, "no tag, no program");
            Assert.AreEqual(0, parsed.Pid, "no tag, no process ID");
            Assert.AreEqual($"{program}[7]: text", parsed.Message, "the message is the rest after the host");
        }
    }

    #endregion // Methods
}