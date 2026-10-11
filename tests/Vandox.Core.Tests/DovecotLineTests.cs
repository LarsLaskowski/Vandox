using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="DovecotLine"/>
/// </summary>
[TestClass]
public class DovecotLineTests
{
    #region Methods

    /// <summary>
    /// A line with the traditional time stamp and a message that begins with a marker, or has one after its first segment, is read; time and message are returned unchanged.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="month">The expected month</param>
    /// <param name="day">The expected day</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("Oct 09 03:12:13 Debug: lmtp-server: conn 192.0.2.1:41234 [1]: Connect from local", 10, 9, "Debug: lmtp-server: conn 192.0.2.1:41234 [1]: Connect from local")]
    [DataRow("Oct 09 03:12:13 Info: imap(bob)<2402><QrStUvWx>: Connection closed in=12 out=345", 10, 9, "Info: imap(bob)<2402><QrStUvWx>: Connection closed in=12 out=345")]
    [DataRow("Oct 09 03:12:13 Warning: x", 10, 9, "Warning: x")]
    [DataRow("Oct 09 03:12:13 Error: x", 10, 9, "Error: x")]
    [DataRow("Oct 09 03:12:13 Fatal: Dovecot is already running", 10, 9, "Fatal: Dovecot is already running")]
    [DataRow("Oct 09 03:12:13 Panic: x", 10, 9, "Panic: x")]
    [DataRow("Oct 09 03:12:13 lmtp(2400): Debug: x", 10, 9, "lmtp(2400): Debug: x")]
    [DataRow("Oct 09 03:12:13 imap-login: Info: Login: user=<alice@example.org>, method=PLAIN", 10, 9, "imap-login: Info: Login: user=<alice@example.org>, method=PLAIN")]
    [DataRow("Oct 09 03:12:13 auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: MySQL server has gone away", 10, 9, "auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: MySQL server has gone away")]
    [DataRow("Oct 09 03:12:13 master: Error: service(imap): child 2301 killed with signal 9", 10, 9, "master: Error: service(imap): child 2301 killed with signal 9")]
    [DataRow("Oct 09 03:12:13 auth: Fatal: master: service(auth): child 2290 returned error 89", 10, 9, "auth: Fatal: master: service(auth): child 2290 returned error 89")]
    [DataRow("Oct 09 03:12:13 imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42", 10, 9, "imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42")]
    [DataRow("Oct  9 03:12:13 master: Info: x", 10, 9, "master: Info: x")]
    [DataRow("Oct  9 03:12:13 Info: x", 10, 9, "Info: x")]
    [DataRow("Jan 31 03:12:13 Info: ", 1, 31, "Info: ")]
    [DataRow("Dec  1 03:12:13 a: Panic: ", 12, 1, "a: Panic: ")]
    public void DovecotLineTryParseReadsTimeAndMessage(string line, int month, int day, string message)
    {
        // Act
        var parsed = DovecotLine.TryParse(line);

        // Assert
        Assert.IsNotNull(parsed, "the line is a Dovecot line");
        Assert.AreEqual(message, parsed.Message, "message as written");
        Assert.AreEqual(0, parsed.Time.Year, "traditional time has no year");
        Assert.AreEqual(month, parsed.Time.Month, "month");
        Assert.AreEqual(day, parsed.Time.Day, "day");
        Assert.AreEqual(3, parsed.Time.Hour, "hour");
        Assert.AreEqual(12, parsed.Time.Minute, "minute");
        Assert.AreEqual(13, parsed.Time.Second, "second");
        Assert.IsNull(parsed.Time.OffsetMinutes, "no offset");
    }

    /// <summary>
    /// A line without a time stamp of the traditional form, or without a marker at Dovecot's position, is no Dovecot line.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("Oct  9 03:12:12 mail-1 dovecot: master: Info: x")]
    [DataRow("Oct 09 03:12:13 imap-login: Login: x")]
    [DataRow("Oct 09 03:12:13 info: x")]
    [DataRow("Oct 09 03:12:13 imap-login: info: x")]
    [DataRow("Oct 09 03:12:13 INFO: x")]
    [DataRow("Oct 09 03:12:13 Info:x")]
    [DataRow("Oct 09 03:12:13 imap-login: Info:x")]
    [DataRow("Oct 09 03:12:13 Info:")]
    [DataRow("Oct 09 03:12:13 a: b: Info: x")]
    [DataRow("Oct 09 03:12:13 imap(a b): Info: x")]
    [DataRow("Oct 09 03:12:13 : Info: x")]
    [DataRow("<30>Oct 09 03:12:13 master: Info: x")]
    [DataRow("2026-10-09T03:12:13Z master: Info: x")]
    [DataRow("2026-10-09T03:12:13+02:00 Info: x")]
    [DataRow("Foo 09 03:12:13 Info: x")]
    [DataRow("Oct 09 24:12:13 Info: x")]
    [DataRow("Oct 09 03:60:13 Info: x")]
    [DataRow("Oct 00 03:12:13 Info: x")]
    [DataRow("Oct 09 03:12:13  Info: x")]
    [DataRow("Oct 09 03:12:13  master: Info: x")]
    [DataRow("Oct 09 03:12:13")]
    [DataRow("Oct 09 03:12:13 ")]
    [DataRow("Oct 09 03:12:13Info: x")]
    [DataRow("")]
    public void DovecotLineTryParseRefusesLinesWithoutAMarkerAtDovecotsPosition(string line)
    {
        // Act
        var parsed = DovecotLine.TryParse(line);

        // Assert
        Assert.IsNull(parsed, $"\"{line}\" is no Dovecot line");
    }

    #endregion // Methods
}