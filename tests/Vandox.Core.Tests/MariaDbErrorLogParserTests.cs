using System.Globalization;
using System.Reflection;
using System.Text;

using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MariaDbErrorLogParser"/>
/// </summary>
[TestClass]
public class MariaDbErrorLogParserTests
{
    #region Constants

    private const string FixturePath = "testdata/logs/mariadb-error.log";
    private const string EmptyLine = "empty line";
    private const string BeforeFirst = "line before the first entry";
    private const string InvalidDate = "invalid date";
    private const string Outside = "time outside the storable range";
    private const string FormA = "2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed at 260301 12:30:15";
    private const string FormB = "2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836";
    private const string FormC = "260302 10:10:10 [ERROR] mysqld got signal 6 ;";
    private const string FormD = "260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql";
    private const string Ready = "2026-03-02 10:20:01 0 [Note] /usr/sbin/mariadbd: ready for connections.";
    private const int Mebibyte = 1024 * 1024;
    private const int AllocationBound = 8 * Mebibyte;

    #endregion // Constants

    #region Fields

    private static readonly LogFile _file = new("mysql/error.log", null);

    /// <summary>
    /// The 1-based numbers of the lines of the fixture that are entry headers, in file order.
    /// </summary>
    private static readonly int[] _fixtureHeaders = [1, 2, 3, 4, 5, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 19, 20, 24, 53, 54, 55, 56];

    /// <summary>
    /// The events of the records of the fixture, in file order; a dash is none.
    /// </summary>
    private static readonly string[] _fixtureEvents = "mariadb.shutdown|-|-|-|mariadb.shutdown_complete|mariadb.start|-|mariadb.ready|mariadb.start|-|mariadb.recovery_start|-|-|mariadb.recovery_end|mariadb.ready|-|-|mariadb.abort|mariadb.start|mariadb.recovery_start|mariadb.recovery_end|mariadb.ready".Split('|');

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the lines that are not headers, taken from the rows of the header tests.
    /// </summary>
    /// <returns>The lines, one row each</returns>
    public static IEnumerable<object[]> NotHeaderLines()
    {
        var method = typeof(MariaDbLineTests).GetMethod(nameof(MariaDbLineTests.MariaDbLineTryParseReturnsNullForAContinuationLine))!;

        return method.GetCustomAttributes<DataRowAttribute>().Select(row => new object[] { (string)row.Data[0]! });
    }

    /// <summary>
    /// The type of the parser is <c>mariadb</c>.
    /// </summary>
    [TestMethod]
    public void MariaDbErrorLogParserTypeIsMariaDb()
    {
        // Act
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);

        // Assert
        Assert.AreEqual("mariadb", parser.Type, "type");
    }

    /// <summary>
    /// An entry becomes a record of origin import with the fields of the header.
    /// </summary>
    /// <param name="line">The header line</param>
    /// <param name="time">The expected time in UTC</param>
    /// <param name="program">The expected program</param>
    /// <param name="priority">The expected priority, or -1 for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(FormA, "2026-03-01T12:30:15Z", "", 6)]
    [DataRow("2026-03-01 12:30:15 3 [Warning] Aborted connection", "2026-03-01T12:30:15Z", "", 4)]
    [DataRow("2026-03-01 12:30:15 3 [ERROR] Master 'backup': Slave I/O: error connecting to master", "2026-03-01T12:30:15Z", "", 3)]
    [DataRow(FormB, "2026-03-02T10:10:10Z", "", -1)]
    [DataRow(FormC, "2026-03-02T10:10:10Z", "", 3)]
    [DataRow(FormD, "2026-03-01T12:00:00Z", "mysqld_safe", -1)]
    public async Task MariaDbErrorLogParserParseAsyncFillsTheFieldsOfARecord(string line, string time, string program, int priority)
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);
        var file = new LogFile("backup/var/log/mysql/error.log.1", null);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, file, line + "\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.IsEmpty(emitter.Skips, "no skip");

        var record = emitter.Records[0];
        var logLine = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual("mariadb", record.Source, "source");
        Assert.AreEqual(0UL, record.Seq, "seq");
        Assert.AreEqual(DateTimeOffset.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), record.CapturedAt, "captured at");
        Assert.AreEqual("backup/var/log/mysql/error.log.1", logLine.Log, "log keeps the name");
        Assert.AreEqual(string.Empty, logLine.Host, "host");
        Assert.AreEqual(program, logLine.Program, "program");
        Assert.AreEqual(0, logLine.Pid, "pid");
        Assert.AreEqual(priority < 0 ? null : (byte?)priority, logLine.Priority, "priority");
        Assert.AreEqual(string.Empty, logLine.Event, "no event for these messages");
        Assert.IsFalse(logLine.Truncated, "not truncated");
    }

    /// <summary>
    /// An empty file gives no record and no skip.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncOfAnEmptyFileEmitsNothing()
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, string.Empty, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Records, "records");
        Assert.IsEmpty(emitter.Skips, "skips");
    }

    /// <summary>
    /// Line ends of Windows and a missing final line break change nothing.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026-03-01 12:00:00 0 [Note] h\r\nsecond\r\n2026-03-01 12:00:01 0 [Note] z\r\n")]
    [DataRow("2026-03-01 12:00:00 0 [Note] h\nsecond\n2026-03-01 12:00:01 0 [Note] z")]
    public async Task MariaDbErrorLogParserParseAsyncReadsEveryLineEnd(string text)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, emitter.Records, "two entries");
        Assert.AreEqual("h\nsecond", RecordingEmitter.Line(emitter.Records[0]).Message, "first message");
        Assert.AreEqual("z", RecordingEmitter.Line(emitter.Records[1]).Message, "last entry is emitted at the end of the input");
        Assert.IsEmpty(emitter.Skips, "no skip");
    }

    /// <summary>
    /// The fixture gives 22 records without a skip, with the events, priorities and messages of its entries.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncReadsTheFixtureIntoItsEntries()
    {
        // Arrange
        var lines = await File.ReadAllLinesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);

        // Act
        var emitter = await ParseFixtureAsync(DateTimeZone.Utc);

        // Assert
        Assert.HasCount(57, lines, "the fixture has 57 lines");
        Assert.HasCount(22, emitter.Records, "22 records");
        Assert.IsEmpty(emitter.Skips, "no skip");

        for (var index = 0; index < emitter.Records.Count; index++)
        {
            var logLine = RecordingEmitter.Line(emitter.Records[index]);
            var header = _fixtureHeaders[index];

            Assert.AreEqual(_fixtureEvents[index] == "-" ? string.Empty : _fixtureEvents[index], logLine.Event, $"event of the record of line {header}");
            Assert.AreEqual(HeaderMessage(lines[header - 1]), logLine.Message.Split('\n')[0], $"first line of the record of line {header}");
            Assert.AreEqual("mysql/error.log", logLine.Log, $"log of the record of line {header}");
        }

        var priorities = emitter.Records.Select(record => RecordingEmitter.Line(record).Priority).ToArray();

        Assert.AreEqual((byte?)4, priorities[15], "the warning of line 19");
        Assert.IsNull(priorities[16], "the InnoDB assertion of line 20 has no level");
        Assert.AreEqual((byte?)3, priorities[17], "the signal of line 24");
        Assert.IsTrue(priorities.Where((priority, index) => index is not 15 and not 16 and not 17).All(priority => priority == 6), "every other record is a note");
    }

    /// <summary>
    /// Entries of the fixture hold the continuation lines up to the next header, without trailing empty lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncJoinsTheContinuationLinesOfTheFixture()
    {
        // Arrange
        var lines = await File.ReadAllLinesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);

        // Act
        var emitter = await ParseFixtureAsync(DateTimeZone.Utc);

        // Assert
        var messages = emitter.Records.Select(record => RecordingEmitter.Line(record).Message).ToArray();

        Assert.AreEqual("/usr/sbin/mariadbd: Shutdown complete", messages[4], "line 6 is empty and dropped");
        Assert.AreEqual($"{HeaderMessage(lines[8])}\n{lines[9]}", messages[7], "ready for connections and the version line");
        Assert.AreEqual($"{HeaderMessage(lines[16])}\n{lines[17]}", messages[14], "ready for connections and the version line after the recovery");
        Assert.AreEqual(HeaderMessage(lines[18]), messages[15], "the warning has no continuation line");
        Assert.AreEqual($"{HeaderMessage(lines[19])}\n{string.Join('\n', lines[20..23])}", messages[16], "the assertion and its three lines");
        Assert.AreEqual($"{HeaderMessage(lines[55])}\n{lines[56]}", messages[21], "the last entry ends with the end of the input");

        var signal = $"mysqld got signal 6 ;\n{string.Join('\n', lines[24..51])}";

        Assert.AreEqual(signal, messages[17], "the signal and the crash report up to the kernel version, the empty line 52 dropped");
        Assert.EndsWith("Kernel version: Linux version 5.15.0-91-generic (buildd@lcy02-amd64-010) #101-Ubuntu SMP Tue Nov 14 13:30:08 UTC 2023", messages[17], "last line of the report");
        Assert.AreEqual(3, messages[17].Split('\n').Count(line => line.Length == 0), "the empty lines 28, 30 and 50 are kept");
        Assert.IsLessThan(messages[17].IndexOf("??:0(handle_fatal_signal)", StringComparison.Ordinal), messages[17].IndexOf("??:0(my_print_stacktrace)", StringComparison.Ordinal), "stack frames in order");
        Assert.IsLessThan(messages[17].IndexOf("libc_sigaction.c:0(__restore_rt)", StringComparison.Ordinal), messages[17].IndexOf("??:0(handle_fatal_signal)", StringComparison.Ordinal), "stack frames in order, second and third");
        Assert.IsLessThan(messages[17].IndexOf("??:0(abort)", StringComparison.Ordinal), messages[17].IndexOf("libc_sigaction.c:0(__restore_rt)", StringComparison.Ordinal), "stack frames in order, third and fourth");
        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[17]).Truncated, "the crash report fits");
    }

    /// <summary>
    /// The times of the fixture are read as local times of the zone, with the padded hour and the date of the signal handler.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncResolvesTheTimesOfTheFixture()
    {
        // Act
        var emitter = await ParseFixtureAsync(DateTimeZone.Utc);

        // Assert
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 23, 0, 1, TimeSpan.Zero), emitter.Records[0].CapturedAt, "line 1");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 3, 12, 40, TimeSpan.Zero), emitter.Records[8].CapturedAt, "line 11 with the padded hour");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 10, 10, 10, TimeSpan.Zero), emitter.Records[16].CapturedAt, "line 20 of the assertion");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 10, 10, 10, TimeSpan.Zero), emitter.Records[17].CapturedAt, "line 24 of the signal handler");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 10, 10, 16, TimeSpan.Zero), emitter.Records[21].CapturedAt, "line 56");
    }

    /// <summary>
    /// Parsing the same file twice gives the same records.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncIsDeterministic()
    {
        // Act
        var first = await ParseFixtureAsync(DateTimeZone.Utc);
        var second = await ParseFixtureAsync(DateTimeZone.Utc);

        // Assert
        Assert.HasCount(22, first.Records, "records of the first run");
        Assert.AreSequenceEqual(first.Records.Select(RecordingEmitter.Describe), second.Records.Select(RecordingEmitter.Describe), "records of both runs");
        Assert.AreSequenceEqual(first.Skips, second.Skips, "skips of both runs");
    }

    /// <summary>
    /// Inner empty lines of an entry are kept and trailing ones are dropped.
    /// </summary>
    /// <param name="lines">The continuation lines, separated by a bar</param>
    /// <param name="expected">The expected message, with a bar for a line break</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("|x||", "h||x")]
    [DataRow("x|||y|", "h|x|||y")]
    [DataRow("|", "h")]
    [DataRow("||||", "h")]
    public async Task MariaDbErrorLogParserParseAsyncKeepsInnerEmptyLinesAndDropsTrailingOnes(string lines, string expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, $"2026-03-01 12:00:00 0 [Note] h\n{lines.Replace('|', '\n')}\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(expected.Replace('|', '\n'), RecordingEmitter.Line(emitter.Records[0]).Message, "message");
        Assert.IsEmpty(emitter.Skips, "no skip");
    }

    /// <summary>
    /// Lines before the first entry are skipped with their line number and the reason, and the first header opens the entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncSkipsTheLinesBeforeTheFirstEntry()
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, "\nfoo\n\nbar\n2026-03-01 12:00:00 0 [Note] h\nafter\n", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(1, EmptyLine), (2, BeforeFirst), (3, EmptyLine), (4, BeforeFirst)], emitter.Skips, "skips with their line numbers");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("h\nafter", RecordingEmitter.Line(emitter.Records[0]).Message, "the entry");
    }

    /// <summary>
    /// A UTF-8 byte order mark is removed from line 1, so a header there is a header.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncRemovesTheByteOrderMarkOfLineOne()
    {
        // Arrange
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] h\n")];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("h", RecordingEmitter.Line(emitter.Records[0]).Message, "message");
    }

    /// <summary>
    /// A byte order mark at the start of line 2 makes that line a continuation line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncKeepsAByteOrderMarkOfLineTwoInTheLine()
    {
        // Arrange
        byte[] content = [.. Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] h\n"), 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("2026-03-01 12:00:01 0 [Note] second\n")];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "the line with the mark is no header");
        Assert.Contains("2026-03-01 12:00:01 0 [Note] second", RecordingEmitter.Line(emitter.Records[0]).Message, "the line is part of the message");
    }

    /// <summary>
    /// A byte order mark on line 2 does not open an entry when line 1 was no header either.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncDoesNotOpenAnEntryAtAByteOrderMarkOfLineTwo()
    {
        // Arrange
        byte[] content = [.. Encoding.UTF8.GetBytes("prose\n"), 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("2026-03-01 12:00:01 0 [Note] second\n")];

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Records, "no record");
        Assert.AreSequenceEqual<(long, string)>([(1, BeforeFirst), (2, BeforeFirst)], emitter.Skips, "both lines are before the first entry");
    }

    /// <summary>
    /// An entry of exactly 16,384 UTF-8 bytes is kept whole.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncKeepsAnEntryOfExactlyTheLimit()
    {
        // Arrange
        var line = new string('a', ModelLimits.MaxTextBytes - 2);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, $"2026-03-01 12:00:00 0 [Note] h\n{line}\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record refused");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual($"h\n{line}", RecordingEmitter.Line(emitter.Records[0]).Message, "the whole message");
        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[0]).Truncated, "not truncated");
    }

    /// <summary>
    /// Continuation lines beyond the limit are replaced by a marker with the exact number of omitted lines.
    /// </summary>
    /// <param name="invalidBytes">Whether the lines are bytes that are not UTF-8</param>
    /// <param name="keptLines">The expected number of kept lines</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false, 16)]
    [DataRow(true, 5)]
    public async Task MariaDbErrorLogParserParseAsyncOmitsTheLinesBeyondTheLimit(bool invalidBytes, int keptLines)
    {
        // Arrange
        var raw = new byte[1000];
        var decoded = invalidBytes ? new string('�', 1000) : new string('a', 1000);
        var content = new List<byte>(Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] h\n"));

        Array.Fill(raw, invalidBytes ? (byte)0xFF : (byte)'a');

        for (var index = 0; index < 40; index++)
        {
            content.AddRange(raw);
            content.Add((byte)'\n');
        }

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, [.. content], TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record refused");
        Assert.HasCount(1, emitter.Records, "one record");

        var logLine = RecordingEmitter.Line(emitter.Records[0]);

        Assert.AreEqual($"h{string.Concat(Enumerable.Repeat($"\n{decoded}", keptLines))}\n[{40 - keptLines} lines omitted]", logLine.Message, "header, whole lines, marker");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(logLine.Message), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(logLine.Truncated, "truncated");
    }

    /// <summary>
    /// A header line whose decoded message exceeds the limit is cut at a character boundary, with or without a following line.
    /// </summary>
    /// <param name="withContinuation">Whether a continuation line follows</param>
    /// <param name="expectedCharacters">The expected number of characters of the first line</param>
    /// <param name="expectedEnd">The expected end of the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false, 5461, "")]
    [DataRow(true, 5440, "\n[1 lines omitted]")]
    public async Task MariaDbErrorLogParserParseAsyncCutsALongHeaderLine(bool withContinuation, int expectedCharacters, string expectedEnd)
    {
        // Arrange
        var content = new List<byte>(Encoding.UTF8.GetBytes("2026-03-01 12:00:00 0 [Note] "));

        content.AddRange(Enumerable.Repeat((byte)0xFF, 16000));
        content.AddRange(withContinuation ? "\nx\n"u8.ToArray() : "\n"u8.ToArray());

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, [.. content], TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record refused");
        Assert.HasCount(1, emitter.Records, "one record");

        var logLine = RecordingEmitter.Line(emitter.Records[0]);

        Assert.AreEqual($"{new string('�', expectedCharacters)}{expectedEnd}", logLine.Message, "message");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(logLine.Message), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(logLine.Truncated, "truncated");
    }

    /// <summary>
    /// A header line longer than the line limit is cut by the line reader and the record is truncated.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncMarksALineCutByTheReaderAsTruncated()
    {
        // Arrange
        var text = $"2026-03-01 12:00:00 0 [Note] {new string('a', 17000)}\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record refused");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.IsTrue(RecordingEmitter.Line(emitter.Records[0]).Truncated, "truncated");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(emitter.Records[0]).Message), "at most 16,384 UTF-8 bytes");
    }

    /// <summary>
    /// An entry whose time cannot be stored is skipped once with its header's line number, without its continuation lines.
    /// </summary>
    /// <param name="badHeader">The header line with the bad time</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026-02-30 12:00:00 0 [Note] bad", InvalidDate)]
    [DataRow("0000-01-01 00:00:00 0 [Note] bad", Outside)]
    public async Task MariaDbErrorLogParserParseAsyncSkipsAnEntryWithABadTimeOnce(string badHeader, string reason)
    {
        // Arrange
        var text = $"2026-03-01 12:00:00 0 [Note] one\ncontinuation of one\n{badHeader}\ncontinuation of the bad entry\n\nanother one\n2026-03-01 12:00:02 0 [Note] three\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(3, reason)], emitter.Skips, "one skip, at the header of the bad entry");
        Assert.HasCount(2, emitter.Records, "the entries before and after are emitted");
        Assert.AreEqual("one\ncontinuation of one", RecordingEmitter.Line(emitter.Records[0]).Message, "entry before");
        Assert.AreEqual("three", RecordingEmitter.Line(emitter.Records[1]).Message, "entry after");
    }

    /// <summary>
    /// The start sequence of MariaDB 10.6.7 to 10.6.11 gives the start, the crash recovery and the ready events.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncReadsTheStartSequenceOfMariaDbTenSixSeven()
    {
        // Arrange
        var text = "2026-02-14  8:01:12 0 [Note] /usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 812 ...\n"
                   + "2026-02-14  8:01:12 0 [Note] InnoDB: Starting crash recovery from checkpoint LSN=42540,42540\n"
                   + "2026-02-14  8:01:13 0 [Note] InnoDB: 10.6.7 started; log sequence number 42564; transaction id 14\n"
                   + "2026-02-14  8:01:13 0 [Note] /usr/sbin/mariadbd: ready for connections.\n"
                   + "Version: '10.6.7-MariaDB-2ubuntu1.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.AreSequenceEqual(["mariadb.start", "mariadb.recovery_start", "mariadb.recovery_end", "mariadb.ready"], emitter.Records.Select(record => RecordingEmitter.Line(record).Event), "events");
        Assert.IsTrue(emitter.Records.All(record => RecordingEmitter.Line(record).Priority == 6), "every record is a note");
        Assert.AreEqual(new DateTimeOffset(2026, 2, 14, 8, 1, 12, TimeSpan.Zero), emitter.Records[0].CapturedAt, "time of the start");
        Assert.AreEqual("/usr/sbin/mariadbd: ready for connections.\nVersion: '10.6.7-MariaDB-2ubuntu1.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04", RecordingEmitter.Line(emitter.Records[3]).Message, "ready with the version line");
    }

    /// <summary>
    /// Local times are read in the zone, with daylight saving time and the repeated hour applied.
    /// </summary>
    /// <param name="zone">The zone</param>
    /// <param name="times">The times, separated by a bar</param>
    /// <param name="expected">The expected instants in UTC or <c>skip</c>, separated by a bar</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("UTC", "2026-03-01 12:00:00", "2026-03-01T12:00:00Z")]
    [DataRow("Europe/Berlin", "2026-07-01 12:00:00", "2026-07-01T10:00:00Z")]
    [DataRow("Europe/Berlin", "2026-01-15 12:00:00", "2026-01-15T11:00:00Z")]
    [DataRow("Europe/Berlin", "260115 12:00:00", "2026-01-15T11:00:00Z")]
    [DataRow("Europe/Berlin", "2026-03-29 02:30:00", "2026-03-29T01:30:00Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:30:00", "2026-10-25T00:30:00Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:00:01|2026-10-25 02:30:00", "2026-10-25T00:59:59Z|2026-10-25T01:00:01Z|2026-10-25T01:30:00Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:59:58|2026-10-25 02:00:01", "2026-10-25T00:59:59Z|2026-10-25T00:59:58Z|2026-10-25T01:00:01Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-10-25 02:10:00|2026-10-25 02:09:58", "2026-10-25T00:59:59Z|2026-10-25T01:10:00Z|2026-10-25T01:09:58Z")]
    [DataRow("Europe/Berlin", "2026-10-25 02:59:59|2026-09-31 12:00:00|2026-10-25 02:49:00", "2026-10-25T00:59:59Z|skip|2026-10-25T01:49:00Z")]
    public async Task MariaDbErrorLogParserParseAsyncResolvesLocalTimesInTheZone(string zone, string times, string expected)
    {
        // Arrange
        var lines = times.Split('|');
        var text = string.Concat(lines.Select(time => HeaderLine(time) + "\n"));
        var parser = new MariaDbErrorLogParser(DateTimeZoneProviders.Tzdb[zone]);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, text, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(expected, Outcomes(emitter, lines.Length), "instants in UTC");
    }

    /// <summary>
    /// A time that cannot be stored is skipped with its reason, never thrown, and a time that can is stored.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="expected">The expected reason, or an empty text for a stored time</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026-13-01 12:00:00", InvalidDate)]
    [DataRow("2026-00-10 12:00:00", InvalidDate)]
    [DataRow("2026-01-32 12:00:00", InvalidDate)]
    [DataRow("2026-01-00 12:00:00", InvalidDate)]
    [DataRow("2026-01-10 24:00:00", InvalidDate)]
    [DataRow("2026-01-10 12:60:00", InvalidDate)]
    [DataRow("2026-01-10 12:00:60", InvalidDate)]
    [DataRow("2026-02-29 12:00:00", InvalidDate)]
    [DataRow("2026-04-31 12:00:00", InvalidDate)]
    [DataRow("260230 12:00:00", InvalidDate)]
    [DataRow("2028-02-29 12:00:00", "")]
    [DataRow("0000-01-01 00:00:00", Outside)]
    [DataRow("1676-12-31 23:59:59", Outside)]
    [DataRow("1677-09-21 00:12:43", Outside)]
    [DataRow("2262-04-11 23:47:17", Outside)]
    [DataRow("2263-01-01 00:00:00", Outside)]
    [DataRow("9999-12-31 23:59:59", Outside)]
    [DataRow("1677-09-21 00:12:44", "")]
    [DataRow("2262-04-11 23:47:16", "")]
    public async Task MariaDbErrorLogParserParseAsyncSkipsTimesThatCannotBeStored(string time, string expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, HeaderLine(time) + "\n", TestContext.CancellationToken);

        // Assert
        if (expected.Length == 0)
        {
            Assert.HasCount(1, emitter.Records, "the time is stored");
            Assert.IsEmpty(emitter.Skips, "no skip");
        }
        else
        {
            Assert.IsEmpty(emitter.Records, "no record");
            Assert.AreSequenceEqual<(long, string)>([(1, expected)], emitter.Skips, "the entry is skipped with the reason");
        }
    }

    /// <summary>
    /// Without a zone the lines before the first header are skipped and the first header fails the file, unemitted and unskipped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncWithoutZoneFailsAtTheFirstHeader()
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(null);
        var emitter = new RecordingEmitter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("\ncontinuation\n2026-03-01 12:00:00 0 [Note] h\n2026-03-01 12:00:01 0 [Note] i\n"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the parse fails");

        // Assert
        Assert.AreEqual("import.time_zone is not set", exception.Message, "fixed message without input text");
        Assert.AreEqual(SyslogParser.TimeZoneNotSet, exception.Message, "the message of the syslog parser");
        Assert.AreSequenceEqual<(long, string)>([(1, EmptyLine), (2, BeforeFirst)], emitter.Skips, "only the lines before the first entry are skipped");
        Assert.AreEqual(0, emitter.Calls, "nothing is emitted");
    }

    /// <summary>
    /// Without a zone the zone is checked before the date, so an invalid date as the first header fails the file as well.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncWithoutZoneChecksTheZoneBeforeTheDate()
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(null);
        var emitter = new RecordingEmitter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("2026-02-30 12:00:00 0 [Note] x\n"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the parse fails");

        // Assert
        Assert.AreEqual("import.time_zone is not set", exception.Message, "fixed message");
        Assert.IsEmpty(emitter.Skips, "the header is not skipped");
        Assert.AreEqual(0, emitter.Calls, "nothing is emitted");
    }

    /// <summary>
    /// Without a zone a file without any header is read without failing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncWithoutZoneAcceptsAFileWithoutHeaders()
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(null), _file, "x\n\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Records, "no record");
        Assert.AreSequenceEqual<(long, string)>([(1, BeforeFirst), (2, EmptyLine)], emitter.Skips, "both lines are skipped");
    }

    /// <summary>
    /// A header that is skipped for its time is not classified and leaves the open recovery as it was.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncDoesNotClassifyASkippedHeader()
    {
        // Arrange
        var text = "2026-03-01 12:00:00 0 [Note] InnoDB: Starting crash recovery from checkpoint LSN=1,1\n"
                   + "2026-02-30 12:00:01 0 [Note] Starting MariaDB 10.6.12 source revision  as process 1\n"
                   + "2026-02-30 12:00:02 0 [Note] InnoDB: 10.6.12 started; log sequence number 1; transaction id 2\n"
                   + "2026-03-01 12:00:03 0 [Note] InnoDB: 10.6.12 started; log sequence number 1; transaction id 2\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(2, InvalidDate), (3, InvalidDate)], emitter.Skips, "the two bad headers");
        Assert.AreSequenceEqual(["mariadb.recovery_start", "mariadb.recovery_end"], emitter.Records.Select(record => RecordingEmitter.Line(record).Event), "the recovery was still open at the last line");
    }

    /// <summary>
    /// Only the first line of the header counts for the event.
    /// </summary>
    /// <param name="header">The header line</param>
    /// <param name="continuation">The continuation line</param>
    /// <param name="expected">The expected event</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026-03-01 12:00:00 0 [Note] InnoDB: Buffer pool(s) load completed", "/usr/sbin/mariadbd: ready for connections.", "")]
    [DataRow("2026-03-01 12:00:00 0 [Note] /usr/sbin/mariadbd: ready for connections.", "Starting MariaDB 10.6.12 source revision  as process 1", "mariadb.ready")]
    [DataRow(FormC, "InnoDB: Starting crash recovery", "mariadb.abort")]
    public async Task MariaDbErrorLogParserParseAsyncClassifiesByTheHeaderLineOnly(string header, string continuation, string expected)
    {
        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, $"{header}\n{continuation}\n", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(expected, RecordingEmitter.Line(emitter.Records[0]).Event, "event of the header");
    }

    /// <summary>
    /// A crash report whose query holds a forged header line is split there, and the forged line becomes an entry of its own.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncSplitsACrashReportAtAForgedHeaderLine()
    {
        // Arrange
        var text = $"{FormC}\nQuery (0x7f3a00000000): select 'a\n2026-03-02 10:10:11 0 [Note] /usr/sbin/mariadbd: ready for connections.\nb'\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(2, emitter.Records, "two records");
        Assert.AreEqual("mariadb.abort", RecordingEmitter.Line(emitter.Records[0]).Event, "the crash report");
        Assert.AreEqual("mysqld got signal 6 ;\nQuery (0x7f3a00000000): select 'a", RecordingEmitter.Line(emitter.Records[0]).Message, "the report ends before the forged line");
        Assert.AreEqual("mariadb.ready", RecordingEmitter.Line(emitter.Records[1]).Event, "the forged line");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 10, 10, 11, TimeSpan.Zero), emitter.Records[1].CapturedAt, "the forged time");
    }

    /// <summary>
    /// A user name with line breaks in an access denied warning forges a ready entry, which is read as written.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncSplitsAnAccessDeniedWarningAtAForgedHeaderLine()
    {
        // Arrange
        var text = $"2026-03-02 10:20:00 7 [Warning] Access denied for user 'x\n{Ready}\n'@'203.0.113.5' (using password: NO)\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(2, emitter.Records, "two records");

        var warning = RecordingEmitter.Line(emitter.Records[0]);
        var forged = RecordingEmitter.Line(emitter.Records[1]);

        Assert.AreEqual((byte?)4, warning.Priority, "the warning");
        Assert.AreEqual("Access denied for user 'x", warning.Message, "the warning ends before the forged line");
        Assert.AreEqual(string.Empty, warning.Event, "no event for the warning");
        Assert.AreEqual((byte?)6, forged.Priority, "the forged note");
        Assert.AreEqual("mariadb.ready", forged.Event, "the forged event");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 10, 20, 1, TimeSpan.Zero), emitter.Records[1].CapturedAt, "the forged time");
        Assert.AreEqual("/usr/sbin/mariadbd: ready for connections.\n'@'203.0.113.5' (using password: NO)", forged.Message, "the rest of the user name is the continuation of the forged entry");
    }

    /// <summary>
    /// A file is claimed by its first non-empty line when it is a header and the name is no syslog name.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head of the file</param>
    /// <param name="expected">The expected confidence</param>
    [TestMethod]
    [DataRow("notes.txt", FormA, Confidence.MatchContent)]
    [DataRow("notes.txt", FormB, Confidence.MatchContent)]
    [DataRow("notes.txt", FormC, Confidence.MatchContent)]
    [DataRow("notes.txt", FormD, Confidence.MatchContent)]
    [DataRow("mysql/error.log", FormA + "\n", Confidence.MatchContent)]
    [DataRow("web-1.err", FormA + "\n", Confidence.MatchContent)]
    [DataRow("error.log.1", FormA + "\n", Confidence.MatchContent)]
    [DataRow("syslog.err", FormA + "\n", Confidence.MatchContent)]
    [DataRow("mysql-syslog", FormA + "\n", Confidence.MatchContent)]
    [DataRow("backup/syslog/error.log", FormA + "\n", Confidence.MatchContent)]
    [DataRow("notes.txt", "﻿" + FormA + "\n", Confidence.MatchContent)]
    [DataRow("notes.txt", "\n\r\n" + FormA + "\n", Confidence.MatchContent)]
    [DataRow("notes.txt", FormA + "\r\nprose\r\n", Confidence.MatchContent)]
    [DataRow("notes.txt", "2026-03-01 12:00:00 0 [Note]", Confidence.MatchContent)]
    [DataRow("notes.txt", "260301 12:00:00 [Note]", Confidence.MatchContent)]
    [DataRow("notes.txt", "2026-03-02 10:10:10 0x7f3a2c1fe640 ", Confidence.MatchContent)]
    [DataRow("notes.txt", "260301 12:00:00 mysqld_safe ", Confidence.MatchContent)]
    [DataRow("notes.txt", "2026-03-01 12:00:00 0 [Not", Confidence.NoMatch)]
    [DataRow("notes.txt", "2026-03-02 10:10:10 0x7f3a2c1fe640", Confidence.NoMatch)]
    [DataRow("notes.txt", "260301 12:00:00 mysqld_safe", Confidence.NoMatch)]
    [DataRow("notes.txt", "", Confidence.NoMatch)]
    [DataRow("notes.txt", "\n\r\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "   \n" + FormA + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "\0\0\n" + FormA + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "InnoDB: Failing assertion: page_is_leaf(block->page.frame)\n??:0(abort)[0x7f3a3c4287f3]\n" + FormA + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "2026-03-01T12:30:15.123456+01:00 web-1 sshd[1234]: Accepted publickey for root\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "<13>Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "This is just some prose about a server.\n" + FormA + "\n", Confidence.NoMatch)]
    [DataRow("notes.txt", "__CURSOR=s=0123456789abcdef;i=1;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1;t=1;x=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n", Confidence.NoMatch)]
    [DataRow("syslog", FormA + "\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("syslog.1", FormA + "\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("kern.log", FormA + "\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("kern.log.2", FormA + "\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("syslog-20260301", FormA + "\n" + Ready + "\n", Confidence.NoMatch)]
    [DataRow("backup/var/log/syslog.1", FormA + "\n" + Ready + "\n", Confidence.NoMatch)]
    public void MariaDbErrorLogParserDetectClaimsAFileByItsFirstNonEmptyLine(string name, string head, Confidence expected)
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(expected, confidence, "confidence");
    }

    /// <summary>
    /// A line that is no header, alone as the first line, does not claim the file.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DynamicData(nameof(NotHeaderLines))]
    public void MariaDbErrorLogParserDetectIgnoresAFirstLineThatIsNoHeader(string line)
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile("notes.txt", null), Encoding.UTF8.GetBytes($"{line}\n"));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "confidence");
    }

    /// <summary>
    /// A head of 4,096 bytes that is one header line cut by the end of the head claims the file.
    /// </summary>
    [TestMethod]
    public void MariaDbErrorLogParserDetectClaimsAHeadThatCutsTheFirstLine()
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);
        var prefix = "2026-03-01 12:00:00 0 [Note] ";
        var head = Encoding.UTF8.GetBytes(prefix + new string('a', 4096 - prefix.Length));

        // Act
        var confidence = parser.Detect(new LogFile("notes.txt", null), head);

        // Assert
        Assert.HasCount(4096, head, "the head has 4,096 bytes");
        Assert.AreEqual(Confidence.MatchContent, confidence, "confidence");
    }

    /// <summary>
    /// The parser does not claim the heads of the journal and syslog parsers.
    /// </summary>
    /// <param name="head">The head</param>
    [TestMethod]
    [DataRow("Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root\n")]
    [DataRow("2026-03-01T12:30:15.123456+01:00 web-1 sshd[1234]: Accepted publickey for root\n")]
    [DataRow("__CURSOR=s=0123456789abcdef;i=1;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1;t=1;x=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n")]
    public void MariaDbErrorLogParserDetectIgnoresJournalAndSyslogHeads(string head)
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile("export.txt", null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "confidence");
    }

    /// <summary>
    /// A cancelled token ends the parse with an operation canceled exception before anything is emitted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncHonorsACancelledToken()
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);
        var emitter = new RecordingEmitter();
        using var cancelled = new CancellationTokenSource();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(FormA + "\n"));

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => parser.ParseAsync(_file, input, emitter, cancelled.Token), "cancelled parse");
        Assert.AreEqual(0, emitter.Calls, "nothing was emitted");
    }

    /// <summary>
    /// Cancelling while the file is read ends the parse without emitting the open entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncDoesNotEmitTheOpenEntryWhenCancelled()
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);
        using var cancel = new CancellationTokenSource();
        var emitter = new RecordingEmitter
                      {
                          Observed = _ => cancel.Cancel()
                      };
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);
        using var input = new MemoryStream(content);

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => parser.ParseAsync(_file, input, emitter, cancel.Token), "cancelled parse");

        // Assert
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
        Assert.HasCount(1, emitter.Records, "only the first entry was emitted");
    }

    /// <summary>
    /// An exception of the emitter ends the parse with that exception, without another call and without emitting the open entry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncPassesOnTheExceptionOfTheEmitter()
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);
        var failure = new InvalidOperationException("store is full");
        var emitter = new RecordingEmitter
                      {
                          Failure = (_, record) => RecordingEmitter.Line(record).Message.StartsWith("Aborted connection", StringComparison.Ordinal) ? failure : null
                      };
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);
        using var input = new MemoryStream(content);

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken), "the emitter fails");

        // Assert
        Assert.AreSame(failure, thrown, "the exception of the emitter");
        Assert.AreEqual(16, emitter.Calls, "the record of line 19 is the 16th and the emitter is not called again for the entry of line 20");
    }

    /// <summary>
    /// Every skip reason is one of the fixed texts, none of them shows input.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncReportsOnlyFixedSkipReasons()
    {
        // Arrange
        var text = "\nsecret-before\n2026-02-30 12:00:00 0 [Note] secret-invalid\n0000-01-01 00:00:00 0 [Note] secret-outside\n2026-03-01 12:00:00 0 [Note] ok\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), _file, text, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(1, EmptyLine), (2, BeforeFirst), (3, InvalidDate), (4, Outside)], emitter.Skips, "skips");
        Assert.HasCount(1, emitter.Records, "the valid entry");
    }

    /// <summary>
    /// A very long entry is bounded: the parse allocates little beyond what the line reader allocates on the same input.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncOfAHugeEntryAllocatesLittleBeyondTheReader()
    {
        // Arrange
        var header = "2026-03-01 12:00:00 0 [Note] h\n"u8.ToArray();
        var pattern = Encoding.ASCII.GetBytes(new string('a', 1023) + "\n");
        var lineCount = 64L * Mebibyte / 1024;

        // Act
        var (emitter, parseAllocated) = await ParseMeasuredAsync(() => new PatternStream(header, pattern, 64L * Mebibyte, []));
        var (readerLines, readerAllocated) = await ReaderAllocationAsync(() => new PatternStream(header, pattern, 64L * Mebibyte, []));

        // Assert
        Assert.AreEqual(lineCount + 1, readerLines, "lines of the input");
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");

        var logLine = RecordingEmitter.Line(emitter.Records[0]);
        var parts = logLine.Message.Split('\n');
        var kept = parts.Length - 2;

        Assert.IsGreaterThan(0, kept, "some lines are kept");
        Assert.AreEqual($"[{lineCount - kept} lines omitted]", parts[^1], "the marker counts the continuation lines that are not kept");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(logLine.Message), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(logLine.Truncated, "truncated");
        Assert.IsLessThan(AllocationBound, parseAllocated - readerAllocated, "allocated bytes beyond the line reader, decoding every line would take more than 128 MiB");
    }

    /// <summary>
    /// Millions of held-back empty lines in front of a line are only counted: the parse allocates little beyond the line reader.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncOfManyEmptyLinesBeforeALineAllocatesLittleBeyondTheReader()
    {
        // Arrange
        var header = "2026-03-01 12:00:00 0 [Note] h\n"u8.ToArray();
        var trailer = "x"u8.ToArray();

        // Act
        var (emitter, parseAllocated) = await ParseMeasuredAsync(() => new PatternStream(header, "\n"u8.ToArray(), 16L * Mebibyte, trailer));
        var (_, readerAllocated) = await ReaderAllocationAsync(() => new PatternStream(header, "\n"u8.ToArray(), 16L * Mebibyte, trailer));

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");

        var logLine = RecordingEmitter.Line(emitter.Records[0]);

        Assert.AreEqual($"h{new string('\n', 16319)}\n[16760898 lines omitted]", logLine.Message, "header, 16,319 kept empty lines, marker");
        Assert.AreEqual(16345, Encoding.UTF8.GetByteCount(logLine.Message), "UTF-8 bytes");
        Assert.IsTrue(logLine.Truncated, "truncated");
        Assert.IsLessThan(AllocationBound, parseAllocated - readerAllocated, "allocated bytes beyond the line reader, one reference per line would take 128 MiB");
    }

    /// <summary>
    /// Millions of trailing empty lines are only counted and dropped: the parse allocates little beyond the line reader.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MariaDbErrorLogParserParseAsyncOfManyTrailingEmptyLinesAllocatesLittleBeyondTheReader()
    {
        // Arrange
        var header = "2026-03-01 12:00:00 0 [Note] h\n"u8.ToArray();

        // Act
        var (emitter, parseAllocated) = await ParseMeasuredAsync(() => new PatternStream(header, "\n"u8.ToArray(), 16L * Mebibyte, []));
        var (_, readerAllocated) = await ReaderAllocationAsync(() => new PatternStream(header, "\n"u8.ToArray(), 16L * Mebibyte, []));

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual("h", RecordingEmitter.Line(emitter.Records[0]).Message, "the trailing empty lines are dropped");
        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[0]).Truncated, "nothing was cut");
        Assert.IsLessThan(AllocationBound, parseAllocated - readerAllocated, "allocated bytes beyond the line reader, one UTF-16 character per line would take 32 MiB");
    }

    /// <summary>
    /// Returns the message of a header line of the fixture.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The text after the prefix</returns>
    private static string HeaderMessage(string line)
    {
        return line.Contains("0x7f3a2c1fe640", StringComparison.Ordinal) ? line[line.IndexOf("InnoDB:", StringComparison.Ordinal)..] : line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
    }

    /// <summary>
    /// Builds the header line of a time: form A for a time with a four-digit year and form C for a time with a two-digit year.
    /// </summary>
    /// <param name="time">The time, <c>yyyy-MM-dd HH:mm:ss</c> or <c>yyMMdd HH:mm:ss</c></param>
    /// <returns>The line</returns>
    private static string HeaderLine(string time)
    {
        return time.IndexOf('-', StringComparison.Ordinal) == 4 ? $"{time} 0 [Note] x" : $"{time} [Note] x";
    }

    /// <summary>
    /// Describes what the emitter saw for a series of one-line entries: the instant in UTC or <c>skip</c> for each line.
    /// </summary>
    /// <param name="emitter">The emitter</param>
    /// <param name="lineCount">The number of lines</param>
    /// <returns>The description, separated by a bar</returns>
    private static string Outcomes(RecordingEmitter emitter, int lineCount)
    {
        var results = new string[lineCount];
        var recordIndex = 0;

        foreach (var (line, _) in emitter.Skips)
        {
            results[line - 1] = "skip";
        }

        for (var index = 0; index < lineCount; index++)
        {
            if (results[index] is null)
            {
                results[index] = emitter.Records[recordIndex++].CapturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
        }

        return string.Join('|', results);
    }

    /// <summary>
    /// Parses the fixture in a zone.
    /// </summary>
    /// <param name="zone">The zone</param>
    /// <returns>A task that returns the emitter</returns>
    private async Task<RecordingEmitter> ParseFixtureAsync(DateTimeZone zone)
    {
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(FixturePath), TestContext.CancellationToken);

        return await RecordingEmitter.ParseAsync(new MariaDbErrorLogParser(zone), new LogFile("mysql/error.log", null), content, TestContext.CancellationToken);
    }

    /// <summary>
    /// Parses a stream and measures the bytes the parse allocates.
    /// </summary>
    /// <param name="create">Creates the stream</param>
    /// <returns>A task that returns the emitter and the allocated bytes</returns>
    private async Task<(RecordingEmitter Emitter, long Allocated)> ParseMeasuredAsync(Func<Stream> create)
    {
        var emitter = new RecordingEmitter();
        var parser = new MariaDbErrorLogParser(DateTimeZone.Utc);

        await using var input = create();

        var before = GC.GetTotalAllocatedBytes(true);

        await parser.ParseAsync(_file, input, emitter, TestContext.CancellationToken);

        return (emitter, GC.GetTotalAllocatedBytes(true) - before);
    }

    /// <summary>
    /// Reads a stream with a plain loop of the line reader and measures the bytes that allocates, which is the share of the parse that no parser can avoid.
    /// </summary>
    /// <param name="create">Creates the stream</param>
    /// <returns>A task that returns the number of lines and the allocated bytes</returns>
    private async Task<(long Lines, long Allocated)> ReaderAllocationAsync(Func<Stream> create)
    {
        await using var input = create();

        var reader = new LogLineReader(input);
        var before = GC.GetTotalAllocatedBytes(true);

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            TestContext.CancellationToken.ThrowIfCancellationRequested();
        }

        return (reader.LineNumber, GC.GetTotalAllocatedBytes(true) - before);
    }

    #endregion // Methods
}