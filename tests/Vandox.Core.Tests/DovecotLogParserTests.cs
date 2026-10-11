using System.Globalization;
using System.Text;

using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="DovecotLogParser"/>
/// </summary>
[TestClass]
public class DovecotLogParserTests
{
    #region Constants

    private const string Sentinel = "QXZ-7741-SENTINEL";
    private const string NotDovecot = "not a Dovecot line";
    private const string Empty = "empty line";
    private const string Unknown = "year unknown: the file has no usable date";
    private const string Valid = "Oct 09 03:12:13 master: Info: valid";
    private const string FixturePath = "testdata/logs/dovecot.log";
    private const string LoginLine = "Oct 09 03:12:13 imap-login: Info: Login: user=<alice@example.org>, method=PLAIN\n";

    #endregion // Constants

    #region Fields

    private static readonly LogFile _file = new("dovecot.log-20261010", null);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The Dovecot log file is framed by lines only, so an import may continue it at the start of a line.
    /// </summary>
    [TestMethod]
    public void DovecotLogParserIsLineFramed()
    {
        // Act
        var parser = new DovecotLogParser(DateTimeZone.Utc);

        // Assert
        Assert.IsTrue(parser.LineFramed, "line framed");
    }

    /// <summary>
    /// The type of the parser is <c>dovecot</c>.
    /// </summary>
    [TestMethod]
    public void DovecotLogParserTypeIsDovecot()
    {
        // Act
        var parser = new DovecotLogParser(DateTimeZone.Utc);

        // Assert
        Assert.AreEqual("dovecot", parser.Type, "type");
    }

    /// <summary>
    /// A file is claimed by content when its first line is a Dovecot line, under any name that is no syslog name, also when the head ends inside that line after the marker.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head of the file</param>
    /// <param name="expected">The expected confidence</param>
    [TestMethod]
    [DataRow("dovecot.log", LoginLine, Confidence.MatchContent)]
    [DataRow("notes.txt", LoginLine, Confidence.MatchContent)]
    [DataRow("mail/dovecot-info.log.1", LoginLine, Confidence.MatchContent)]
    [DataRow("dovecot.log", LoginLine + "Oct 09 03:12:14 master: Error: x\n", Confidence.MatchContent)]
    [DataRow("dovecot.log", "﻿" + LoginLine, Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 imap-login: Info: Login: user=<a>\r\nOct 09 03:12:14 master: Error: x\r\n", Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 imap-login: Info: Login: no line break in the head", Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 Info: no line break in the head", Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct  9 03:12:13 master: Info: x\n", Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 imap-login: Info: ", Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 Info: ", Confidence.MatchContent)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 imap-login: Info:", Confidence.NoMatch)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 Info:", Confidence.NoMatch)]
    [DataRow("dovecot.log", "Oct 09 03:12:13 imap-login: Login: x\nOct 09 03:12:14 master: Info: x\n", Confidence.NoMatch)]
    public void DovecotLogParserDetectClaimsAFileByItsFirstLine(string name, string head, Confidence expected)
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(expected, confidence, "confidence");
    }

    /// <summary>
    /// A syslog name is never claimed, also not when the first line is a Dovecot line.
    /// </summary>
    /// <param name="name">The file name</param>
    [TestMethod]
    [DataRow("syslog")]
    [DataRow("syslog.1")]
    [DataRow("kern.log")]
    [DataRow("kern.log-20261010")]
    [DataRow("var/log/syslog-20261010")]
    public void DovecotLogParserDetectNeverClaimsASyslogName(string name)
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(LoginLine));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, $"\"{name}\" is a syslog name");
    }

    /// <summary>
    /// A head whose first line is no Dovecot line is not claimed, whatever follows it.
    /// </summary>
    /// <param name="head">The head</param>
    [TestMethod]
    [DataRow("\n" + LoginLine)]
    [DataRow("This is just some prose about a server.\n" + LoginLine)]
    [DataRow("Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root\n")]
    [DataRow("Oct  9 03:12:12 mail-1 dovecot: master: Info: Dovecot v2.3.16 starting up\n")]
    [DataRow("2026-03-01T12:30:15.123456+01:00 web-1 sshd[1234]: Accepted publickey for root\n")]
    [DataRow("__CURSOR=s=0123456789abcdef;i=1;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1;t=1;x=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n")]
    [DataRow("2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed\n")]
    [DataRow("")]
    public void DovecotLogParserDetectIgnoresOtherHeads(string head)
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile("dovecot.log", null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "confidence");
    }

    /// <summary>
    /// A line becomes a record of source <c>dovecot</c> and origin import, with the time stamp read in the zone, the message as written, and empty host, program and process ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncFillsTheFieldsOfARecord()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZoneProviders.Tzdb["Europe/Berlin"]);
        var message = "master: Info: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, $"Oct 09 03:12:12 {message}\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var payload = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual("dovecot", record.Source, "source");
        Assert.AreEqual(0UL, record.Seq, "sequence number");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 1, 12, 12, TimeSpan.Zero), record.CapturedAt, "03:12:12 in Berlin summer time is 01:12:12 UTC");
        Assert.AreEqual(TimeSpan.Zero, record.CapturedAt.Offset, "UTC");
        Assert.AreEqual("dovecot.log-20261010", payload.Log, "log is the file name");
        Assert.AreEqual(string.Empty, payload.Host, "host");
        Assert.AreEqual(string.Empty, payload.Program, "program");
        Assert.AreEqual(0, payload.Pid, "process ID");
        Assert.AreEqual((byte)6, payload.Priority, "priority of Info");
        Assert.AreEqual(string.Empty, payload.Event, "event");
        Assert.AreEqual(message, payload.Message, "message as written");
        Assert.IsFalse(payload.Truncated, "not truncated");
    }

    /// <summary>
    /// The fixture gives eleven records with the priority of their label and the events for the database, panic and fatal lines, in file order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncReadsTheFixture()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZoneProviders.Tzdb["Europe/Berlin"]);
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);
        var lines = Encoding.UTF8.GetString(content).TrimEnd('\n').Split('\n');

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(11, lines, "the fixture has eleven lines");
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(11, emitter.Records, "one record per line");
        Assert.AreEqual("6|6|3|4|3|2|2|7|6|6|6", string.Join('|', emitter.Records.Select(record => RecordingEmitter.Line(record).Priority!.Value.ToString(CultureInfo.InvariantCulture))), "priorities");
        Assert.AreEqual("-|-|dovecot.database_unavailable|dovecot.database_unavailable|-|dovecot.panic|dovecot.fatal|-|-|-|-", string.Join('|', emitter.Records.Select(record => RecordingEmitter.Line(record).Event is { Length: > 0 } name ? name : "-")), "events");

        for (var index = 0; index < lines.Length; index++)
        {
            var record = emitter.Records[index];
            var payload = RecordingEmitter.Line(record);

            Assert.AreEqual("dovecot", record.Source, $"source of line {index + 1}");
            Assert.AreEqual(lines[index][16..], payload.Message, $"message of line {index + 1}");
            Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 1, 12, 12 + index, TimeSpan.Zero), record.CapturedAt, $"time of line {index + 1}");
            Assert.AreEqual(string.Empty, payload.Program, $"program of line {index + 1}");
            Assert.IsFalse(payload.Truncated, $"truncated of line {index + 1}");
        }
    }

    /// <summary>
    /// Parsing the same content twice gives the same records in the same order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncIsDeterministic()
    {
        // Arrange
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);
        var parser = new DovecotLogParser(DateTimeZoneProviders.Tzdb["Europe/Berlin"]);

        // Act
        var first = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);
        var second = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsNotEmpty(first.Records, "records were emitted");
        Assert.AreSequenceEqual(first.Records.Select(RecordingEmitter.Describe).ToList(), second.Records.Select(RecordingEmitter.Describe).ToList(), "same records in the same order");
        Assert.AreSequenceEqual(first.Skips, second.Skips, "same skips");
    }

    /// <summary>
    /// An empty line and a line that is no Dovecot line are skipped with their 1-based line number and the parse continues.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncSkipsLinesThatAreNoDovecotLines()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var text = string.Join('\n',
                               string.Empty,
                               "garbage",
                               "Oct 09 03:12:13 imap-login: Login: x",
                               "Oct  9 03:12:12 mail-1 dovecot: master: Info: x",
                               "Foo 09 03:12:13 Info: x",
                               "Oct 09 24:12:13 Info: x",
                               "2026-10-09T03:12:13Z master: Info: x",
                               "Oct 09 03:12:13 info: x",
                               "Oct 09 03:12:13 a: b: Info: x",
                               "Oct 09 03:12:13",
                               Valid,
                               string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, text, TestContext.CancellationToken);

        // Assert
        var expected = $"1:{Empty}|2:{NotDovecot}|3:{NotDovecot}|4:{NotDovecot}|5:{NotDovecot}|6:{NotDovecot}|7:{NotDovecot}|8:{NotDovecot}|9:{NotDovecot}|10:{NotDovecot}";

        Assert.AreEqual(expected, string.Join('|', emitter.Skips.Select(skip => $"{skip.Line}:{skip.Reason}")), "skips with their line numbers");
        Assert.HasCount(1, emitter.Records, "the valid line is emitted");
        Assert.AreEqual("master: Info: valid", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
    }

    /// <summary>
    /// A time that does not exist, or a year-less time in a file without any date, is skipped with the reason of the syslog parser.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="withDate">Whether the file name carries a date</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Feb 30 03:12:13 master: Info: x", true, "invalid date")]
    [DataRow("Nov 31 03:12:13 Info: x", true, "invalid date")]
    [DataRow("Oct 09 03:12:13 master: Info: x", false, Unknown)]
    public async Task DovecotLogParserParseAsyncSkipsTimesWithTheSyslogReasons(string line, bool withDate, string reason)
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var file = new LogFile(withDate ? "dovecot.log-20261010" : "dovecot.log", null);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, line + "\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Records, "no record");
        Assert.AreSequenceEqual<(long, string)>([(1, reason)], emitter.Skips, "the line is skipped with the reason of the time");
    }

    /// <summary>
    /// A byte order mark is removed from the first line only: there the line is a record, on a later line the mark stays and the line is skipped.
    /// </summary>
    /// <param name="markOnFirstLine">Whether the mark starts the first line; otherwise it starts the second</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DovecotLogParserParseAsyncRemovesTheByteOrderMarkOfLineOne(bool markOnFirstLine)
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var line = "Oct 09 03:12:13 master: Info: hello\n"u8.ToArray();
        byte[] content = markOnFirstLine ? [.. bom, .. line] : [.. "\n"u8, .. bom, .. line];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        if (markOnFirstLine)
        {
            Assert.IsEmpty(emitter.Skips, "nothing is skipped");
            Assert.HasCount(1, emitter.Records, "one record");
            Assert.AreEqual("master: Info: hello", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
        }
        else
        {
            Assert.IsEmpty(emitter.Records, "no record");
            Assert.AreSequenceEqual<(long, string)>([(1, Empty), (2, NotDovecot)], emitter.Skips, "the mark on line 2 is kept and the line is skipped");
        }
    }

    /// <summary>
    /// Invalid bytes that grow when they are decoded are cut to the limit in UTF-8 bytes, the record is marked as truncated and not refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncCutsALongMessageAndMarksItTruncated()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var prefix = "Oct 09 03:12:13 master: Info: "u8.ToArray();
        var line = prefix.Concat(Enumerable.Repeat((byte)0xFF, LogLineReader.MaxLineBytes - prefix.Length)).Append((byte)'\n').ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, line, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record is refused");
        Assert.HasCount(1, emitter.Records, "one record");

        var payload = RecordingEmitter.Line(emitter.Records[0]);

        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(payload.Message), "message within the limit in UTF-8 bytes");
        Assert.IsTrue(payload.Truncated, "truncated");
        Assert.StartsWith("master: Info: ", payload.Message, "the start of the message is kept");
    }

    /// <summary>
    /// A line cut by the line reader is marked as truncated and its message stays within the limit; the next line is not truncated.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncMarksALineCutByTheReaderAsTruncated()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var text = $"Oct 09 03:12:13 master: Info: {new string('a', LogLineReader.MaxLineBytes + 5000)}\n{Valid}\n";

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
    /// A skip reason is one of the fixed texts and never holds a part of the line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncReportsOnlyFixedSkipReasons()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var text = string.Join('\n',
                               string.Empty,
                               Sentinel,
                               $"Oct 09 03:12:13 {Sentinel}: Login: {Sentinel}",
                               $"Oct 09 24:12:13 master: Info: {Sentinel}",
                               $"Feb 30 03:12:13 master: Info: {Sentinel}",
                               $"Oct  9 03:12:12 {Sentinel} dovecot: master: Info: {Sentinel}",
                               string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, text, TestContext.CancellationToken);

        // Assert
        string[] allowed = [Empty, NotDovecot, "invalid date", Unknown];

        Assert.HasCount(6, emitter.Skips, "every line is skipped");
        Assert.IsTrue(emitter.Skips.All(skip => allowed.Contains(skip.Reason)), "every reason is a fixed text");
        Assert.DoesNotContain(Sentinel, string.Join('|', emitter.Skips.Select(skip => skip.Reason)), "no reason holds text of the line");
    }

    /// <summary>
    /// Without a time zone the first Dovecot line fails the file with a fixed message; lines before it that are empty or no Dovecot line are skipped first and nothing is emitted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncWithoutZoneFailsAtTheFirstDovecotLine()
    {
        // Arrange
        var parser = new DovecotLogParser(null);
        var emitter = new RecordingEmitter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"\ngarbage\n{Valid}\n{Valid}\n"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the parse fails");

        // Assert
        Assert.AreEqual("import.time_zone is not set", exception.Message, "fixed message without input text");
        Assert.AreEqual(0, emitter.Calls, "nothing is emitted");
        Assert.AreSequenceEqual<(long, string)>([(1, Empty), (2, NotDovecot)], emitter.Skips, "the lines before the Dovecot line are skipped");
    }

    /// <summary>
    /// A cancelled token ends the parse with an operation canceled exception before anything is emitted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncHonorsACancelledToken()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var emitter = new RecordingEmitter();
        using var cancelled = new CancellationTokenSource();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Valid + "\n"));

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => parser.ParseAsync(_file, input, emitter, cancelled.Token), "cancelled parse");
        Assert.AreEqual(0, emitter.Calls, "nothing was emitted");
    }

    /// <summary>
    /// An exception of the emitter ends the parse with that exception and without another call.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DovecotLogParserParseAsyncPassesOnTheExceptionOfTheEmitter()
    {
        // Arrange
        var parser = new DovecotLogParser(DateTimeZone.Utc);
        var failure = new InvalidOperationException("store is full");
        var emitter = new RecordingEmitter
                      {
                          Failure = (_, _) => failure
                      };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"{Valid}\n{Valid}\n"));

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the emitter fails");

        // Assert
        Assert.AreSame(failure, thrown, "the exception of the emitter");
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
    }

    #endregion // Methods
}