using System.Text;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MariaDbLine"/>
/// </summary>
[TestClass]
public class MariaDbLineTests
{
    #region Methods

    /// <summary>
    /// Every header form is read into its time, level, program and message.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="year">The expected year</param>
    /// <param name="month">The expected month</param>
    /// <param name="day">The expected day</param>
    /// <param name="hour">The expected hour</param>
    /// <param name="minute">The expected minute</param>
    /// <param name="second">The expected second</param>
    /// <param name="level">The expected level</param>
    /// <param name="program">The expected program</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed at 260301 12:30:15", 2026, 3, 1, 12, 30, 15, "Note", "", "InnoDB: Buffer pool(s) load completed at 260301 12:30:15")]
    [DataRow("2026-03-02  3:12:40 0 [Note] Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 3456", 2026, 3, 2, 3, 12, 40, "Note", "", "Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 3456")]
    [DataRow("2026-03-02 03:12:40 0 [Note] x", 2026, 3, 2, 3, 12, 40, "Note", "", "x")]
    [DataRow("2026-03-02  3:15:02 18446744073709551615 [Warning] Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)", 2026, 3, 2, 3, 15, 2, "Warning", "", "Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)")]
    [DataRow("2026-03-01 12:00:00 12 [ERROR] Master 'backup': Slave I/O: error connecting to master", 2026, 3, 1, 12, 0, 0, "ERROR", "", "Master 'backup': Slave I/O: error connecting to master")]
    [DataRow("2026-03-01 12:00:00 0 [Note]", 2026, 3, 1, 12, 0, 0, "Note", "", "")]
    [DataRow("2026-03-01 12:00:00 0 [Note] ", 2026, 3, 1, 12, 0, 0, "Note", "", "")]
    [DataRow("2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836", 2026, 3, 2, 10, 10, 10, "", "", "InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836")]
    [DataRow("2026-03-02 10:10:10 0x7f3a2c1fe640 INNODB MONITOR OUTPUT", 2026, 3, 2, 10, 10, 10, "", "", "INNODB MONITOR OUTPUT")]
    [DataRow("260302 10:10:10 [ERROR] mysqld got signal 6 ;", 2026, 3, 2, 10, 10, 10, "ERROR", "", "mysqld got signal 6 ;")]
    [DataRow("260302  9:05:01 [ERROR] /usr/sbin/mariadbd got signal 11 ;", 2026, 3, 2, 9, 5, 1, "ERROR", "", "/usr/sbin/mariadbd got signal 11 ;")]
    [DataRow("260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql", 2026, 3, 1, 12, 0, 0, "", "mysqld_safe", "Starting mariadbd daemon with databases from /var/lib/mysql")]
    [DataRow("2026-13-45 25:61:61 0 [Note] x", 2026, 13, 45, 25, 61, 61, "Note", "", "x")]
    public void MariaDbLineTryParseReadsEveryHeaderForm(string line, int year, int month, int day, int hour, int minute, int second, string level, string program, string message)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(line);

        // Act
        var header = MariaDbLine.TryParse(bytes);

        // Assert
        Assert.IsNotNull(header, "the line is a header");
        Assert.AreEqual(year, header.Time.Year, "year");
        Assert.AreEqual(month, header.Time.Month, "month");
        Assert.AreEqual(day, header.Time.Day, "day");
        Assert.AreEqual(hour, header.Time.Hour, "hour");
        Assert.AreEqual(minute, header.Time.Minute, "minute");
        Assert.AreEqual(second, header.Time.Second, "second");
        Assert.AreEqual(0, header.Time.FractionTicks, "no fraction");
        Assert.IsNull(header.Time.OffsetMinutes, "no offset");
        Assert.AreEqual(level, header.Level, "level");
        Assert.AreEqual(program, header.Program, "program");
        Assert.AreEqual(message, header.Message, "message");
    }

    /// <summary>
    /// The level maps to the syslog priority, and a header without a level has none.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">The expected priority, or -1 for none</param>
    [TestMethod]
    [DataRow("2026-03-01 12:00:00 0 [ERROR] x", 3)]
    [DataRow("2026-03-01 12:00:00 0 [Warning] x", 4)]
    [DataRow("2026-03-01 12:00:00 0 [Note] x", 6)]
    [DataRow("260301 12:00:00 [ERROR] x", 3)]
    [DataRow("260301 12:00:00 [Warning] x", 4)]
    [DataRow("260301 12:00:00 [Note] x", 6)]
    [DataRow("2026-03-01 12:00:00 0x7f3a2c1fe640  InnoDB: x", -1)]
    [DataRow("260301 12:00:00 mysqld_safe x", -1)]
    public void MariaDbLineTryParseMapsTheLevelToThePriority(string line, int expected)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(line);

        // Act
        var header = MariaDbLine.TryParse(bytes);

        // Assert
        Assert.IsNotNull(header, "the line is a header");
        Assert.AreEqual(expected < 0 ? null : (byte?)expected, header.Priority, "priority");
    }

    /// <summary>
    /// Bytes that are not UTF-8 in the message become U+FFFD while the prefix is still read.
    /// </summary>
    [TestMethod]
    public void MariaDbLineTryParseDecodesInvalidBytesOfTheMessage()
    {
        // Arrange
        byte[] bytes = [.. Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] a"), 0xFF, .. "b"u8.ToArray()];

        // Act
        var header = MariaDbLine.TryParse(bytes);

        // Assert
        Assert.IsNotNull(header, "the line is a header");
        Assert.AreEqual("Note", header.Level, "level");
        Assert.AreEqual(12, header.Time.Hour, "hour");
        Assert.AreEqual("a\uFFFDb", header.Message, "the invalid byte is replaced");
    }

    /// <summary>
    /// The message is not cut by the header parser, however long it is.
    /// </summary>
    [TestMethod]
    public void MariaDbLineTryParseDoesNotCutTheMessage()
    {
        // Arrange
        var message = new string('m', 20000);
        var bytes = Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] " + message);

        // Act
        var header = MariaDbLine.TryParse(bytes);

        // Assert
        Assert.IsNotNull(header, "the line is a header");
        Assert.AreEqual(message, header.Message, "the whole message");
    }

    /// <summary>
    /// A line that is not a header is a continuation line: <c>null</c>, and no exception.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04")]
    [DataRow("InnoDB: Failing assertion: page_is_leaf(block->page.frame)")]
    [DataRow("??:0(my_print_stacktrace)[0x55d0b1c1d2a2]")]
    [DataRow("")]
    [DataRow("2026-03-01 12:00:00 0 [Info] x")]
    [DataRow("2026-03-01 12:00:00 0 [note] x")]
    [DataRow("2026-03-01 12:00:00 0 [Note]x")]
    [DataRow("2026-03-01 12:00:00 [Note] x")]
    [DataRow("260301 12:00:00 0 [Note] x")]
    [DataRow("2026-03-01T12:00:00.123456Z 0 [System] [MY-010116] [Server] /usr/sbin/mysqld (mysqld 8.0.36) starting as process 1")]
    [DataRow("2026/03/01 12:00:00 [error] 1234#1234: *5 open() \"/var/www/x\" failed")]
    [DataRow("2026-3-01 12:00:00 0 [Note] x")]
    [DataRow("2026-03-01 12:00:00,123 fail2ban.filter [1234]: INFO [sshd] Found 203.0.113.5")]
    [DataRow("2026-03-01 12:00:00 status installed mariadb-server:amd64 1:10.6.12-0ubuntu0.22.04.1")]
    [DataRow("260301 12:00:00\t    5 Connect\troot@localhost on  using Socket")]
    [DataRow("# Time: 260301 12:00:00")]
    [DataRow("2026-03-01 12:00:00  0 [Note] x")]
    [DataRow("2026-03-01 12:00:00 0  [Note] x")]
    [DataRow("2026-03-01 12:00:00 0x  InnoDB: x")]
    [DataRow("2026-03-01 12:00:00 0X7F  InnoDB: x")]
    [DataRow("2026-03-01 12:00:00 0x7F  InnoDB: x")]
    [DataRow("2026-03-01 12:00:00 0x0123456789abcdef0  InnoDB: x")]
    [DataRow("2026-03-01 12:00:00 123456789012345678901 [Note] x")]
    [DataRow("2026-03-01 12:00:00 0x7f3a2c1fe640")]
    [DataRow("260301 12:00:00 mysqld_safeX y")]
    [DataRow("\u0660026-03-01 12:00:00 0 [Note] x")]
    [DataRow("2026-03-01 1:00:00 0 [Note] x")]
    public void MariaDbLineTryParseReturnsNullForAContinuationLine(string line)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(line);

        // Act
        var header = MariaDbLine.TryParse(bytes);

        // Assert
        Assert.IsNull(header, "the line is not a header");
    }

    /// <summary>
    /// A header cut at any length never throws; it is a header once its level is complete.
    /// </summary>
    /// <param name="length">The number of bytes</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(30)]
    public void MariaDbLineTryParseNeverThrowsOnCutHeaders(int length)
    {
        // Arrange
        var full = Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] x");
        var cut = full.AsSpan(0, Math.Min(length, full.Length)).ToArray();

        // Act
        var header = MariaDbLine.TryParse(cut);

        // Assert
        Assert.AreEqual(length >= 28, header is not null, "a header only when the level is complete");
    }

    #endregion // Methods
}