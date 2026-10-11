using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ErrorLogLine"/>
/// </summary>
[TestClass]
public class ErrorLogLineTests
{
    #region Constants

    private const string IdEight = "abcdefg9";
    private const string Id64 = IdEight + IdEight + IdEight + IdEight + IdEight + IdEight + IdEight + IdEight;
    private const string ModuleEight = "Ab_c1D_9";
    private const string Module64 = ModuleEight + ModuleEight + ModuleEight + ModuleEight + ModuleEight + ModuleEight + ModuleEight + ModuleEight;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A Plesk panel line with or without a fraction, a process ID and request ID, and any level is read: the time digits, the fraction in ticks, the priority of the level and the text after the time stamp are returned.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="fractionTicks">The expected fraction in ticks of 100 ns</param>
    /// <param name="priority">The expected priority</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("[2026-10-09 03:12:12.123] 4821:5f3a9c1e INFO [panel] Session opened", 1230000, 6, "4821:5f3a9c1e INFO [panel] Session opened")]
    [DataRow("[2026-10-09 03:12:12] ERR [panel] Cannot connect to the database", 0, 3, "ERR [panel] Cannot connect to the database")]
    [DataRow("[2026-10-09 03:12:12.5] WARN x", 5000000, 4, "WARN x")]
    [DataRow("[2026-10-09 03:12:12.000789] 4822:a1b2c3d4 DEBUG [panel] Cache warmed", 7890, 7, "4822:a1b2c3d4 DEBUG [panel] Cache warmed")]
    [DataRow("[2026-10-09 03:12:12.123456] EMERG [panel] x", 1234560, 0, "EMERG [panel] x")]
    [DataRow("[2026-10-09 03:12:12] ALERT x", 0, 1, "ALERT x")]
    [DataRow("[2026-10-09 03:12:12] CRIT x", 0, 2, "CRIT x")]
    [DataRow("[2026-10-09 03:12:12] NOTICE x", 0, 5, "NOTICE x")]
    [DataRow("[2026-10-09 03:12:12] INFO", 0, 6, "INFO")]
    [DataRow("[2026-10-09 03:12:12.1] 7:a DEBUG", 1000000, 7, "7:a DEBUG")]
    [DataRow("[2026-10-09 03:12:12] 1234567890:Ab9 INFO x", 0, 6, "1234567890:Ab9 INFO x")]
    [DataRow("[2026-10-09 03:12:12] 1:" + Id64 + " INFO x", 0, 6, "1:" + Id64 + " INFO x")]
    [DataRow("[2026-10-09 03:12:12] INFO  two spaces stay in the message", 0, 6, "INFO  two spaces stay in the message")]
    public void ErrorLogLineTryParseReadsPleskPanelLines(string line, int fractionTicks, int priority, string message)
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.PleskPanel, line);

        // Assert
        Assert.IsNotNull(parsed, $"\"{line}\" is a Plesk panel line");
        Assert.AreEqual(new SyslogTime(2026, 10, 9, 3, 12, 12, fractionTicks, null), parsed.Time, "time digits, fraction and no offset");
        Assert.AreEqual((byte)priority, parsed.Priority, "priority of the level");
        Assert.AreEqual(message, parsed.Message, "text after the time stamp");
    }

    /// <summary>
    /// A line that does not have the Plesk panel shape is refused, also when its time digits or its level are not exactly those of the format.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("[2026-10-09 03:12:12]")]
    [DataRow("[2026-10-09 03:12:12] ")]
    [DataRow("[2026-10-09 03:12:12] info x")]
    [DataRow("[2026-10-09 03:12:12] Info x")]
    [DataRow("[2026-10-09 03:12:12] WARNING x")]
    [DataRow("[2026-10-09 03:12:12] ERROR x")]
    [DataRow("[2026-10-09 03:12:12] INFOx")]
    [DataRow("[2026-10-09 03:12:12]INFO x")]
    [DataRow("[2026-10-09 03:12:12]  INFO x")]
    [DataRow("2026-10-09 03:12:12 INFO x")]
    [DataRow(" [2026-10-09 03:12:12] INFO x")]
    [DataRow("[2026-10-09T03:12:12] INFO x")]
    [DataRow("[2026/10/09 03:12:12] INFO x")]
    [DataRow("[26-10-09 03:12:12] INFO x")]
    [DataRow("[2026-10-9 03:12:12] INFO x")]
    [DataRow("[2026-10-09 03:12] INFO x")]
    [DataRow("[2026-10-09 03:12:12.] INFO x")]
    [DataRow("[2026-10-09 03:12:12.1234567] INFO x")]
    [DataRow("[2026-10-09 03:12:12,123] INFO x")]
    [DataRow("[2026-10-09 03:12:12 +0200] INFO x")]
    [DataRow("[2026-10-09 03:12:12] 4821:5f3a9c1e")]
    [DataRow("[2026-10-09 03:12:12] 4821:5f3a9c1e  INFO x")]
    [DataRow("[2026-10-09 03:12:12] 4821 INFO x")]
    [DataRow("[2026-10-09 03:12:12] :5f3a9c1e INFO x")]
    [DataRow("[2026-10-09 03:12:12] 4821: INFO x")]
    [DataRow("[2026-10-09 03:12:12] 4821:5f-3a INFO x")]
    [DataRow("[2026-10-09 03:12:12] a821:5f3a9c1e INFO x")]
    [DataRow("[2026-10-09 03:12:12] 12345678901:5f3a9c1e INFO x")]
    [DataRow("[2026-10-09 03:12:12] 1:" + Id64 + "x INFO x")]
    [DataRow("2026/10/09 03:12:12 [error] 1021#1021: x")]
    [DataRow("[Fri Oct 09 03:12:12.123456 2026] [core:error] x")]
    public void ErrorLogLineTryParseRefusesOtherPleskPanelShapes(string line)
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.PleskPanel, line);

        // Assert
        Assert.IsNull(parsed, $"\"{line}\" is no Plesk panel line");
    }

    /// <summary>
    /// A time stamp whose digits are no valid date or clock time is still a line of the format, because the digits are checked when the time is resolved.
    /// </summary>
    [TestMethod]
    public void ErrorLogLineTryParseDoesNotRangeCheckTheDigitsOfThePleskPanelTime()
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.PleskPanel, "[2026-13-45 25:61:62] INFO x");

        // Assert
        Assert.IsNotNull(parsed, "the grammar accepts the digits");
        Assert.AreEqual(new SyslogTime(2026, 13, 45, 25, 61, 62, 0, null), parsed.Time, "the digits as written");
    }

    /// <summary>
    /// An nginx line with any level is read: the time digits, the priority and the text from the bracket of the level on are returned.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="priority">The expected priority</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("2026/10/09 03:12:12 [emerg] 1021#1021: x", 0, "[emerg] 1021#1021: x")]
    [DataRow("2026/10/09 03:12:12 [alert] 1021#1021: x", 1, "[alert] 1021#1021: x")]
    [DataRow("2026/10/09 03:12:12 [crit] 1021#1021: *16 connect() failed", 2, "[crit] 1021#1021: *16 connect() failed")]
    [DataRow("2026/10/09 03:12:12 [error] 1021#1021: *15 FastCGI sent in stderr", 3, "[error] 1021#1021: *15 FastCGI sent in stderr")]
    [DataRow("2026/10/09 03:12:12 [warn] 1021#1021: x", 4, "[warn] 1021#1021: x")]
    [DataRow("2026/10/09 03:12:12 [notice] 1021#1021: signal process started", 5, "[notice] 1021#1021: signal process started")]
    [DataRow("2026/10/09 03:12:12 [info] 1021#1021: x", 6, "[info] 1021#1021: x")]
    [DataRow("2026/10/09 03:12:12 [debug] 1021#1021: x", 7, "[debug] 1021#1021: x")]
    [DataRow("2026/10/09 03:12:12 [error]", 3, "[error]")]
    [DataRow("2026/10/09 03:12:12 [error]  x", 3, "[error]  x")]
    [DataRow("2026/10/09 03:12:12 [error] ", 3, "[error] ")]
    public void ErrorLogLineTryParseReadsNginxLines(string line, int priority, string message)
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.Nginx, line);

        // Assert
        Assert.IsNotNull(parsed, $"\"{line}\" is an nginx line");
        Assert.AreEqual(new SyslogTime(2026, 10, 9, 3, 12, 12, 0, null), parsed.Time, "time digits, no fraction and no offset");
        Assert.AreEqual((byte)priority, parsed.Priority, "priority of the level");
        Assert.AreEqual(message, parsed.Message, "text from the bracket of the level on");
    }

    /// <summary>
    /// The digits of an nginx time stamp are not range-checked by the grammar.
    /// </summary>
    [TestMethod]
    public void ErrorLogLineTryParseDoesNotRangeCheckTheDigitsOfTheNginxTime()
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.Nginx, "2026/13/45 25:61:62 [error] x");

        // Assert
        Assert.IsNotNull(parsed, "the grammar accepts the digits");
        Assert.AreEqual(new SyslogTime(2026, 13, 45, 25, 61, 62, 0, null), parsed.Time, "the digits as written");
    }

    /// <summary>
    /// A line that does not have the nginx shape is refused: other separators, a level in the wrong case or spelling, missing brackets, a fraction, and lines of the other formats.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("2026/10/09 03:12:12")]
    [DataRow("2026/10/09 03:12:12 ")]
    [DataRow("2026/10/09 03:12:12 [Error] x")]
    [DataRow("2026/10/09 03:12:12 [ERROR] x")]
    [DataRow("2026/10/09 03:12:12 [warning] x")]
    [DataRow("2026/10/09 03:12:12 [err] x")]
    [DataRow("2026/10/09 03:12:12 [error]x")]
    [DataRow("2026/10/09 03:12:12 [errors] x")]
    [DataRow("2026/10/09 03:12:12 [] x")]
    [DataRow("2026/10/09 03:12:12 [error x")]
    [DataRow("2026/10/09 03:12:12 error x")]
    [DataRow("2026/10/09 03:12:12  [error] x")]
    [DataRow("2026/10/09 03:12:12[error] x")]
    [DataRow("2026/10/09 03:12:12.123 [error] x")]
    [DataRow("2026-10-09 03:12:12 [error] x")]
    [DataRow("2026/10/09T03:12:12 [error] x")]
    [DataRow("26/10/09 03:12:12 [error] x")]
    [DataRow("2026/10/9 03:12:12 [error] x")]
    [DataRow("2026/10/09 03:12 [error] x")]
    [DataRow(" 2026/10/09 03:12:12 [error] x")]
    [DataRow("[2026-10-09 03:12:12] INFO x")]
    [DataRow("[Fri Oct 09 03:12:12.123456 2026] [core:error] x")]
    [DataRow("Oct  9 03:12:12 web-1 sshd[1]: x")]
    public void ErrorLogLineTryParseRefusesOtherNginxShapes(string line)
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.Nginx, line);

        // Assert
        Assert.IsNull(parsed, $"\"{line}\" is no nginx line");
    }

    /// <summary>
    /// An Apache 2.4 line with a module and a fraction, without a module, without a fraction, and an Apache 2.2 line are read, with the priority of the level and the text from the bracket of the level on.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="month">The expected month</param>
    /// <param name="day">The expected day</param>
    /// <param name="fractionTicks">The expected fraction in ticks of 100 ns</param>
    /// <param name="priority">The expected priority</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("[Fri Oct 09 03:12:12.123456 2026] [mpm_prefork:notice] [pid 812] AH00163: resuming", 10, 9, 1234560, 5, "[mpm_prefork:notice] [pid 812] AH00163: resuming")]
    [DataRow("[Fri Oct 09 03:12:12.123456 2026] [:warn] [pid 902:tid 140] x", 10, 9, 1234560, 4, "[:warn] [pid 902:tid 140] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:error] [pid 1] x", 10, 9, 0, 3, "[core:error] [pid 1] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [error] [client 198.51.100.8] File does not exist", 10, 9, 0, 3, "[error] [client 198.51.100.8] File does not exist")]
    [DataRow("[Mon Jan 01 03:12:12.000001 2026] [core:emerg] x", 1, 1, 10, 0, "[core:emerg] x")]
    [DataRow("[Tue Feb 02 03:12:12 2026] [core:alert] x", 2, 2, 0, 1, "[core:alert] x")]
    [DataRow("[Wed Mar 03 03:12:12 2026] [core:crit] x", 3, 3, 0, 2, "[core:crit] x")]
    [DataRow("[Thu Apr 04 03:12:12 2026] [core:info] x", 4, 4, 0, 6, "[core:info] x")]
    [DataRow("[Sat May 05 03:12:12 2026] [core:debug] x", 5, 5, 0, 7, "[core:debug] x")]
    [DataRow("[Sun Jun 06 03:12:12 2026] [core:trace1] x", 6, 6, 0, 7, "[core:trace1] x")]
    [DataRow("[Fri Jul 07 03:12:12 2026] [core:trace8] x", 7, 7, 0, 7, "[core:trace8] x")]
    [DataRow("[Fri Aug 08 03:12:12 2026] [error]", 8, 8, 0, 3, "[error]")]
    [DataRow("[Fri Sep 09 03:12:12 2026] [error]  x", 9, 9, 0, 3, "[error]  x")]
    [DataRow("[Fri Nov 11 03:12:12 2026] [Mod_1:warn] x", 11, 11, 0, 4, "[Mod_1:warn] x")]
    [DataRow("[Fri Dec 12 03:12:12 2026] [" + Module64 + ":warn] x", 12, 12, 0, 4, "[" + Module64 + ":warn] x")]
    public void ErrorLogLineTryParseReadsApacheLines(string line, int month, int day, int fractionTicks, int priority, string message)
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.Apache, line);

        // Assert
        Assert.IsNotNull(parsed, $"\"{line}\" is an Apache line");
        Assert.AreEqual(new SyslogTime(2026, month, day, 3, 12, 12, fractionTicks, null), parsed.Time, "time digits, fraction and no offset");
        Assert.AreEqual((byte)priority, parsed.Priority, "priority of the level");
        Assert.AreEqual(message, parsed.Message, "text from the bracket of the level on");
    }

    /// <summary>
    /// The digits of an Apache time stamp are not range-checked by the grammar, and the weekday is not compared with the date.
    /// </summary>
    [TestMethod]
    public void ErrorLogLineTryParseDoesNotRangeCheckTheDigitsOfTheApacheTime()
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.Apache, "[Mon Oct 99 25:61:62 2026] [error] x");

        // Assert
        Assert.IsNotNull(parsed, "the grammar accepts the digits and the weekday");
        Assert.AreEqual(new SyslogTime(2026, 10, 99, 25, 61, 62, 0, null), parsed.Time, "the digits as written");
    }

    /// <summary>
    /// A line that does not have the Apache shape is refused: a space-padded day, a fraction that is not six digits, a weekday or month in another case or language, a two-digit year, a level that is not one of Apache's, a module with a character outside the set or longer than 64, and a startup line without time stamp.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("[Fri Oct  9 03:12:12.123456 2026] [core:error] x")]
    [DataRow("[Fri Oct 9 03:12:12.123456 2026] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12.12345 2026] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12.1234567 2026] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12. 2026] [core:error] x")]
    [DataRow("[fri Oct 09 03:12:12 2026] [core:error] x")]
    [DataRow("[FRI Oct 09 03:12:12 2026] [core:error] x")]
    [DataRow("[Friday Oct 09 03:12:12 2026] [core:error] x")]
    [DataRow("[Xyz Oct 09 03:12:12 2026] [core:error] x")]
    [DataRow("[Fri oct 09 03:12:12 2026] [core:error] x")]
    [DataRow("[Fri Okt 09 03:12:12 2026] [core:error] x")]
    [DataRow("[Fri October 09 03:12:12 2026] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 26] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 20266] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12 2026] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 +0200 2026] [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026][core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026]  [core:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] core:error x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:error")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:error]x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:Error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:ERROR] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:warning] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [warning] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:trace0] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:trace9] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:trace] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [core:] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [mod-x:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [mod.x:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [a:b:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026] [" + Module64 + "x:error] x")]
    [DataRow("[Fri Oct 09 03:12:12 2026]")]
    [DataRow("[")]
    [DataRow("[Fr")]
    [DataRow("[Fri Oc")]
    [DataRow("AH00558: apache2: Could not reliably determine the server's fully qualified domain name")]
    [DataRow("[2026-10-09 03:12:12] INFO x")]
    [DataRow("2026/10/09 03:12:12 [error] 1021#1021: x")]
    public void ErrorLogLineTryParseRefusesOtherApacheShapes(string line)
    {
        // Act
        var parsed = ErrorLogLine.TryParse(ErrorLogFormat.Apache, line);

        // Assert
        Assert.IsNull(parsed, $"\"{line}\" is no Apache line");
    }

    /// <summary>
    /// A format value outside the enumeration is no format of any line.
    /// </summary>
    [TestMethod]
    public void ErrorLogLineTryParseRefusesAnUnknownFormat()
    {
        // Act
        var parsed = ErrorLogLine.TryParse((ErrorLogFormat)99, "2026/10/09 03:12:12 [error] x");

        // Assert
        Assert.IsNull(parsed, "no line is of an unknown format");
    }

    #endregion // Methods
}