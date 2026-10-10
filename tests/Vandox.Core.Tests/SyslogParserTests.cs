using System.Globalization;
using System.Text;

using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SyslogParser"/>
/// </summary>
[TestClass]
public class SyslogParserTests
{
    #region Constants

    private const string NotSyslog = "not a syslog line";
    private const string Outside = "time outside the storable range";
    private const string Unknown = "year unknown: the file has no usable date";
    private const string Header = "Mar  1 12:00:00 web-1 sshd[1]: ";
    private const string Valid = "Mar  1 12:00:01 web-1 sshd[1]: valid";

    #endregion // Constants

    #region Fields

    private static readonly LogFile _file = new("syslog", new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The type of the parser is <c>syslog</c>.
    /// </summary>
    [TestMethod]
    public void SyslogParserTypeIsSyslog()
    {
        // Act
        var parser = new SyslogParser(DateTimeZone.Utc);

        // Assert
        Assert.AreEqual("syslog", parser.Type, "type");
    }

    /// <summary>
    /// A file is claimed by the shape of its first line or by its name, weakly; anything else is not.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head of the file</param>
    /// <param name="expected">The expected confidence</param>
    [TestMethod]
    [DataRow("other.txt", "Mar  1 12:00:00 web-1 sshd[1]: hello\n", Confidence.MatchName)]
    [DataRow("other.txt", "Mar 21 06:25:01 web-1 CRON[3321]: (root) CMD\nMar 21 06:25:02 web-1 a: b\n", Confidence.MatchName)]
    [DataRow("other.txt", "2026-03-01T12:00:00.123456+01:00 web-1 sshd[1]: hello\n", Confidence.MatchName)]
    [DataRow("other.txt", "<13>Mar  1 12:00:00 web-1 sshd[1]: hello\n", Confidence.MatchName)]
    [DataRow("other.txt", "﻿Mar  1 12:00:00 web-1 sshd[1]: hello\n", Confidence.MatchName)]
    [DataRow("other.txt", "Mar  1 12:00:00 web-1 sshd[1]: hello\r\nMar  1 12:00:01 web-1 a: b\r\n", Confidence.MatchName)]
    [DataRow("other.txt", "Mar  1 12:00:00 web-1 sshd[1]: no line break in the head", Confidence.MatchName)]
    [DataRow("syslog", "", Confidence.MatchName)]
    [DataRow("kern.log", "", Confidence.MatchName)]
    [DataRow("a/b/syslog.1", "", Confidence.MatchName)]
    [DataRow("kern.log.2", "", Confidence.MatchName)]
    [DataRow("syslog-20260301", "", Confidence.MatchName)]
    [DataRow("backup/var/log/kern.log-20260301", "some prose\n", Confidence.MatchName)]
    [DataRow("syslog.12", "some prose\n", Confidence.MatchName)]
    [DataRow("other.txt", "", Confidence.NoMatch)]
    [DataRow("other.txt", "This is a note about the server, not a log.\n", Confidence.NoMatch)]
    [DataRow("other.txt", "__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n", Confidence.NoMatch)]
    [DataRow("other.txt", "2026-03-01 12:00:00 0 [Note] InnoDB: Buffer pool(s) load completed\n", Confidence.NoMatch)]
    [DataRow("other.txt", "<34>1 2026-03-01T12:00:00Z web-1 su - - - message\n", Confidence.NoMatch)]
    [DataRow("syslog.bak", "some prose\n", Confidence.NoMatch)]
    [DataRow("mysyslog", "some prose\n", Confidence.NoMatch)]
    [DataRow("syslogs", "some prose\n", Confidence.NoMatch)]
    [DataRow("kern.log.x", "some prose\n", Confidence.NoMatch)]
    [DataRow("syslog-2026", "some prose\n", Confidence.NoMatch)]
    [DataRow("auth.log", "some prose\n", Confidence.NoMatch)]
    public void SyslogParserDetectClaimsFilesWeakly(string name, string head, Confidence expected)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(expected, confidence, "confidence");
    }

    /// <summary>
    /// A line becomes a record of origin import with the time in UTC and the fields of the line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expectedTime">The expected time in UTC</param>
    /// <param name="host">The expected host</param>
    /// <param name="program">The expected program</param>
    /// <param name="pid">The expected process ID</param>
    /// <param name="priority">The expected priority, or -1 for none</param>
    /// <param name="message">The expected message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1234]: Accepted publickey for root", "2026-03-01T12:00:00.0000000Z", "web-1", "sshd", 1234, -1, "Accepted publickey for root")]
    [DataRow("Mar 21 06:25:01 web-1 CRON[3321]: (root) CMD (command -v debian-sa1)", "2025-03-21T06:25:01.0000000Z", "web-1", "CRON", 3321, -1, "(root) CMD (command -v debian-sa1)")]
    [DataRow("2026-03-01T12:00:00.123456+01:00 web-1 postfix/smtpd[2210]: connect from unknown[192.0.2.9]", "2026-03-01T11:00:00.1234560Z", "web-1", "postfix/smtpd", 2210, -1, "connect from unknown[192.0.2.9]")]
    [DataRow("2026-03-01T12:00:00Z web-1 kernel: [123456.789012] Out of memory", "2026-03-01T12:00:00.0000000Z", "web-1", "kernel", 0, -1, "[123456.789012] Out of memory")]
    [DataRow("<13>Mar  1 12:00:00 web-1 systemd[1]: Started Daily apt upgrade", "2026-03-01T12:00:00.0000000Z", "web-1", "systemd", 1, 5, "Started Daily apt upgrade")]
    [DataRow("<27>2026-03-01T12:00:00-02:30 web-1 systemd[1]: Failed", "2026-03-01T14:30:00.0000000Z", "web-1", "systemd", 1, 3, "Failed")]
    [DataRow("Mar  1 12:00:11 web-1 last message repeated 3 times", "2026-03-01T12:00:11.0000000Z", "web-1", "", 0, -1, "last message repeated 3 times")]
    [DataRow("Mar  1 12:00:11 web-1 sshd[2147483648]: process ID too large", "2026-03-01T12:00:11.0000000Z", "web-1", "sshd", 0, -1, "process ID too large")]
    public async Task SyslogParserParseAsyncMapsTheLineToARecord(string line, string expectedTime, string host, string program, int pid, int priority, string message)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var file = new LogFile("backup/var/log/syslog.1", new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, line + "\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var payload = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual("syslog", record.Source, "source");
        Assert.AreEqual(0UL, record.Seq, "sequence number");
        Assert.AreEqual(DateTimeOffset.Parse(expectedTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), record.CapturedAt, "time");
        Assert.AreEqual(TimeSpan.Zero, record.CapturedAt.Offset, "UTC");
        Assert.AreEqual("backup/var/log/syslog.1", payload.Log, "log is the path as the import lists it");
        Assert.AreEqual(host, payload.Host, "host");
        Assert.AreEqual(program, payload.Program, "program");
        Assert.AreEqual(pid, payload.Pid, "process ID");
        Assert.AreEqual(priority < 0 ? null : (byte)priority, payload.Priority, "priority");
        Assert.AreEqual(message, payload.Message, "message");
        Assert.IsFalse(payload.Truncated, "not truncated");
    }

    /// <summary>
    /// A byte order mark is removed from the first line only: there the line is a record, on a later line the mark stays and the line is skipped.
    /// </summary>
    /// <param name="markOnFirstLine">Whether the mark starts the first line; otherwise it starts the second</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SyslogParserParseAsyncRemovesTheByteOrderMarkOnlyFromTheFirstLine(bool markOnFirstLine)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var line = "2026-03-01T12:00:00Z web-1 sshd[1]: hello\n"u8.ToArray();
        byte[] content = markOnFirstLine ? [.. bom, .. line] : [.. "\n"u8, .. bom, .. line];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        if (markOnFirstLine)
        {
            Assert.IsEmpty(emitter.Skips, "nothing is skipped");
            Assert.HasCount(1, emitter.Records, "one record");

            var payload = RecordingEmitter.Line(emitter.Records[0]);

            Assert.AreEqual("sshd", payload.Program, "program");
            Assert.AreEqual(1, payload.Pid, "process ID");
            Assert.AreEqual("hello", payload.Message, "message");
        }
        else
        {
            Assert.IsEmpty(emitter.Records, "no record");
            Assert.AreSequenceEqual<(long, string)>([(1, "empty line"), (2, NotSyslog)], emitter.Skips, "the mark on line 2 is kept and the line is skipped");
        }
    }

    /// <summary>
    /// Lines that are not records are skipped with their line number and the parse continues.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncSkipsLinesThatAreNotRecords()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var text = string.Join('\n',
                               string.Empty,
                               "garbage",
                               "Foo  1 12:00:00 web-1 a: b",
                               "Mar  1 24:00:00 web-1 a: b",
                               "Mar  1 12:60:00 web-1 a: b",
                               "2026-03-01T12:00:00+14:01 web-1 a: b",
                               "2026-03-01t12:00:00Z web-1 a: b",
                               "2026-03-01T12:00:00z web-1 a: b",
                               "2026-02-30T12:00:00Z web-1 a: b",
                               "2026-04-31T12:00:00Z web-1 a: b",
                               "Feb 29 12:00:00 web-1 a: b",
                               "<34>1 2026-03-01T12:00:00Z web-1 su - - - message",
                               Valid,
                               string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, text, TestContext.CancellationToken);

        // Assert
        var expected = "1:empty line|2:not a syslog line|3:not a syslog line|4:not a syslog line|5:not a syslog line|6:not a syslog line|7:not a syslog line|8:not a syslog line|9:invalid date|10:invalid date|11:invalid date|12:not a syslog line";

        Assert.AreEqual(expected, string.Join('|', emitter.Skips.Select(skip => $"{skip.Line}:{skip.Reason}")), "skips with their line numbers");
        Assert.HasCount(1, emitter.Records, "the valid line is emitted");
        Assert.AreEqual("valid", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
    }

    /// <summary>
    /// A time outside the range storage can hold is skipped with a fixed reason, and the valid line after it is emitted.
    /// </summary>
    /// <param name="time">The RFC 3339 time</param>
    /// <param name="accepted">Whether the time is accepted</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("0001-01-01T00:00:00+01:00", false)]
    [DataRow("9999-12-31T23:59:59-01:00", false)]
    [DataRow("0000-01-01T00:00:00Z", false)]
    [DataRow("1677-09-21T00:12:43Z", false)]
    [DataRow("2262-04-11T23:47:17Z", false)]
    [DataRow("1677-09-21T00:12:44Z", true)]
    [DataRow("2262-04-11T23:47:16Z", true)]
    public async Task SyslogParserParseAsyncSkipsRfc3339TimesOutsideTheStorableRange(string time, bool accepted)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, $"{time} web-1 sshd[1]: edge\n{Valid}\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(accepted ? 2 : 1, emitter.Records, "records");
        Assert.HasCount(accepted ? 0 : 1, emitter.Skips, "skips");

        if (accepted)
        {
            Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        }
        else
        {
            Assert.AreEqual((1L, Outside), emitter.Skips[0], "the skip reason is fixed");
        }

        Assert.AreEqual("valid", RecordingEmitter.Line(emitter.Records[^1]).Message, "the line after it is emitted");
    }

    /// <summary>
    /// Without any date for the year, year-less lines are skipped with a reason and RFC 3339 lines are still read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncSkipsYearLessLinesWithoutAnyDate()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var file = new LogFile("syslog", null);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, "Mar  1 12:00:00 web-1 a: year-less\n2026-03-01T12:00:00Z web-1 a: with year\nMar  1 12:00:02 web-1 a: year-less again\n", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(1, Unknown), (3, Unknown)], emitter.Skips, "year-less lines are skipped");
        Assert.HasCount(1, emitter.Records, "the RFC 3339 line is read");
        Assert.AreEqual("with year", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
    }

    /// <summary>
    /// The year advances once at every New Year until it leaves the storable range, with the exact counts of records and skips.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncAdvancesTheYearUpToTheStorableRange()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var text = new StringBuilder();

        for (var line = 1; line <= 1000; line++)
        {
            text.Append(line % 2 == 1 ? "Jul  1 00:00:00 web-1 a: b\n" : "Jan  1 00:00:00 web-1 a: b\n");
        }

        var file = new LogFile("syslog", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, text.ToString(), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(472, emitter.Records, "lines 1 to 472 are records");
        Assert.AreEqual(new DateTimeOffset(2262, 1, 1, 0, 0, 0, TimeSpan.Zero), emitter.Records[^1].CapturedAt, "the last record is 2262-01-01");
        Assert.HasCount(528, emitter.Skips, "lines 473 to 1000 are skipped");
        Assert.AreEqual(473L, emitter.Skips[0].Line, "first skipped line");
        Assert.AreEqual(1000L, emitter.Skips[^1].Line, "last skipped line");
        Assert.IsTrue(emitter.Skips.All(skip => skip.Reason == Outside), "every skip has the fixed reason");
    }

    /// <summary>
    /// Only year-less lines are predecessors for the year advance: RFC 3339 lines, empty lines and lines that are not syslog lines are not.
    /// </summary>
    /// <param name="text">The file</param>
    /// <param name="expected">The expected records as times, skips as reasons, separated by a bar</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Jul  1 00:00:00 h a: b\n2026-12-31T00:00:00Z h a: b\nJun  1 00:00:00 h a: b\n", "2026-07-01T00:00:00Z|2026-12-31T00:00:00Z|2026-06-01T00:00:00Z")]
    [DataRow("Jul  1 00:00:00 h a: b\nNov 31 12:00:00 h a: b\nMay  1 00:00:00 h a: b\n", "2026-07-01T00:00:00Z|invalid date|2027-05-01T00:00:00Z")]
    [DataRow("Jul  1 00:00:00 h a: b\nnot a log line\n\nJun  1 00:00:00 h a: b\n", "2026-07-01T00:00:00Z|not a syslog line|empty line|2026-06-01T00:00:00Z")]
    public async Task SyslogParserParseAsyncComparesYearsOnlyWithYearLessLines(string text, string expected)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var file = new LogFile("syslog", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, text, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(expected, Outcomes(emitter), "records and skips in file order");
    }

    /// <summary>
    /// Local times are read in the zone, and the repeated hour compares with the last year-less line only.
    /// </summary>
    /// <param name="text">The file</param>
    /// <param name="expected">The expected records as times, skips as reasons, separated by a bar</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Jul  1 12:00:00 h a: b\n", "2026-07-01T10:00:00Z")]
    [DataRow("Jan 15 12:00:00 h a: b\n", "2026-01-15T11:00:00Z")]
    [DataRow("Mar 29 02:30:00 h a: b\n", "2026-03-29T01:30:00Z")]
    [DataRow("Oct 25 02:59:59 h a: b\n2026-10-25T00:55:00Z h a: b\nOct 25 02:49:00 h a: b\n", "2026-10-25T00:59:59Z|2026-10-25T00:55:00Z|2026-10-25T01:49:00Z")]
    [DataRow("Oct 25 02:59:59 h a: b\nSep 31 12:00:00 h a: b\nOct 25 02:49:00 h a: b\n", "2026-10-25T00:59:59Z|invalid date|2026-10-25T01:49:00Z")]
    [DataRow("2026-07-01T12:00:00+05:00 h a: b\n", "2026-07-01T07:00:00Z")]
    public async Task SyslogParserParseAsyncReadsYearLessTimesInTheZone(string text, string expected)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZoneProviders.Tzdb["Europe/Berlin"]);
        var file = new LogFile("syslog", new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, text, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(expected, Outcomes(emitter), "records and skips in file order");
    }

    /// <summary>
    /// A line cut by the line reader is marked as truncated and its message stays within the limit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncMarksLineCutByTheReaderAsTruncated()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var text = $"{Header}{new string('a', LogLineReader.MaxLineBytes + 5000)}\n{Valid}\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(2, emitter.Records, "both lines are records");
        Assert.IsTrue(RecordingEmitter.Line(emitter.Records[0]).Truncated, "truncated");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(emitter.Records[0]).Message), "message within the limit");
        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[1]).Truncated, "the next line is not truncated");
    }

    /// <summary>
    /// Invalid bytes that grow when they are decoded are cut to the limit in UTF-8 bytes and the record is not refused.
    /// </summary>
    /// <param name="header">The header of the line</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Mar  1 12:00:00 web-1 sshd[1]: ")]
    [DataRow("2026-03-01T12:00:00+01:00 web-1 sshd[1]: ")]
    public async Task SyslogParserParseAsyncCutsInvalidBytesToTheLimitInUtf8Bytes(string header)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var prefix = Encoding.UTF8.GetBytes(header);
        var line = prefix.Concat(Enumerable.Repeat((byte)0xFF, LogLineReader.MaxLineBytes - prefix.Length)).Append((byte)'\n').ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, line, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record is refused");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(emitter.Records[0]).Message), "message within the limit in UTF-8 bytes");
        Assert.IsTrue(RecordingEmitter.Line(emitter.Records[0]).Truncated, "truncated");
    }

    /// <summary>
    /// The host is limited syntactically to 255 UTF-8 bytes of the decoded line and is never cut.
    /// </summary>
    /// <param name="bytePattern">The byte that is repeated, as a character</param>
    /// <param name="count">How often it is repeated</param>
    /// <param name="accepted">Whether the line is a record</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow('a', 255, true)]
    [DataRow('a', 256, false)]
    [DataRow('ÿ', 85, true)]
    [DataRow('ÿ', 86, false)]
    [DataRow('ÿ', 255, false)]
    public async Task SyslogParserParseAsyncLimitsTheHostToDecodedBytes(char bytePattern, int count, bool accepted)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var host = Enumerable.Repeat(bytePattern == 'a' ? (byte)'a' : (byte)0xFF, count).ToArray();
        byte[] content = [.. "Mar  1 12:00:00 "u8, .. host, .. " sshd[1]: first\n"u8, .. Encoding.UTF8.GetBytes(Valid + "\n")];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(accepted ? 2 : 1, emitter.Records, "records");
        Assert.HasCount(accepted ? 0 : 1, emitter.Skips, "skips");

        if (accepted)
        {
            Assert.AreEqual(count, RecordingEmitter.Line(emitter.Records[0]).Host.Length, "the host is kept whole");
        }
        else
        {
            Assert.AreEqual((1L, NotSyslog), emitter.Skips[0], "the line is not a syslog line");
        }

        Assert.AreEqual("valid", RecordingEmitter.Line(emitter.Records[^1]).Message, "the next line is emitted");
    }

    /// <summary>
    /// The program of a tag is limited to 128 UTF-8 bytes of the decoded line; beyond it the line has no tag.
    /// </summary>
    /// <param name="invalidBytes">How many invalid bytes begin the program</param>
    /// <param name="letters">How many letters follow them</param>
    /// <param name="tagged">Whether the tag is recognized</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(0, 128, true)]
    [DataRow(0, 129, false)]
    [DataRow(42, 2, true)]
    [DataRow(43, 0, false)]
    [DataRow(128, 0, false)]
    public async Task SyslogParserParseAsyncLimitsTheProgramToDecodedBytes(int invalidBytes, int letters, bool tagged)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var program = Enumerable.Repeat((byte)0xFF, invalidBytes).Concat(Enumerable.Repeat((byte)'a', letters)).ToArray();
        byte[] content = [.. "Mar  1 12:00:00 web-1 "u8, .. program, .. ": text\n"u8, .. Encoding.UTF8.GetBytes(Valid + "\n")];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip and no refusal");
        Assert.HasCount(2, emitter.Records, "both lines are records");

        var payload = RecordingEmitter.Line(emitter.Records[0]);
        var decoded = Encoding.UTF8.GetString(program);

        if (tagged)
        {
            Assert.AreEqual(decoded, payload.Program, "the program is kept whole");
            Assert.AreEqual("text", payload.Message, "message");
        }
        else
        {
            Assert.AreEqual(string.Empty, payload.Program, "no tag, no program");
            Assert.AreEqual($"{decoded}: text", payload.Message, "the message is the rest after the host");
        }

        Assert.AreEqual(0, payload.Pid, "no process ID");
    }

    /// <summary>
    /// Without a time zone, a file of RFC 3339 lines is parsed completely.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncWithoutZoneReadsRfc3339Files()
    {
        // Arrange
        var parser = new SyslogParser(null);
        var file = new LogFile("syslog", null);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, "2026-03-01T12:00:00Z web-1 a: one\ngarbage\n2026-03-01T12:00:01+01:00 web-1 a: two\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, emitter.Records, "both RFC 3339 lines");
        Assert.AreSequenceEqual<(long, string)>([(2, NotSyslog)], emitter.Skips, "the line that is not a syslog line");
    }

    /// <summary>
    /// Without a time zone, the first year-less line fails the file after the lines before it were emitted, and it is neither emitted nor skipped.
    /// </summary>
    /// <param name="withAnchor">Whether the file has a modification time</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SyslogParserParseAsyncWithoutZoneFailsAtTheFirstYearLessLine(bool withAnchor)
    {
        // Arrange
        var parser = new SyslogParser(null);
        var file = new LogFile("syslog", withAnchor ? new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero) : null);
        var emitter = new RecordingEmitter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("2026-03-01T11:59:58Z web-1 a: one\ngarbage\n2026-03-01T11:59:59Z web-1 a: two\nMar  1 12:00:00 web-1 a: year-less\n2026-03-01T12:00:01Z web-1 a: after\n"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(file, input, emitter, TestContext.CancellationToken), "the parse fails");

        // Assert
        Assert.AreEqual("import.time_zone is not set", exception.Message, "fixed message without input text");
        Assert.HasCount(2, emitter.Records, "the lines before it are emitted");
        Assert.AreSequenceEqual<(long, string)>([(2, NotSyslog)], emitter.Skips, "only the line that is not a syslog line is skipped");
    }

    /// <summary>
    /// A failure for the unset zone does not flush an open kernel report, so that a later complete run emits the same records first.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncWithoutZoneDoesNotFlushAnOpenReportAndTheNextRunContinuesIt()
    {
        // Arrange
        var text = "2026-03-01T12:00:00Z web-1 kernel: [1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca, order=0\n"
                   + "2026-03-01T12:00:01Z web-1 sshd[5]: Accepted publickey for root\n"
                   + "Mar  1 12:00:02 web-1 kernel: [1.1] year-less line\n";
        var file = new LogFile("syslog", new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
        var failed = new RecordingEmitter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));

        // Act
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new SyslogParser(null).ParseAsync(file, input, failed, TestContext.CancellationToken), "the first run fails");

        var second = await RecordingEmitter.ParseAsync(new SyslogParser(DateTimeZone.Utc), file, text, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, failed.Records, "exactly the sshd record is emitted before the failure");
        Assert.AreEqual("Accepted publickey for root", RecordingEmitter.Line(failed.Records[0]).Message, "the sshd record");
        Assert.IsGreaterThanOrEqualTo(2, second.Records.Count, "the complete run emits more");
        Assert.AreEqual(RecordingEmitter.Describe(failed.Records[0]), RecordingEmitter.Describe(second.Records[0]), "the first record of the complete run is the one the failed run emitted");
    }

    /// <summary>
    /// The fixture with a real OOM report gives the report as one record between the lines before and after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncKeepsTheOomReportOfTheFixtureTogether()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/logs/kern.log-oom"), TestContext.CancellationToken);
        var file = new LogFile("var/log/kern.log-oom", new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(3, emitter.Records, "the line before, the report and the line after");
        Assert.AreEqual(KernelReportSamples.Before, RecordingEmitter.Line(emitter.Records[0]).Message, "the line before");

        var report = RecordingEmitter.Line(emitter.Records[1]);

        Assert.AreEqual("kernel", report.Program, "program");
        Assert.AreEqual(0, report.Pid, "process ID");
        Assert.AreEqual("web-1", report.Host, "host");
        Assert.AreEqual("var/log/kern.log-oom", report.Log, "log");
        Assert.AreEqual(string.Join('\n', KernelReportSamples.Oom), report.Message, "the member messages joined by line feeds in file order");
        Assert.IsFalse(report.Truncated, "not truncated");
        Assert.IsNull(report.Priority, "no priority in the file");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 12, 30, 15, TimeSpan.Zero), emitter.Records[1].CapturedAt, "the time of the first line of the report");
        Assert.AreEqual(KernelReportSamples.After, RecordingEmitter.Line(emitter.Records[2]).Message, "the line after");
    }

    /// <summary>
    /// A warning report from the cut line to the end trace line is one record, and a report open at the end of the input is emitted.
    /// </summary>
    /// <param name="withEnd">Whether the report has its end line</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SyslogParserParseAsyncGroupsWarningReportAndFlushesItAtTheEndOfTheInput(bool withEnd)
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var members = withEnd ? KernelReportSamples.CutHere : KernelReportSamples.CutHere[..^1];
        var text = string.Concat(members.Select(message => $"2026-03-01T12:00:00+01:00 web-1 kernel: {message}\n"));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(string.Join('\n', members), RecordingEmitter.Line(emitter.Records[0]).Message, "the members joined by line feeds");
    }

    /// <summary>
    /// A report whose members hold invalid bytes is cut to the limit in UTF-8 bytes and not refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncCutsReportWithInvalidBytesToTheLimitInUtf8Bytes()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var content = new List<byte>();

        content.AddRange(Encoding.UTF8.GetBytes("2026-03-01T12:00:00Z web-1 kernel: [1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca\n"));

        for (var line = 0; line < 100; line++)
        {
            content.AddRange(Encoding.UTF8.GetBytes("2026-03-01T12:00:00Z web-1 kernel: "));
            content.AddRange(Enumerable.Repeat((byte)0xFF, 200));
            content.Add((byte)'\n');
        }

        content.AddRange(Encoding.UTF8.GetBytes("2026-03-01T12:00:01Z web-1 kernel: [1.1] Out of memory: Killed process 4242 (mariadbd)\n"));

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, [.. content], TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record is refused");
        Assert.HasCount(1, emitter.Records, "one report record");

        var report = RecordingEmitter.Line(emitter.Records[0]);

        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(report.Message), "message within the limit in UTF-8 bytes");
        Assert.IsTrue(report.Truncated, "truncated");
    }

    /// <summary>
    /// Parsing the same content twice gives the same records in the same order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncIsDeterministic()
    {
        // Arrange
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/logs/kern.log-oom"), TestContext.CancellationToken);
        byte[] more = [.. content, .. "Mar  1 12:31:00 web-1 sshd[9]: after\nMar  1 12:31:01 web-1 sshd[9]: more\n"u8];
        var parser = new SyslogParser(DateTimeZoneProviders.Tzdb["Europe/Berlin"]);

        // Act
        var first = await RecordingEmitter.ParseAsync(parser, _file, more, TestContext.CancellationToken);
        var second = await RecordingEmitter.ParseAsync(parser, _file, more, TestContext.CancellationToken);

        // Assert
        Assert.IsNotEmpty(first.Records, "records were emitted");
        Assert.AreSequenceEqual(first.Records.Select(RecordingEmitter.Describe).ToList(), second.Records.Select(RecordingEmitter.Describe).ToList(), "same records in the same order");
        Assert.AreSequenceEqual(first.Skips, second.Skips, "same skips");
    }

    /// <summary>
    /// A cancelled token ends the parse with an operation canceled exception before anything is emitted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncHonorsACancelledToken()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var emitter = new RecordingEmitter();
        using var cancelled = new CancellationTokenSource();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Valid + "\n"));

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => parser.ParseAsync(_file, input, emitter, cancelled.Token), "cancelled parse");
        Assert.AreEqual(0, emitter.Calls, "nothing was emitted");
    }

    /// <summary>
    /// Cancelling while a report is open ends the parse without flushing the report and without another call of the emitter.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncDoesNotFlushAnOpenReportWhenCancelled()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        using var cancel = new CancellationTokenSource();
        var emitter = new RecordingEmitter
                      {
                          Observed = _ => cancel.Cancel()
                      };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(OpenReport()));

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => parser.ParseAsync(_file, input, emitter, cancel.Token), "cancelled parse");

        // Assert
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
        Assert.HasCount(1, emitter.Records, "only the line written inside the report");
    }

    /// <summary>
    /// An exception of the emitter ends the parse with that exception, without flushing an open report and without another call.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SyslogParserParseAsyncPassesOnTheExceptionOfTheEmitter()
    {
        // Arrange
        var parser = new SyslogParser(DateTimeZone.Utc);
        var failure = new InvalidOperationException("store is full");
        var emitter = new RecordingEmitter
                      {
                          Failure = (_, _) => failure
                      };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(OpenReport()));

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the emitter fails");

        // Assert
        Assert.AreSame(failure, thrown, "the exception of the emitter");
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
    }

    /// <summary>
    /// Describes what the emitter saw in file order: records as their time in UTC, skips as their reason.
    /// </summary>
    /// <param name="emitter">The emitter</param>
    /// <returns>The description, separated by a bar</returns>
    private static string Outcomes(RecordingEmitter emitter)
    {
        // Every line of the examples is a record or a skip, so the skips are placed by their line number among the records.
        var texts = emitter.Records.Select(record => record.CapturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).ToList();
        var result = new List<string>();
        var recordIndex = 0;
        var skipIndex = 0;
        var line = 0L;

        while (recordIndex < texts.Count || skipIndex < emitter.Skips.Count)
        {
            line++;

            if (skipIndex < emitter.Skips.Count && emitter.Skips[skipIndex].Line == line)
            {
                result.Add(emitter.Skips[skipIndex].Reason);
                skipIndex++;
            }
            else
            {
                result.Add(texts[recordIndex]);
                recordIndex++;
            }
        }

        return string.Join('|', result);
    }

    /// <summary>
    /// Returns a file that has an open OOM report with a line of another program written inside it.
    /// </summary>
    /// <returns>The file content</returns>
    private static string OpenReport()
    {
        return "2026-03-01T12:00:00Z web-1 kernel: [1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca, order=0\n"
               + "2026-03-01T12:00:01Z web-1 sshd[5]: Accepted publickey for root\n"
               + "2026-03-01T12:00:02Z web-1 kernel: [1.1] CPU: 1 PID: 4242 Comm: mariadbd\n"
               + "2026-03-01T12:00:03Z web-1 kernel: [1.2] Call Trace:\n";
    }

    #endregion // Methods
}