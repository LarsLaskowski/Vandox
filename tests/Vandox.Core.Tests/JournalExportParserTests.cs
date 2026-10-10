using System.Globalization;
using System.Text;

using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="JournalExportParser"/>
/// </summary>
[TestClass]
public class JournalExportParserTests
{
    #region Constants

    private const string Sshd = "__CURSOR=s=0123456789abcdef0123456789abcdef;i=1b2c;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1d5e3a;t=64a1;x=9f8e\n__REALTIME_TIMESTAMP=1772368215123456\n__MONOTONIC_TIMESTAMP=123456789\n_BOOT_ID=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f\n_TRANSPORT=syslog\nPRIORITY=6\nSYSLOG_FACILITY=4\nSYSLOG_IDENTIFIER=sshd\n_UID=0\n_GID=0\n_COMM=sshd\n_EXE=/usr/sbin/sshd\n_PID=1234\n_HOSTNAME=web-1\nMESSAGE=Accepted publickey for root from 192.0.2.7 port 51234 ssh2: ED25519 SHA256:abc\nSYSLOG_PID=1234\n\n";
    private const string Systemd = "__CURSOR=s=0123456789abcdef0123456789abcdef;i=1b2d\n__REALTIME_TIMESTAMP=1772368216000001\n__MONOTONIC_TIMESTAMP=123456790\n_BOOT_ID=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f\nPRIORITY=6\nSYSLOG_FACILITY=3\nCODE_FILE=src/core/job.c\nCODE_LINE=861\nCODE_FUNC=job_log_done_message\nSYSLOG_IDENTIFIER=systemd\n_TRANSPORT=journal\n_PID=1\n_UID=0\n_GID=0\n_COMM=systemd\n_EXE=/usr/lib/systemd/systemd\n_HOSTNAME=web-1\nMESSAGE=Started Daily apt download activities.\n\n";
    private const string Cron = "__CURSOR=s=0123456789abcdef0123456789abcdef;i=1b2e\n__REALTIME_TIMESTAMP=1772368217500000\n_BOOT_ID=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f\n_TRANSPORT=syslog\nPRIORITY=6\nSYSLOG_FACILITY=9\nSYSLOG_IDENTIFIER=CRON\n_COMM=cron\n_PID=2201\n_HOSTNAME=web-1\nMESSAGE=(root) CMD (command -v debian-sa1 > /dev/null && debian-sa1 1 1)\nSYSLOG_PID=2201\n\n";
    private const string Kernel = "__CURSOR=s=0123456789abcdef0123456789abcdef;i=1b2f\n__REALTIME_TIMESTAMP=1772368218000000\n_BOOT_ID=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f\n_TRANSPORT=kernel\nPRIORITY=6\nSYSLOG_FACILITY=0\nSYSLOG_IDENTIFIER=kernel\n_SOURCE_MONOTONIC_TIMESTAMP=123456000\n_HOSTNAME=web-1\nMESSAGE=TCP: request_sock_TCP: Possible SYN flooding on port 80. Sending cookies.\n\n";
    private const string Base = "__REALTIME_TIMESTAMP=1772368215123456\n";
    private const string Truncated = "truncated entry";
    private const string Malformed = "malformed field";
    private const string NoTimestamp = "entry without __REALTIME_TIMESTAMP";
    private const string BadTimestamp = "invalid __REALTIME_TIMESTAMP";
    private const string NoMessage = "entry without MESSAGE";

    #endregion // Constants

    #region Fields

    private static readonly LogFile _file = new("journal.export", null);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The type of the parser is <c>journal</c>.
    /// </summary>
    [TestMethod]
    public void JournalExportParserTypeIsJournal()
    {
        // Act
        var parser = new JournalExportParser();

        // Assert
        Assert.AreEqual("journal", parser.Type, "type");
    }

    /// <summary>
    /// A journal export is recognized by its content, whatever the file is called; anything else is not.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head</param>
    /// <param name="expected">The expected confidence</param>
    [TestMethod]
    [DataRow("journal.export", "__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n", Confidence.MatchContent)]
    [DataRow("syslog", "__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n", Confidence.MatchContent)]
    [DataRow("a.txt", "__CURSOR=s=1;i=1\nPRIORITY=6\n__REALTIME_TIMESTAMP=1772368215123456\n", Confidence.MatchContent)]
    [DataRow("a.txt", "__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n", Confidence.MatchContent)]
    [DataRow("a.txt", "__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456", Confidence.MatchContent)]
    [DataRow("a.txt", "", Confidence.NoMatch)]
    [DataRow("a.txt", "Mar  1 12:00:00 web-1 sshd[1]: hello\n", Confidence.NoMatch)]
    [DataRow("a.txt", "2026-03-01T12:00:00Z web-1 sshd[1]: hello\n", Confidence.NoMatch)]
    [DataRow("a.txt", "LPKSHHRH\u0001\u0000\u0000\u0000", Confidence.NoMatch)]
    [DataRow("a.txt", "LPKSHHRH__CURSOR=s=1\n__REALTIME_TIMESTAMP=1\n", Confidence.NoMatch)]
    [DataRow("a.txt", "x\n__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\n", Confidence.NoMatch)]
    [DataRow("a.txt", " __CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\n", Confidence.NoMatch)]
    [DataRow("a.txt", "__CURSOR=s=1;i=1\nMESSAGE=hello\n\n", Confidence.NoMatch)]
    [DataRow("a.txt", "﻿__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\n", Confidence.NoMatch)]
    public void JournalExportParserDetectRecognizesTheExportByContent(string name, string head, Confidence expected)
    {
        // Arrange
        var parser = new JournalExportParser();

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(expected, confidence, "confidence");
    }

    /// <summary>
    /// A real entry becomes a record with the time, host, program, process ID, priority and message of the entry.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="microseconds">The expected time as microseconds since the Unix epoch</param>
    /// <param name="host">The expected host</param>
    /// <param name="program">The expected program</param>
    /// <param name="pid">The expected process ID</param>
    /// <param name="priority">The expected priority</param>
    /// <param name="message">The expected message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(Sshd, 1772368215123456, "web-1", "sshd", 1234, 6, "Accepted publickey for root from 192.0.2.7 port 51234 ssh2: ED25519 SHA256:abc")]
    [DataRow(Systemd, 1772368216000001, "web-1", "systemd", 1, 6, "Started Daily apt download activities.")]
    [DataRow(Cron, 1772368217500000, "web-1", "CRON", 2201, 6, "(root) CMD (command -v debian-sa1 > /dev/null && debian-sa1 1 1)")]
    [DataRow(Kernel, 1772368218000000, "web-1", "kernel", 0, 6, "TCP: request_sock_TCP: Possible SYN flooding on port 80. Sending cookies.")]
    public async Task JournalExportParserParseAsyncMapsTheEntryToARecord(string entry, long microseconds, string host, string program, int pid, int priority, string message)
    {
        // Arrange
        var parser = new JournalExportParser();
        var file = new LogFile("exports/journal.export", null);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, entry, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var payload = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual("journal", record.Source, "source");
        Assert.AreEqual(0UL, record.Seq, "sequence number");
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddTicks(microseconds * 10), record.CapturedAt, "time");
        Assert.AreEqual(TimeSpan.Zero, record.CapturedAt.Offset, "UTC");
        Assert.AreEqual("journal", payload.Log, "log");
        Assert.AreEqual(host, payload.Host, "host");
        Assert.AreEqual(program, payload.Program, "program");
        Assert.AreEqual(pid, payload.Pid, "process ID");
        Assert.AreEqual((byte)priority, payload.Priority, "priority");
        Assert.AreEqual(message, payload.Message, "message");
        Assert.IsFalse(payload.Truncated, "not truncated");
    }

    /// <summary>
    /// The program is the syslog identifier, else the command, else empty.
    /// </summary>
    /// <param name="fields">Extra fields</param>
    /// <param name="expected">The expected program</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("SYSLOG_IDENTIFIER=a\n_COMM=b\n", "a")]
    [DataRow("_COMM=b\n", "b")]
    [DataRow("", "")]
    public async Task JournalExportParserParseAsyncChoosesTheProgram(string fields, string expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{Base}{fields}MESSAGE=m\n\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(expected, RecordingEmitter.Line(emitter.Records[0]).Program, "program");
    }

    /// <summary>
    /// The process ID is <c>_PID</c>, else <c>SYSLOG_PID</c>, else 0; values that are no process ID are ignored.
    /// </summary>
    /// <param name="fields">Extra fields</param>
    /// <param name="expected">The expected process ID</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("_PID=5\nSYSLOG_PID=6\n", 5)]
    [DataRow("SYSLOG_PID=6\n", 6)]
    [DataRow("_PID=0\nSYSLOG_PID=6\n", 6)]
    [DataRow("_PID=abc\nSYSLOG_PID=6\n", 6)]
    [DataRow("_PID=2147483648\nSYSLOG_PID=6\n", 6)]
    [DataRow("_PID=2147483647\n", 2147483647)]
    [DataRow("_PID=12345678901\nSYSLOG_PID=6\n", 6)]
    [DataRow("_PID=12345678901\n", 0)]
    [DataRow("_PID=-1\n", 0)]
    [DataRow("", 0)]
    public async Task JournalExportParserParseAsyncChoosesTheProcessId(string fields, int expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{Base}{fields}MESSAGE=m\n\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(expected, RecordingEmitter.Line(emitter.Records[0]).Pid, "process ID");
    }

    /// <summary>
    /// The priority is the one digit 0 to 7 of <c>PRIORITY</c>, else none.
    /// </summary>
    /// <param name="fields">Extra fields</param>
    /// <param name="expected">The expected priority, or -1 for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("PRIORITY=0\n", 0)]
    [DataRow("PRIORITY=3\n", 3)]
    [DataRow("PRIORITY=7\n", 7)]
    [DataRow("PRIORITY=8\n", -1)]
    [DataRow("PRIORITY=10\n", -1)]
    [DataRow("PRIORITY=-1\n", -1)]
    [DataRow("PRIORITY=x\n", -1)]
    [DataRow("PRIORITY= 3\n", -1)]
    [DataRow("PRIORITY=\n", -1)]
    [DataRow("", -1)]
    public async Task JournalExportParserParseAsyncReadsThePriority(string fields, int expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{Base}{fields}MESSAGE=m\n\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(expected < 0 ? null : (byte)expected, RecordingEmitter.Line(emitter.Records[0]).Priority, "priority");
    }

    /// <summary>
    /// A binary message with a line feed, NUL and invalid UTF-8 is kept, and a binary field that is not kept is skipped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncKeepsBinaryMessage()
    {
        // Arrange
        byte[] message = [.. "line1\nline2"u8, 0, .. "end"u8, 0xFF, (byte)'x'];
        var input = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456")
                                              .Binary("COREDUMP_DUMP", [1, 2, 3, 10, 0, 255])
                                              .Binary("MESSAGE", message)
                                              .Binary("_HOSTNAME", "web-1"u8.ToArray())
                                              .Text("SYSLOG_IDENTIFIER", "app")
                                              .End()
                                              .ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("line1\nline2\0end�x", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
        Assert.AreEqual("web-1", RecordingEmitter.Line(emitter.Records[0]).Host, "host from the binary field");
        Assert.AreEqual("app", RecordingEmitter.Line(emitter.Records[0]).Program, "program after the skipped field");
    }

    /// <summary>
    /// A text and a binary message keep a carriage return and the spaces around the text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncKeepsTheMessageUnchanged()
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{Base}MESSAGE=  padded \r\n\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("  padded \r", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
    }

    /// <summary>
    /// A kept text over its limit is cut to the limit in UTF-8 bytes at a character boundary and marked as truncated; invalid bytes do not make the record refused.
    /// </summary>
    /// <param name="field">The field</param>
    /// <param name="binary">Whether the field is written as a binary field</param>
    /// <param name="invalid">Whether the bytes are invalid UTF-8 instead of ASCII</param>
    /// <param name="length">The number of bytes</param>
    /// <param name="limit">The limit in UTF-8 bytes</param>
    /// <param name="truncated">Whether the record is expected to be truncated</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("MESSAGE", false, false, 16384, 16384, false)]
    [DataRow("MESSAGE", false, false, 16385, 16384, true)]
    [DataRow("MESSAGE", true, false, 16385, 16384, true)]
    [DataRow("MESSAGE", false, false, 70000, 16384, true)]
    [DataRow("MESSAGE", true, false, 70000, 16384, true)]
    [DataRow("MESSAGE", false, true, 16384, 16384, true)]
    [DataRow("MESSAGE", true, true, 16384, 16384, true)]
    [DataRow("MESSAGE", true, true, 20000, 16384, true)]
    [DataRow("_HOSTNAME", false, false, 1024, 1024, false)]
    [DataRow("_HOSTNAME", false, false, 1025, 1024, true)]
    [DataRow("SYSLOG_IDENTIFIER", false, false, 1025, 1024, true)]
    [DataRow("_COMM", false, false, 1025, 1024, true)]
    [DataRow("_HOSTNAME", false, true, 1024, 1024, true)]
    [DataRow("SYSLOG_IDENTIFIER", false, true, 1024, 1024, true)]
    [DataRow("_COMM", false, true, 1024, 1024, true)]
    [DataRow("_HOSTNAME", true, true, 1024, 1024, true)]
    public async Task JournalExportParserParseAsyncCutsTextsToTheirLimitInUtf8Bytes(string field, bool binary, bool invalid, int length, int limit, bool truncated)
    {
        // Arrange
        var value = Enumerable.Repeat(invalid ? (byte)0xFF : (byte)'a', length).ToArray();
        var builder = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456");

        _ = binary ? builder.Binary(field, value) : builder.Text(field, value);
        _ = field == "MESSAGE" ? builder : builder.Text("MESSAGE", "m");

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, builder.End().ToArray(), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip and no refusal");
        Assert.HasCount(1, emitter.Records, "one record");

        var payload = RecordingEmitter.Line(emitter.Records[0]);
        var text = field switch
                   {
                       "MESSAGE" => payload.Message,
                       "_HOSTNAME" => payload.Host,
                       _ => payload.Program
                   };

        Assert.IsLessThanOrEqualTo(limit, Encoding.UTF8.GetByteCount(text), "at most the limit in UTF-8 bytes");
        Assert.AreEqual(truncated, payload.Truncated, "truncated flag");

        if (invalid || truncated)
        {
            return;
        }

        Assert.AreEqual(length, text.Length, "the text is kept whole");
    }

    /// <summary>
    /// A valid multi-byte character that the raw cut splits is dropped whole, with no replacement character in its place.
    /// </summary>
    /// <param name="binary">Whether the message is a binary field</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JournalExportParserParseAsyncDropsACharacterSplitByTheCut(bool binary)
    {
        // Arrange
        byte[] value = [.. Enumerable.Repeat((byte)'a', 16383), .. "ézzz"u8];
        var builder = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456");

        _ = binary ? builder.Binary("MESSAGE", value) : builder.Text("MESSAGE", value);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, builder.End().ToArray(), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(new string('a', 16383), RecordingEmitter.Line(emitter.Records[0]).Message, "the split character is gone and no replacement character was added");
        Assert.IsTrue(RecordingEmitter.Line(emitter.Records[0]).Truncated, "truncated");
    }

    /// <summary>
    /// A 64 MiB binary field of a name that is not kept is skipped and the next entry is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncSkipsHugeBinaryFieldAndReadsTheNextEntry()
    {
        // Arrange
        const long size = 64 * 1024 * 1024;
        var header = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456").BinaryHeader("HUGE_FIELD", size).ToArray();
        var trailer = Encoding.UTF8.GetBytes($"\nMESSAGE=first\n\n{Base}MESSAGE=second\n\n");
        using var input = new PatternStream(header, "x"u8.ToArray(), size, trailer);
        var emitter = new RecordingEmitter();

        // Act
        await new JournalExportParser().ParseAsync(_file, input, emitter, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.AreSequenceEqual(["first", "second"], emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToList(), "both entries are read");
    }

    /// <summary>
    /// A binary length beyond the remaining input, or of 2^63 or more, skips the entry as cut off and ends the parse without an exception; entries before it are kept.
    /// </summary>
    /// <param name="declared">The declared length</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1000UL)]
    [DataRow(1UL << 62)]
    [DataRow(1UL << 63)]
    [DataRow(ulong.MaxValue)]
    public async Task JournalExportParserParseAsyncEndsAtABinaryLengthBeyondTheInput(ulong declared)
    {
        // Arrange
        var input = new JournalExportBuilder().Raw($"{Base}MESSAGE=before\n\n")
                                              .Text("__REALTIME_TIMESTAMP", "1772368215123457")
                                              .Binary("MESSAGE", declared, new byte[100])
                                              .Raw($"\n{Base}MESSAGE=after\n\n")
                                              .ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(0, Truncated)], emitter.Skips, "the entry is skipped as cut off");
        Assert.AreSequenceEqual(["before"], emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToList(), "only the entry before it");
    }

    /// <summary>
    /// A text value ended by the end of the input instead of a line feed skips its entry as cut off.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncSkipsTextValueEndedByTheEndOfTheInput()
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{Base}MESSAGE=complete\n\n{Base}MESSAGE=cut off", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(0, Truncated)], emitter.Skips, "the second entry is cut off");
        Assert.HasCount(1, emitter.Records, "the first entry is read");
    }

    /// <summary>
    /// An entry with a malformed field name is skipped and parsing resumes after the next empty line.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <param name="accepted">Whether the name is accepted</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("", false)]
    [DataRow("abc", false)]
    [DataRow("A-B", false)]
    [DataRow("A B", false)]
    [DataRow("1ABC", false)]
    [DataRow("Ä", false)]
    [DataRow("A_1", true)]
    [DataRow("__ADDRESS", true)]
    public async Task JournalExportParserParseAsyncSkipsEntryWithMalformedFieldName(string name, bool accepted)
    {
        // Arrange
        var input = $"{Base}{name}=x\nMESSAGE=first\n\n{Base}MESSAGE=second\n\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(accepted ? ["first", "second"] : ["second"], emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToList(), "records");
        Assert.HasCount(accepted ? 0 : 1, emitter.Skips, "skips");

        if (accepted)
        {
            Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        }
        else
        {
            Assert.AreEqual((0L, Malformed), emitter.Skips[0], "the skip reason is fixed");
        }
    }

    /// <summary>
    /// A field name of 65 bytes makes the entry malformed and one of 64 bytes does not.
    /// </summary>
    /// <param name="length">The length of the name</param>
    /// <param name="skips">The expected number of skips</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(64, 0)]
    [DataRow(65, 1)]
    public async Task JournalExportParserParseAsyncLimitsTheFieldNameTo64Bytes(int length, int skips)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{Base}{new string('A', length)}=x\nMESSAGE=m\n\n{Base}MESSAGE=next\n\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(skips, emitter.Skips, "skips");
        Assert.HasCount(2 - skips, emitter.Records, "records");
    }

    /// <summary>
    /// An entry without a time stamp, without a message or with an invalid time stamp is skipped with a fixed reason and never throws.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("MESSAGE=no time\n", NoTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=1772368215123456\nPRIORITY=6\n", NoMessage)]
    [DataRow("__REALTIME_TIMESTAMP=0\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=12a4\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=-1\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=+1\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP= 1\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=100000000000000000000\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=99999999999999999999\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=18446744073709551615\nMESSAGE=m\n", BadTimestamp)]
    [DataRow("__REALTIME_TIMESTAMP=9223372036854776\nMESSAGE=m\n", BadTimestamp)]
    public async Task JournalExportParserParseAsyncSkipsIncompleteEntryWithAFixedReason(string entry, string reason)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"{entry}\n{Base}MESSAGE=next\n\n", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(0, reason)], emitter.Skips, "skip with line 0 and a fixed reason");
        Assert.AreSequenceEqual(["next"], emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToList(), "the next entry is read");
    }

    /// <summary>
    /// The latest storable time stamp is accepted and the first microsecond is a valid time.
    /// </summary>
    /// <param name="microseconds">The time stamp</param>
    /// <param name="expected">The expected time in UTC</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("9223372036854775", "2262-04-11T23:47:16.8547750Z")]
    [DataRow("1", "1970-01-01T00:00:00.0000010Z")]
    [DataRow("00000000000000000001", "1970-01-01T00:00:00.0000010Z")]
    public async Task JournalExportParserParseAsyncAcceptsTheStorableTimeStamps(string microseconds, string expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, $"__REALTIME_TIMESTAMP={microseconds}\nMESSAGE=m\n\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(expected, emitter.Records[0].CapturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), "time");
    }

    /// <summary>
    /// The first value of a repeated field wins, several empty lines are accepted and the last entry needs no closing empty line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncKeepsFirstValueAndAcceptsEmptyLines()
    {
        // Arrange
        var input = $"\n\n{Base}MESSAGE=first\nMESSAGE=second\n__REALTIME_TIMESTAMP=1\n\n\n\n{Base}MESSAGE=last\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.AreSequenceEqual(["first", "last"], emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToList(), "messages");
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddTicks(17723682151234560), emitter.Records[0].CapturedAt, "the first time stamp wins");
    }

    /// <summary>
    /// Every skip reason is one of the fixed texts, reported with line 0.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncReportsOnlyFixedReasons()
    {
        // Arrange
        var input = $"MESSAGE=secret-one\n\n{Base}\n\n__REALTIME_TIMESTAMP=secret-two\nMESSAGE=m\n\n{Base}bad name=secret-three\n\n{Base}OTHER=cut off";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        string[] allowed = [Malformed, Truncated, NoTimestamp, BadTimestamp, NoMessage];

        Assert.IsNotEmpty(emitter.Skips, "there are skips");
        Assert.IsTrue(emitter.Skips.All(skip => skip.Line == 0), "line 0");
        Assert.IsTrue(emitter.Skips.All(skip => allowed.Contains(skip.Reason)), "every reason is one of the fixed texts");
    }

    /// <summary>
    /// The entries of an OOM report are one record with the lowest priority of its members, between the entries before and after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncKeepsTheOomReportTogether()
    {
        // Arrange
        var input = KernelEntries([KernelReportSamples.Before, .. KernelReportSamples.Oom, KernelReportSamples.After], 1772368200000000);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(3, emitter.Records, "the entry before, the report and the entry after");
        Assert.AreEqual(KernelReportSamples.Before, RecordingEmitter.Line(emitter.Records[0]).Message, "the entry before");

        var report = RecordingEmitter.Line(emitter.Records[1]);

        Assert.AreEqual("kernel", report.Program, "program");
        Assert.AreEqual(0, report.Pid, "process ID");
        Assert.AreEqual("web-1", report.Host, "host");
        Assert.AreEqual("journal", report.Log, "log");
        Assert.AreEqual(string.Join('\n', KernelReportSamples.Oom), report.Message, "the member messages joined by line feeds");
        Assert.AreEqual((byte)3, report.Priority, "the lowest priority of the members");
        Assert.IsFalse(report.Truncated, "not truncated");
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddTicks((1772368200000000 + 1000000) * 10), emitter.Records[1].CapturedAt, "the time of the first entry of the report");
        Assert.AreEqual(KernelReportSamples.After, RecordingEmitter.Line(emitter.Records[2]).Message, "the entry after");
    }

    /// <summary>
    /// A report whose members hold invalid bytes is cut to the limit in UTF-8 bytes and not refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncCutsReportWithInvalidBytesToTheLimitInUtf8Bytes()
    {
        // Arrange
        var builder = new JournalExportBuilder();
        var micros = 1772368215000000;

        builder.Entry(micros.ToString(CultureInfo.InvariantCulture), "web-1", "kernel", 0, 4, "[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca");

        for (var index = 1; index <= 100; index++)
        {
            builder.Text("__REALTIME_TIMESTAMP", (micros + index).ToString(CultureInfo.InvariantCulture))
                   .Text("SYSLOG_IDENTIFIER", "kernel")
                   .Text("_HOSTNAME", "web-1")
                   .Text("MESSAGE", Enumerable.Repeat((byte)0xFF, 200).ToArray())
                   .End();
        }

        builder.Entry((micros + 200).ToString(CultureInfo.InvariantCulture), "web-1", "kernel", 0, 3, "[1.1] Out of memory: Killed process 4242 (mariadbd)");

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, builder.ToArray(), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record is refused");
        Assert.HasCount(1, emitter.Records, "one report record");

        var report = RecordingEmitter.Line(emitter.Records[0]);

        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(report.Message), "message within the limit in UTF-8 bytes");
        Assert.IsTrue(report.Truncated, "truncated");
        Assert.AreEqual((byte)3, report.Priority, "lowest priority");
    }

    /// <summary>
    /// Parsing the same content twice gives the same records in the same order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncIsDeterministic()
    {
        // Arrange
        var input = Encoding.UTF8.GetBytes($"{Sshd}{Systemd}{Encoding.UTF8.GetString(KernelEntries(KernelReportSamples.Oom, 1772368300000000))}{Cron}{Kernel}");
        var parser = new JournalExportParser();

        // Act
        var first = await RecordingEmitter.ParseAsync(parser, _file, input, TestContext.CancellationToken);
        var second = await RecordingEmitter.ParseAsync(parser, _file, input, TestContext.CancellationToken);

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
    public async Task JournalExportParserParseAsyncHonorsACancelledToken()
    {
        // Arrange
        var emitter = new RecordingEmitter();
        using var cancelled = new CancellationTokenSource();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Sshd));

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => new JournalExportParser().ParseAsync(_file, input, emitter, cancelled.Token), "cancelled parse");
        Assert.AreEqual(0, emitter.Calls, "nothing was emitted");
    }

    /// <summary>
    /// Cancelling while a report is open ends the parse without flushing the report and without another call of the emitter.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncDoesNotFlushAnOpenReportWhenCancelled()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        var emitter = new RecordingEmitter
                      {
                          Observed = _ => cancel.Cancel()
                      };
        using var input = new MemoryStream(OpenReport());

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => new JournalExportParser().ParseAsync(_file, input, emitter, cancel.Token), "cancelled parse");

        // Assert
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
        Assert.HasCount(1, emitter.Records, "only the entry written inside the report");
    }

    /// <summary>
    /// An exception of the emitter ends the parse with that exception, without flushing an open report and without another call.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncPassesOnTheExceptionOfTheEmitter()
    {
        // Arrange
        var failure = new InvalidOperationException("store is full");
        var emitter = new RecordingEmitter
                      {
                          Failure = (_, _) => failure
                      };
        using var input = new MemoryStream(OpenReport());

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new JournalExportParser().ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the emitter fails");

        // Assert
        Assert.AreSame(failure, thrown, "the exception of the emitter");
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
    }

    /// <summary>
    /// Two entries of <c>mariadbd</c> with the same process form one record with the message after the header prefix, the event, the priority of the level and the fields of the first entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncJoinsAMariaDbHeaderAndItsContinuationEntryIntoOneRecord()
    {
        // Arrange
        var input = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("/usr/sbin/mariadbd: ready for connections."))
                                              .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(1)), "web-1", "mariadbd", 2345, 6, "Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04")
                                              .ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var line = RecordingEmitter.Line(record);

        Assert.AreEqual("/usr/sbin/mariadbd: ready for connections.\nVersion: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04", line.Message, "message");
        Assert.AreEqual(MariaDbEvents.Ready, line.Event, "event");
        Assert.AreEqual((byte?)6, line.Priority, "priority");
        Assert.AreEqual(MariaDbSamples.Start, record.CapturedAt, "the time of the first entry");
        Assert.AreEqual("web-1", line.Host, "host");
        Assert.AreEqual("mariadbd", line.Program, "program");
        Assert.AreEqual(2345, line.Pid, "process ID");
        Assert.AreEqual("journal", line.Log, "log");
        Assert.AreEqual("journal", record.Source, "source");
        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual(0UL, record.Seq, "sequence");
        Assert.IsFalse(line.Truncated, "not truncated");
    }

    /// <summary>
    /// The level of the header decides the priority, whatever <c>PRIORITY</c> says; a form without a level keeps the entry's priority and has no event.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="priority">The <c>PRIORITY</c> of the entry</param>
    /// <param name="expectedPriority">The expected priority</param>
    /// <param name="expectedMessage">The expected message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026-03-01 23:00:05 0 [ERROR] x", 6, 3, "x")]
    [DataRow("2026-03-01 23:00:05 0 [Warning] x", 6, 4, "x")]
    [DataRow("2026-03-01 23:00:05 0 [Note] x", 3, 6, "x")]
    [DataRow("2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure", 5, 5, "InnoDB: Assertion failure")]
    [DataRow("260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql", 5, 5, "Starting mariadbd daemon with databases from /var/lib/mysql")]
    public async Task JournalExportParserParseAsyncTakesThePriorityOfAMariaDbEntryFromTheLevel(string message, int priority, int expectedPriority, string expectedMessage)
    {
        // Arrange
        var input = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, priority, message).ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual((byte?)expectedPriority, RecordingEmitter.Line(emitter.Records[0]).Priority, "priority");
        Assert.AreEqual(expectedMessage, RecordingEmitter.Line(emitter.Records[0]).Message, "the text after the header prefix");
        Assert.AreEqual("mariadbd", RecordingEmitter.Line(emitter.Records[0]).Program, "program");
    }

    /// <summary>
    /// A header-shaped message of another program is stored unchanged, and so is a line of <c>mysqld</c> that is no header; a header of <c>mysqld</c> is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncReadsMysqldAndLeavesOtherProgramsAlone()
    {
        // Arrange
        var input = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "sshd", 5, 6, MariaDbSamples.Note("/usr/sbin/mariadbd: ready for connections."))
                                              .Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mysqld", 7, 6, MariaDbSamples.Note("/usr/sbin/mysqld: ready for connections."))
                                              .Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mysqld", 7, 6, "second line")
                                              .ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, emitter.Records, "two records");
        Assert.AreEqual(MariaDbSamples.Note("/usr/sbin/mariadbd: ready for connections."), RecordingEmitter.Line(emitter.Records[0]).Message, "the line of sshd is unchanged");
        Assert.AreEqual(string.Empty, RecordingEmitter.Line(emitter.Records[0]).Event, "no event for sshd");
        Assert.AreEqual("/usr/sbin/mysqld: ready for connections.\nsecond line", RecordingEmitter.Line(emitter.Records[1]).Message, "the entry of mysqld");
        Assert.AreEqual(MariaDbEvents.Ready, RecordingEmitter.Line(emitter.Records[1]).Event, "event of mysqld");
    }

    /// <summary>
    /// A binary <c>MESSAGE</c> with a line feed of <c>mariadbd</c> is stored unchanged even when it begins with a header; it neither joins nor ends the open entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncStoresABinaryMessageWithALineFeedUnchanged()
    {
        // Arrange
        var builder = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("/usr/sbin/mariadbd: ready for connections."));

        builder.Text("__REALTIME_TIMESTAMP", MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(1)))
               .Text("SYSLOG_IDENTIFIER", "mariadbd")
               .Text("_PID", "2345")
               .Text("_HOSTNAME", "web-1")
               .Binary("MESSAGE", Encoding.UTF8.GetBytes("2026-03-01 23:00:06 0 [ERROR] first\nsecond"))
               .End();
        builder.Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(2)), "web-1", "mariadbd", 2345, 6, "Version: x");

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, builder.ToArray(), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, emitter.Records, "the binary entry and the entry");
        Assert.AreEqual("2026-03-01 23:00:06 0 [ERROR] first\nsecond", RecordingEmitter.Line(emitter.Records[0]).Message, "the binary entry is unchanged");
        Assert.AreEqual(string.Empty, RecordingEmitter.Line(emitter.Records[0]).Event, "no event");
        Assert.IsNull(RecordingEmitter.Line(emitter.Records[0]).Priority, "its own priority, which is none");
        Assert.AreEqual("/usr/sbin/mariadbd: ready for connections.\nVersion: x", RecordingEmitter.Line(emitter.Records[1]).Message, "the continuation line after it still joins the entry");
    }

    /// <summary>
    /// A line of the process without a header joins up to 60 seconds after the header; one microsecond more ends the entry.
    /// </summary>
    /// <param name="offsetMicroseconds">The time of the line after the header, in microseconds</param>
    /// <param name="joins">Whether the line joins</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(60000000L, true)]
    [DataRow(60000001L, false)]
    public async Task JournalExportParserParseAsyncJoinsALineWithinSixtySecondsOnly(long offsetMicroseconds, bool joins)
    {
        // Arrange
        var input = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("header"))
                                              .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddTicks(offsetMicroseconds * 10)), "web-1", "mariadbd", 2345, 6, "line")
                                              .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(1)), "web-1", "mariadbd", 2345, 6, "following")
                                              .ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);

        // Assert
        var messages = emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToList();

        List<string> expected = joins ? ["header\nline\nfollowing"] : ["header", "line", "following"];

        Assert.AreSequenceEqual(expected, messages, "the records");
    }

    /// <summary>
    /// A journal export with MariaDB lines parses with the journal parser of the built-in list without a time zone, and the time is the real-time stamp of the header entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncNeedsNoTimeZoneForMariaDbLines()
    {
        // Arrange
        var parser = BuiltInParsers.Create(null)[0];
        var input = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, "2026-03-01 23:00:05 0 [Note] /usr/sbin/mariadbd: ready for connections.").ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 22, 0, 5, TimeSpan.Zero).AddTicks(1234560), emitter.Records[0].CapturedAt, "22:00:05.123456Z, not the 23:00:05 of the message");
    }

    /// <summary>
    /// A header whose date does not exist opens an entry and is classified.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026-02-30 12:00:00 0 [Warning] x")]
    [DataRow("2026-03-01 24:00:00 0 [Warning] x")]
    public async Task JournalExportParserParseAsyncOpensAnEntryAtAHeaderWithAnImpossibleDate(string message)
    {
        // Arrange
        var parser = BuiltInParsers.Create(null)[0];
        var input = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, message)
                                              .Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, "continued")
                                              .ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("x\ncontinued", RecordingEmitter.Line(emitter.Records[0]).Message, "classified as a header");
        Assert.AreEqual((byte?)4, RecordingEmitter.Line(emitter.Records[0]).Priority, "the priority of the level");
    }

    /// <summary>
    /// The recovery state belongs to one parse: a started line at the start of the second parse of the same parser has no event.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncSharesNoRecoveryStateBetweenParses()
    {
        // Arrange
        var parser = new JournalExportParser();
        var first = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234")).ToArray();
        var second = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678")).ToArray();

        // Act
        var firstEmitter = await RecordingEmitter.ParseAsync(parser, _file, first, TestContext.CancellationToken);
        var secondEmitter = await RecordingEmitter.ParseAsync(parser, _file, second, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(MariaDbEvents.RecoveryStart, RecordingEmitter.Line(firstEmitter.Records[0]).Event, "the first parse ends with an open recovery");
        Assert.AreEqual(string.Empty, RecordingEmitter.Line(secondEmitter.Records[0]).Event, "the second parse does not know it");
    }

    /// <summary>
    /// An entry without <c>MESSAGE</c> is skipped and neither opens nor ends an entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncDoesNotSeeASkippedEntryInsideAnEntry()
    {
        // Arrange
        var builder = new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("header"));

        builder.Text("__REALTIME_TIMESTAMP", MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(1)))
               .Text("SYSLOG_IDENTIFIER", "mariadbd")
               .Text("_PID", "9999")
               .Text("_HOSTNAME", "web-1")
               .End();
        builder.Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(2)), "web-1", "mariadbd", 2345, 6, "continued");

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, builder.ToArray(), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("header\ncontinued", RecordingEmitter.Line(emitter.Records[0]).Message, "the continuation line joined");
        Assert.HasCount(1, emitter.Skips, "one skip");
        Assert.AreEqual(NoMessage, emitter.Skips[0].Reason, "the skipped entry");
    }

    /// <summary>
    /// Cancelling while an entry is open does not emit the entry; the records emitted so far are a prefix of a complete parse.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncDoesNotEmitAnOpenMariaDbEntryWhenCancelled()
    {
        // Arrange
        var input = OpenEntry();
        var complete = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, input, TestContext.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var emitter = new RecordingEmitter
                      {
                          Observed = _ => cancel.Cancel()
                      };
        using var stream = new MemoryStream(input);

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => new JournalExportParser().ParseAsync(_file, stream, emitter, cancel.Token), "cancelled parse");

        // Assert
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
        Assert.HasCount(1, emitter.Records, "only the line of sshd");
        Assert.AreSequenceEqual(complete.Records.Take(1).Select(RecordingEmitter.Describe).ToList(), emitter.Records.Select(RecordingEmitter.Describe).ToList(), "a prefix of the complete parse");
        Assert.HasCount(2, complete.Records, "the complete parse has the line of sshd and the entry");
    }

    /// <summary>
    /// An exception of the emitter ends the parse with that exception, without emitting the open entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncPassesOnTheExceptionOfTheEmitterWithAnOpenMariaDbEntry()
    {
        // Arrange
        var failure = new InvalidOperationException("store is full");
        var emitter = new RecordingEmitter
                      {
                          Failure = (_, _) => failure
                      };
        using var stream = new MemoryStream(OpenEntry());

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new JournalExportParser().ParseAsync(_file, stream, emitter, TestContext.CancellationToken), "the emitter fails");

        // Assert
        Assert.AreSame(failure, thrown, "the exception of the emitter");
        Assert.AreEqual(1, emitter.Calls, "the open entry is not emitted");
    }

    /// <summary>
    /// Two parses of the same input give equal records in the same order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncIsDeterministicForMariaDbLines()
    {
        // Arrange
        var input = MariaDbSamples.FixtureJournal(false);
        var parser = new JournalExportParser();

        // Act
        var first = await RecordingEmitter.ParseAsync(parser, _file, input, TestContext.CancellationToken);
        var second = await RecordingEmitter.ParseAsync(parser, _file, input, TestContext.CancellationToken);

        // Assert
        Assert.IsNotEmpty(first.Records, "records were emitted");
        Assert.AreSequenceEqual(first.Records.Select(RecordingEmitter.Describe).ToList(), second.Records.Select(RecordingEmitter.Describe).ToList(), "same records in the same order");
    }

    /// <summary>
    /// The fixture of the error log, fed as journal entries with its empty lines, gives the messages, events and times of the error log parser, and the priority of the entry for a header without a level.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncGivesTheEntriesOfTheErrorLogFixture()
    {
        // Arrange
        var expected = await ParseErrorLogFixtureAsync();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, MariaDbSamples.FixtureJournal(false), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(22, emitter.Records, "22 entries");
        Assert.HasCount(22, expected.Records, "the error log parser reads 22 entries");

        for (var index = 0; index < expected.Records.Count; index++)
        {
            var want = RecordingEmitter.Line(expected.Records[index]);
            var got = RecordingEmitter.Line(emitter.Records[index]);

            Assert.AreEqual(want.Message, got.Message, $"message of entry {index}");
            Assert.AreEqual(want.Event, got.Event, $"event of entry {index}");
            Assert.AreEqual(expected.Records[index].CapturedAt, emitter.Records[index].CapturedAt, $"time of entry {index}");
            Assert.AreEqual((byte?)(want.Priority ?? (byte)6), got.Priority, $"priority of entry {index}: the level, else the priority of the entry");
        }
    }

    /// <summary>
    /// The fixture without its empty lines, as journald stores the output, gives the same entries and events, and each message equals the error log's without its empty lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncGivesTheEntriesOfTheErrorLogFixtureWithoutEmptyLines()
    {
        // Arrange
        var expected = await ParseErrorLogFixtureAsync();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, MariaDbSamples.FixtureJournal(true), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(22, emitter.Records, "22 entries");

        for (var index = 0; index < expected.Records.Count; index++)
        {
            var want = RecordingEmitter.Line(expected.Records[index]);
            var got = RecordingEmitter.Line(emitter.Records[index]);

            Assert.AreEqual(MariaDbSamples.WithoutEmptyLines(want.Message), got.Message, $"message of entry {index}");
            Assert.AreEqual(want.Event, got.Event, $"event of entry {index}");
        }

        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[17]).Message.Contains("\n\n", StringComparison.Ordinal), "the crash report lacks the lines 28, 30 and 50");
    }

    /// <summary>
    /// Entries that carry the process ID of the server in <c>SYSLOG_PID</c> only and their own <c>_PID</c> are stored unchanged before the server's entry, and the entry holds the server's lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportParserParseAsyncKeysEntriesByPidNotBySyslogPid()
    {
        // Arrange
        var at = new DateTimeOffset(2026, 3, 2, 10, 10, 10, TimeSpan.Zero);
        var builder = new JournalExportBuilder().Entry(MariaDbSamples.Micros(at), "web-1", "mariadbd", 2345, 6, "260302 10:10:10 [ERROR] mysqld got signal 6 ;");
        var injected = new string('A', 1000);

        for (var index = 0; index < 20; index++)
        {
            MariaDbSamples.EntryWithSyslogPid(builder, at.AddSeconds(1), 9999, 2345, injected);
        }

        builder.Entry(MariaDbSamples.Micros(at.AddSeconds(2)), "web-1", "mariadbd", 2345, 6, "Query (0x7f3a2c0d9e10): SELECT 1")
               .Entry(MariaDbSamples.Micros(at.AddSeconds(2)), "web-1", "mariadbd", 2345, 6, "Connection ID (thread ID): 42");

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new JournalExportParser(), _file, builder.ToArray(), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(21, emitter.Records, "20 injected entries and the server's entry");

        for (var index = 0; index < 20; index++)
        {
            var line = RecordingEmitter.Line(emitter.Records[index]);

            Assert.AreEqual(injected, line.Message, $"injected entry {index} is unchanged");
            Assert.AreEqual(9999, line.Pid, $"injected entry {index} has the _PID");
            Assert.AreEqual(string.Empty, line.Event, $"injected entry {index} has no event");
        }

        var entry = RecordingEmitter.Line(emitter.Records[20]);

        Assert.AreEqual("mysqld got signal 6 ;\nQuery (0x7f3a2c0d9e10): SELECT 1\nConnection ID (thread ID): 42", entry.Message, "the entry holds the server's lines only");
        Assert.AreEqual(MariaDbEvents.Abort, entry.Event, "event");
        Assert.AreEqual(2345, entry.Pid, "process ID");
        Assert.AreEqual((byte?)3, entry.Priority, "priority");
    }

    /// <summary>
    /// Returns a journal export with an open MariaDB entry, a line of sshd written inside it, and more lines of the entry's process.
    /// </summary>
    /// <returns>The bytes</returns>
    private static byte[] OpenEntry()
    {
        return new JournalExportBuilder().Entry(MariaDbSamples.Micros(MariaDbSamples.Start), "web-1", "mariadbd", 2345, 6, MariaDbSamples.Note("header"))
                                         .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(1)), "web-1", "sshd", 5, 6, "Accepted publickey for root")
                                         .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(2)), "web-1", "mariadbd", 2345, 6, "continued")
                                         .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(3)), "web-1", "mariadbd", 2345, 6, "continued again")
                                         .Entry(MariaDbSamples.Micros(MariaDbSamples.Start.AddSeconds(4)), "web-1", "mariadbd", 2345, 6, "and again")
                                         .ToArray();
    }

    /// <summary>
    /// Builds journal entries of the kernel with consecutive time stamps; the first message gets priority 4 when it starts a report, the kill line priority 3, all others 6.
    /// </summary>
    /// <param name="messages">The messages</param>
    /// <param name="start">The time stamp of the first entry in microseconds</param>
    /// <returns>The bytes</returns>
    private static byte[] KernelEntries(IEnumerable<string> messages, long start)
    {
        var builder = new JournalExportBuilder();
        var index = 0;

        foreach (var message in messages)
        {
            var priority = 6;

            if (message.Contains("invoked oom-killer", StringComparison.Ordinal))
            {
                priority = 4;
            }
            else if (message.Contains("Killed process", StringComparison.Ordinal))
            {
                priority = 3;
            }

            builder.Entry((start + (index * 1000000)).ToString(CultureInfo.InvariantCulture), "web-1", "kernel", 0, priority, message);
            index++;
        }

        return builder.ToArray();
    }

    /// <summary>
    /// Returns a journal export that has an open OOM report with an entry of another program written inside it.
    /// </summary>
    /// <returns>The bytes</returns>
    private static byte[] OpenReport()
    {
        return new JournalExportBuilder().Entry("1772368215000000", "web-1", "kernel", 0, 4, "[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca")
                                         .Entry("1772368216000000", "web-1", "sshd", 5, 6, "Accepted publickey for root")
                                         .Entry("1772368217000000", "web-1", "kernel", 0, 6, "[1.1] CPU: 1 PID: 4242 Comm: mariadbd")
                                         .Entry("1772368218000000", "web-1", "kernel", 0, 6, "[1.2] Call Trace:")
                                         .ToArray();
    }

    /// <summary>
    /// Parses the error log fixture with the error log parser in UTC.
    /// </summary>
    /// <returns>A task that returns the emitter</returns>
    private async Task<RecordingEmitter> ParseErrorLogFixtureAsync()
    {
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(MariaDbSamples.FixturePath), TestContext.CancellationToken);

        return await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), new LogFile("mysql/error.log", null), content, TestContext.CancellationToken);
    }

    #endregion // Methods
}