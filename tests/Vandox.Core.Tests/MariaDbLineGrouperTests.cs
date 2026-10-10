using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MariaDbLineGrouper"/>
/// </summary>
[TestClass]
public class MariaDbLineGrouperTests
{
    #region Constants

    private const string Ready = "2026-03-01 23:00:05 0 [Note] /usr/sbin/mariadbd: ready for connections.";
    private const string Version = "Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04";
    private const string ReadyMessage = "/usr/sbin/mariadbd: ready for connections.";
    private const int Mebibyte = 1024 * 1024;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The maximum span between a header and a line of its entry is 60 seconds.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperMaxSpanIsSixtySeconds()
    {
        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(60), MariaDbLineGrouper.MaxSpan, "the time bound");
    }

    /// <summary>
    /// A header and the line after it become one record with the message after the prefix, the event, the priority of the level and the fields of the header line.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddJoinsAHeaderAndItsContinuationLineIntoOneRecord()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var header = MariaDbSamples.Record(Ready, MariaDbSamples.Start);
        var next = MariaDbSamples.Record(Version, MariaDbSamples.Start.AddSeconds(1));

        // Act
        grouper.Add(header, ready);
        grouper.Add(next, ready);

        var heldBack = ready.Count;

        grouper.Finish(ready);

        // Assert
        Assert.AreEqual(0, heldBack, "the open entry is not emitted before its end");
        Assert.HasCount(1, ready, "one record");

        var record = ready[0];
        var line = RecordingEmitter.Line(record);

        Assert.AreEqual($"{ReadyMessage}\n{Version}", line.Message, "the header message and the line, joined by a line feed");
        Assert.AreEqual(MariaDbEvents.Ready, line.Event, "event");
        Assert.AreEqual((byte?)6, line.Priority, "priority");
        Assert.AreEqual(MariaDbSamples.Start, record.CapturedAt, "the time of the header line");
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
    /// The syslog source and the log name of the header line are those of the record.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddKeepsTheSourceAndLogOfTheHeaderLine()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start, source: "syslog", log: "backup/var/log/syslog.1"), ready);
        grouper.Add(MariaDbSamples.Record(Version, MariaDbSamples.Start, source: "syslog", log: "backup/var/log/syslog.1"), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual("syslog", ready[0].Source, "source");
        Assert.AreEqual("backup/var/log/syslog.1", RecordingEmitter.Line(ready[0]).Log, "log");
    }

    /// <summary>
    /// The level of the header decides the priority, whatever the priority of the line is.
    /// </summary>
    /// <param name="message">The message of the header line</param>
    /// <param name="linePriority">The priority of the line</param>
    /// <param name="expected">The expected priority</param>
    [TestMethod]
    [DataRow("2026-03-01 23:00:05 0 [ERROR] x", 6, 3)]
    [DataRow("2026-03-01 23:00:05 0 [ERROR] x", 3, 3)]
    [DataRow("2026-03-01 23:00:05 0 [Warning] x", 6, 4)]
    [DataRow("2026-03-01 23:00:05 0 [Warning] x", 3, 4)]
    [DataRow("2026-03-01 23:00:05 0 [Note] x", 3, 6)]
    [DataRow("260301 23:00:05 [ERROR] x", 6, 3)]
    public void MariaDbLineGrouperAddTakesThePriorityFromTheLevel(string message, int linePriority, int expected)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(message, MariaDbSamples.Start, priority: (byte)linePriority), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual((byte?)expected, RecordingEmitter.Line(ready[0]).Priority, "priority of the level");
        Assert.AreEqual("x", RecordingEmitter.Line(ready[0]).Message, "the text after the header prefix");
    }

    /// <summary>
    /// The forms without a level keep the priority of the line, have no event and keep the program of the line.
    /// </summary>
    /// <param name="message">The message of the header line</param>
    /// <param name="linePriority">The priority of the line, or -1 for none</param>
    /// <param name="expectedMessage">The expected message</param>
    [TestMethod]
    [DataRow("2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836", 5, "InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836")]
    [DataRow("2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836", -1, "InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836")]
    [DataRow("260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql", 6, "Starting mariadbd daemon with databases from /var/lib/mysql")]
    [DataRow("260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql", -1, "Starting mariadbd daemon with databases from /var/lib/mysql")]
    public void MariaDbLineGrouperAddKeepsThePriorityOfTheLineForAHeaderWithoutALevel(string message, int linePriority, string expectedMessage)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(message, MariaDbSamples.Start, priority: linePriority < 0 ? null : (byte)linePriority), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");

        var line = RecordingEmitter.Line(ready[0]);

        Assert.AreEqual(linePriority < 0 ? null : (byte?)linePriority, line.Priority, "the priority of the line");
        Assert.AreEqual(string.Empty, line.Event, "no event");
        Assert.AreEqual("mariadbd", line.Program, "the program stays the program of the line");
        Assert.AreEqual(expectedMessage, line.Message, "the text after the header prefix");
    }

    /// <summary>
    /// Both program names open an entry.
    /// </summary>
    /// <param name="program">The program</param>
    [TestMethod]
    [DataRow("mariadbd")]
    [DataRow("mysqld")]
    public void MariaDbLineGrouperAddReadsTheProgramsMariaDbdAndMysqld(string program)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start, program: program), ready);
        grouper.Add(MariaDbSamples.Record(Version, MariaDbSamples.Start, program: program), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual(program, RecordingEmitter.Line(ready[0]).Program, "program");
        Assert.AreEqual($"{ReadyMessage}\n{Version}", RecordingEmitter.Line(ready[0]).Message, "message");
        Assert.AreEqual(MariaDbEvents.Ready, RecordingEmitter.Line(ready[0]).Event, "event");
    }

    /// <summary>
    /// A header-shaped message of another program is emitted unchanged at once, and it does not open an entry.
    /// </summary>
    /// <param name="program">The program</param>
    [TestMethod]
    [DataRow("mariadb")]
    [DataRow("MariaDBd")]
    [DataRow("mysqld_safe")]
    [DataRow("kernel")]
    [DataRow("sshd")]
    [DataRow("")]
    public void MariaDbLineGrouperAddEmitsAHeaderShapedMessageOfAnotherProgramUnchanged(string program)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var other = MariaDbSamples.Record(Ready, MariaDbSamples.Start, program: program);
        var next = MariaDbSamples.Record(Version, MariaDbSamples.Start);

        // Act
        grouper.Add(other, ready);

        var afterOther = ready.ToList();

        grouper.Add(next, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, afterOther, "emitted at once");
        Assert.AreSame(other, afterOther[0], "the record itself");
        Assert.HasCount(2, ready, "no entry was opened, so the next line of the key is emitted as it is");
        Assert.AreSame(next, ready[1], "the next line");
        Assert.AreEqual(string.Empty, RecordingEmitter.Line(ready[0]).Event, "no event");
    }

    /// <summary>
    /// A record with a line feed in the message is emitted unchanged even when it begins with a header; it does not open an entry.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddEmitsAMessageWithALineFeedUnchangedAndOpensNoEntry()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var binary = MariaDbSamples.Record($"{Ready}\nsecond line", MariaDbSamples.Start);
        var next = MariaDbSamples.Record(Version, MariaDbSamples.Start);

        // Act
        grouper.Add(binary, ready);
        grouper.Add(next, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "both records are emitted as they are");
        Assert.AreSame(binary, ready[0], "the record with the line feed");
        Assert.AreSame(next, ready[1], "no entry was open for the next line");
    }

    /// <summary>
    /// A record with a line feed neither joins nor ends the open entry: it is emitted before the entry, and a later line still joins.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddKeepsTheEntryOpenAcrossAMessageWithALineFeed()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var binary = MariaDbSamples.Record("a\nb", MariaDbSamples.Start);

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start), ready);
        grouper.Add(binary, ready);
        grouper.Add(MariaDbSamples.Record(Version, MariaDbSamples.Start.AddSeconds(1)), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "the record with the line feed and the entry");
        Assert.AreSame(binary, ready[0], "the record with the line feed comes first");
        Assert.AreEqual($"{ReadyMessage}\n{Version}", RecordingEmitter.Line(ready[1]).Message, "the entry has the header and the line after the record with the line feed");
    }

    /// <summary>
    /// Without an open entry a line without a header is emitted unchanged: at the start, a MySQL 8 line, and a payload that is no log line.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddEmitsLinesWithoutAnOpenEntryUnchanged()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var first = MariaDbSamples.Record("no header here", MariaDbSamples.Start);
        var mysql8 = MariaDbSamples.Record("2026-03-01T12:00:00.123456Z 0 [System] [MY-010116] [Server] /usr/sbin/mysqld (mysqld 8.0.36) starting as process 1", MariaDbSamples.Start, program: "mysqld");
        var gap = new DataRecord
                  {
                      Origin = RecordOrigin.Import,
                      Source = "journal",
                      CapturedAt = MariaDbSamples.Start,
                      Data = new Gap()
                  };

        // Act
        grouper.Add(first, ready);
        grouper.Add(mysql8, ready);
        grouper.Add(gap, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(3, ready, "three records");
        Assert.AreSame(first, ready[0], "the line at the start");
        Assert.AreSame(mysql8, ready[1], "the MySQL 8 line is no header");
        Assert.AreSame(gap, ready[2], "a payload that is no log line");
    }

    /// <summary>
    /// Lines 24 to 52 of the fixture become one record of the event abort; the inner empty lines are kept and the trailing one is dropped.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddJoinsTheCrashReportOfTheFixtureIntoOneRecord()
    {
        // Arrange
        var lines = MariaDbSamples.FixtureLines();
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var at = new DateTimeOffset(2026, 3, 2, 10, 10, 10, TimeSpan.Zero);

        // Act
        grouper.Add(MariaDbSamples.Record(lines[23], at, pid: 3456), ready);

        for (var index = 24; index < 52; index++)
        {
            grouper.Add(MariaDbSamples.Record(lines[index], at, pid: 3456), ready);
        }

        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");

        var line = RecordingEmitter.Line(ready[0]);

        Assert.AreEqual(MariaDbEvents.Abort, line.Event, "event");
        Assert.AreEqual((byte?)3, line.Priority, "priority");
        Assert.AreEqual($"mysqld got signal 6 ;\n{string.Join('\n', lines[24..51])}", line.Message, "the header and lines 25 to 51");
        Assert.AreEqual(3, line.Message.Split('\n').Count(part => part.Length == 0), "the empty lines 28, 30 and 50 are kept");
        Assert.IsFalse(line.Message.EndsWith('\n'), "the trailing empty line 52 is dropped");
        Assert.AreEqual(at, ready[0].CapturedAt, "the time of the header");
    }

    /// <summary>
    /// A line of another program, another process or another host between a header and a continuation line is emitted unchanged before the entry, and the entry holds only its own lines.
    /// </summary>
    /// <param name="program">The program of the intruding line</param>
    /// <param name="pid">The process ID of the intruding line</param>
    /// <param name="host">The host of the intruding line</param>
    [TestMethod]
    [DataRow("sshd", 2345, "web-1")]
    [DataRow("mysqld", 2345, "web-1")]
    [DataRow("mariadbd", 9999, "web-1")]
    [DataRow("mariadbd", 2345, "web-2")]
    public void MariaDbLineGrouperAddEmitsInterleavedLinesBeforeTheEntry(string program, int pid, string host)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var intruder = MariaDbSamples.Record("intruder", MariaDbSamples.Start.AddSeconds(1), pid: pid, program: program, host: host);

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start), ready);
        grouper.Add(intruder, ready);

        var afterIntruder = ready.ToList();

        grouper.Add(MariaDbSamples.Record(Version, MariaDbSamples.Start.AddSeconds(2)), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, afterIntruder, "the intruder is emitted at once");
        Assert.AreSame(intruder, afterIntruder[0], "the intruder itself");
        Assert.HasCount(2, ready, "the intruder and the entry");
        Assert.AreEqual($"{ReadyMessage}\n{Version}", RecordingEmitter.Line(ready[1]).Message, "the entry holds its own lines only");
    }

    /// <summary>
    /// A header of another process emits the open entry and opens its own; a later line without a header of the first process is emitted unchanged.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddEndsTheEntryAtAHeaderOfAnotherProcess()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var late = MariaDbSamples.Record("late line of the first process", MariaDbSamples.Start.AddSeconds(2), pid: 2345);

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start, pid: 2345), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("Starting MariaDB 10.6.12 source revision  as process 7"), MariaDbSamples.Start.AddSeconds(1), pid: 7), ready);

        var afterSecondHeader = ready.ToList();

        grouper.Add(late, ready);
        grouper.Add(MariaDbSamples.Record(Version, MariaDbSamples.Start.AddSeconds(3), pid: 7), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, afterSecondHeader, "the first entry is emitted by the second header");
        Assert.AreEqual(ReadyMessage, RecordingEmitter.Line(afterSecondHeader[0]).Message, "the first entry");
        Assert.HasCount(3, ready, "first entry, the late line and the second entry");
        Assert.AreSame(late, ready[1], "the late line is emitted as it is, before the open second entry");
        Assert.AreEqual($"Starting MariaDB 10.6.12 source revision  as process 7\n{Version}", RecordingEmitter.Line(ready[2]).Message, "the second entry holds the line of its process");
        Assert.AreEqual(MariaDbEvents.Start, RecordingEmitter.Line(ready[2]).Event, "event of the second entry");
    }

    /// <summary>
    /// A line without a header at most 60 seconds after the header joins; one 60 seconds and one microsecond after ends the entry, and the following line of the key is emitted unchanged too.
    /// </summary>
    /// <param name="offsetTicks">The time of the line after the header, in ticks</param>
    /// <param name="joins">Whether the line joins the entry</param>
    [TestMethod]
    [DataRow(0L, true)]
    [DataRow(600000000L, true)]
    [DataRow(600000010L, false)]
    [DataRow(-600000000L, true)]
    [DataRow(-600000010L, false)]
    public void MariaDbLineGrouperAddJoinsALineWithinSixtySecondsOfTheHeaderOnly(long offsetTicks, bool joins)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var line = MariaDbSamples.Record("line", MariaDbSamples.Start.AddTicks(offsetTicks));
        var following = MariaDbSamples.Record("following", MariaDbSamples.Start.AddSeconds(1));

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start), ready);
        grouper.Add(line, ready);

        var afterLine = ready.ToList();

        grouper.Add(following, ready);
        grouper.Finish(ready);

        // Assert
        if (joins)
        {
            Assert.IsEmpty(afterLine, "the line joined, nothing emitted yet");
            Assert.HasCount(1, ready, "one record");
            Assert.AreEqual($"{ReadyMessage}\nline\nfollowing", RecordingEmitter.Line(ready[0]).Message, "the entry holds the line and the following one");
            Assert.AreEqual(MariaDbSamples.Start, ready[0].CapturedAt, "the time of the header");
        }
        else
        {
            Assert.HasCount(2, afterLine, "the entry and then the line");
            Assert.AreEqual(ReadyMessage, RecordingEmitter.Line(afterLine[0]).Message, "the entry is emitted first, without the line");
            Assert.AreSame(line, afterLine[1], "the line unchanged");
            Assert.HasCount(3, ready, "the following line is emitted unchanged too");
            Assert.AreSame(following, ready[2], "no entry is open after the bound");
        }
    }

    /// <summary>
    /// The bound counts from the header, not from the previous member.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddCountsTheTimeBoundFromTheHeader()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var late = MariaDbSamples.Record("late", MariaDbSamples.Start.AddSeconds(61));

        // Act
        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record("a", MariaDbSamples.Start.AddSeconds(30)), ready);
        grouper.Add(MariaDbSamples.Record("b", MariaDbSamples.Start.AddSeconds(59)), ready);

        var beforeLate = ready.Count;

        grouper.Add(late, ready);
        grouper.Finish(ready);

        // Assert
        Assert.AreEqual(0, beforeLate, "the lines at 30 and 59 seconds joined");
        Assert.HasCount(2, ready, "the entry and the late line");
        Assert.AreEqual($"{ReadyMessage}\na\nb", RecordingEmitter.Line(ready[0]).Message, "the entry holds the two joined lines");
        Assert.AreSame(late, ready[1], "the line at 61 seconds ends the entry although it is 2 seconds after the previous member");
    }

    /// <summary>
    /// An entry of exactly 16,384 bytes is one record, whole.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddKeepsAnEntryOfExactlyTheLimitWhole()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var text = new string('a', 16382);

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(text, MariaDbSamples.Start), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual($"h\n{text}", RecordingEmitter.Line(ready[0]).Message, "the whole entry");
        Assert.AreEqual(16384, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(ready[0]).Message), "exactly the limit");
        Assert.IsFalse(RecordingEmitter.Line(ready[0]).Truncated, "not truncated");
    }

    /// <summary>
    /// A line that would make the message 16,385 bytes ends the entry: the entry is emitted whole without a marker, then the line and the next line of the key unchanged.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddEndsTheEntryAtALineThatDoesNotFit()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var first = new string('a', 16000);
        var tooLong = MariaDbSamples.Record(new string('b', 382), MariaDbSamples.Start.AddSeconds(1), truncated: true);
        var following = MariaDbSamples.Record("short", MariaDbSamples.Start.AddSeconds(2));

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(first, MariaDbSamples.Start.AddSeconds(1)), ready);
        grouper.Add(tooLong, ready);

        var afterTooLong = ready.ToList();

        grouper.Add(following, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, afterTooLong, "the entry, then the line");
        Assert.AreEqual($"h\n{first}", RecordingEmitter.Line(afterTooLong[0]).Message, "the entry is whole, 16,002 bytes");
        Assert.DoesNotContain("lines omitted", RecordingEmitter.Line(afterTooLong[0]).Message, "no marker");
        Assert.IsFalse(RecordingEmitter.Line(afterTooLong[0]).Truncated, "the flag of the line that does not fit is not taken over");
        Assert.AreSame(tooLong, afterTooLong[1], "the line unchanged, with its own flag");
        Assert.HasCount(3, ready, "the following short line is emitted unchanged too");
        Assert.AreSame(following, ready[2], "no entry is open");
    }

    /// <summary>
    /// The line that makes the message exactly 16,384 bytes still joins.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddJoinsALineThatMakesTheMessageExactlyTheLimit()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(new string('a', 16000), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(new string('b', 381), MariaDbSamples.Start), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual(16384, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(ready[0]).Message), "16,002 + 1 + 381 bytes");
    }

    /// <summary>
    /// The held-back empty lines count: with two of them before the line, the entry's record does not end in a line feed and the empty lines are not emitted.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddCountsTheHeldBackEmptyLinesAndDropsThemAtTheEnd()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var first = new string('a', 16000);
        var tooLong = MariaDbSamples.Record(new string('b', 380), MariaDbSamples.Start);

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(first, MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(string.Empty, MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(string.Empty, MariaDbSamples.Start), ready);
        grouper.Add(tooLong, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "the entry and the line; the empty lines are not emitted");
        Assert.AreEqual($"h\n{first}", RecordingEmitter.Line(ready[0]).Message, "no trailing line feed");
        Assert.AreSame(tooLong, ready[1], "the line that did not fit, because 16,002 + 2 + 1 + 380 is 16,385");
    }

    /// <summary>
    /// A first continuation line of 16,000 characters joins; a second one ends the entry.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddJoinsAFirstLongLineAndEndsTheEntryAtTheSecond()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var first = new string('a', 16000);
        var second = MariaDbSamples.Record(new string('b', 16000), MariaDbSamples.Start);

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(first, MariaDbSamples.Start), ready);
        grouper.Add(second, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "the entry and the second line");
        Assert.AreEqual($"h\n{first}", RecordingEmitter.Line(ready[0]).Message, "the entry holds the first line");
        Assert.AreSame(second, ready[1], "the second line unchanged");
    }

    /// <summary>
    /// Sizes count UTF-8 bytes of the decoded text: a line of two-byte characters that is 16,386 bytes does not join.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddCountsUtf8BytesNotCharacters()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var fits = new string('é', 8191);
        var tooLong = MariaDbSamples.Record(new string('é', 8192), MariaDbSamples.Start);

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(fits, MariaDbSamples.Start), ready);
        grouper.Add(tooLong, ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "the entry and the long line");
        Assert.AreEqual($"h\n{fits}", RecordingEmitter.Line(ready[0]).Message, "1 + 1 + 16,382 bytes fit");
        Assert.AreSame(tooLong, ready[1], "1 + 1 + 16,384 bytes do not fit");
    }

    /// <summary>
    /// A truncated header or a truncated line that joined makes the entry truncated.
    /// </summary>
    /// <param name="headerTruncated">Whether the header record is truncated</param>
    /// <param name="lineTruncated">Whether the line is truncated</param>
    /// <param name="expected">The expected flag of the entry</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public void MariaDbLineGrouperAddTakesTheTruncatedFlagFromTheHeaderAndTheJoinedLines(bool headerTruncated, bool lineTruncated, bool expected)
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start, truncated: headerTruncated), ready);
        grouper.Add(MariaDbSamples.Record("line", MariaDbSamples.Start, truncated: lineTruncated), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual(expected, RecordingEmitter.Line(ready[0]).Truncated, "truncated");
    }

    /// <summary>
    /// No text is lost: the non-empty parts of the emitted messages are the non-empty input messages, in input order, each once.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddLosesNoText()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var sizes = new[] { 1, 100, 0, 5000, 16000, 0, 7000, 3, 16000, 0, 0, 12000, 40, 16000, 9000, 9000, 1, 0, 2500, 16000 };
        var expected = new List<string>();
        var index = 0;

        // Act
        foreach (var header in new[] { "first header", "second header" })
        {
            grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note(header), MariaDbSamples.Start), ready);
            expected.Add(header);

            foreach (var size in sizes)
            {
                var text = size == 0 ? string.Empty : new string((char)('a' + (index % 26)), size);

                index++;
                grouper.Add(MariaDbSamples.Record(text, MariaDbSamples.Start), ready);

                if (size > 0)
                {
                    expected.Add(text);
                }
            }
        }

        grouper.Finish(ready);

        // Assert
        var parts = ready.SelectMany(record => RecordingEmitter.Line(record).Message.Split('\n')).Where(part => part.Length > 0).ToList();

        Assert.AreSequenceEqual(expected, parts, "the same texts in the same order");
        Assert.IsTrue(ready.All(record => Encoding.UTF8.GetByteCount(RecordingEmitter.Line(record).Message) <= ModelLimits.MaxTextBytes), "every record is within the limit");
        Assert.DoesNotContain(record => RecordingEmitter.Line(record).Message.Contains("lines omitted", StringComparison.Ordinal), ready, "no marker");
    }

    /// <summary>
    /// A recovery start of one host followed by a started line of another host: the started line gets the event recovery end; this pins the documented multi-host limitation.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddTracksOneRecoveryInInputOrderOverAllHosts()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234"), MariaDbSamples.Start, pid: 1, host: "web-1"), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678"), MariaDbSamples.Start, pid: 2, host: "web-2"), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "two entries");
        Assert.AreEqual(MariaDbEvents.RecoveryStart, RecordingEmitter.Line(ready[0]).Event, "the start of host A");
        Assert.AreEqual(MariaDbEvents.RecoveryEnd, RecordingEmitter.Line(ready[1]).Event, "the started line of host B ends the recovery of host A");
    }

    /// <summary>
    /// A start line between a recovery start and a started line closes the recovery: the started line has no event.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddClosesARecoveryAtAStartLine()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("Starting MariaDB 10.6.12-MariaDB source revision  as process 7"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678"), MariaDbSamples.Start), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(3, ready, "three entries");
        Assert.AreEqual(MariaDbEvents.RecoveryStart, RecordingEmitter.Line(ready[0]).Event, "recovery start");
        Assert.AreEqual(MariaDbEvents.Start, RecordingEmitter.Line(ready[1]).Event, "start");
        Assert.AreEqual(string.Empty, RecordingEmitter.Line(ready[2]).Event, "the started line has no event after the start line");
    }

    /// <summary>
    /// Two groupers share no recovery state.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddSharesNoRecoveryStateBetweenGroupers()
    {
        // Arrange
        var ready = new List<DataRecord>();
        var first = new MariaDbLineGrouper();
        var second = new MariaDbLineGrouper();

        // Act
        first.Add(MariaDbSamples.Record(MariaDbSamples.Note("InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234"), MariaDbSamples.Start), ready);
        first.Finish(ready);
        ready.Clear();
        second.Add(MariaDbSamples.Record(MariaDbSamples.Note("InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678"), MariaDbSamples.Start), ready);
        second.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual(string.Empty, RecordingEmitter.Line(ready[0]).Event, "a new grouper has no open recovery");
    }

    /// <summary>
    /// Finish without an open entry emits nothing, and a second Finish emits nothing.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperFinishEmitsTheOpenEntryOnce()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Finish(ready);

        var empty = ready.Count;

        grouper.Add(MariaDbSamples.Record(Ready, MariaDbSamples.Start), ready);
        grouper.Finish(ready);
        grouper.Finish(ready);

        // Assert
        Assert.AreEqual(0, empty, "nothing is open");
        Assert.HasCount(1, ready, "the entry is emitted once");
    }

    /// <summary>
    /// One header and 2,000 continuation records of 16,000 characters retain less than 1 MiB; the entry is the header and the first line, the other 1,999 lines are emitted unchanged.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddRetainsNoLongLines()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var emitted = 0;
        var tooBig = 0;
        var plain = 0;
        var entryLength = 0;
        var before = RetainedBytes();

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note(new string('h', 300)), MariaDbSamples.Start), ready);

        for (var index = 0; index < 2000; index++)
        {
            grouper.Add(MariaDbSamples.Record(new string('x', 16000), MariaDbSamples.Start), ready);

            foreach (var record in ready)
            {
                var message = RecordingEmitter.Line(record).Message;

                emitted++;
                tooBig += Encoding.UTF8.GetByteCount(message) > ModelLimits.MaxTextBytes ? 1 : 0;
                plain += message.Length == 16000 ? 1 : 0;
                entryLength = message.Length == 16000 ? entryLength : message.Length;
            }

            ready.Clear();
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(grouper);
        grouper.Finish(ready);

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, retaining the lines would be at least 32 MiB");
        Assert.AreEqual(2000, emitted, "the entry and the 1,999 lines that did not join");
        Assert.AreEqual(1999, plain, "unchanged lines");
        Assert.AreEqual(300 + 1 + 16000, entryLength, "the entry holds the header and the first line");
        Assert.AreEqual(0, tooBig, "no record over 16,384 bytes");
        Assert.IsEmpty(ready, "no entry is open at the end");
    }

    /// <summary>
    /// One header and 100,000 empty continuation records retain less than 1 MiB; the entry is the header message alone.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddRetainsNoEmptyLines()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var emitted = 0;

        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);

        var before = RetainedBytes();

        // Act
        for (var index = 0; index < 100000; index++)
        {
            grouper.Add(MariaDbSamples.Record(string.Empty, MariaDbSamples.Start), ready);
            emitted += ready.Count;
            ready.Clear();
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(grouper);
        grouper.Finish(ready);

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, one record per empty line would be several megabytes");
        Assert.AreEqual(0, emitted, "the empty lines are held back as a count");
        Assert.HasCount(1, ready, "the entry at the end");
        Assert.AreEqual("h", RecordingEmitter.Line(ready[0]).Message, "the header message alone");
    }

    /// <summary>
    /// One header and 100,000 continuation records of 100 characters retain less than 1 MiB; no record is over the limit and no line is lost.
    /// </summary>
    [TestMethod]
    public void MariaDbLineGrouperAddRetainsNoShortLinesAndLosesNone()
    {
        // Arrange
        var grouper = new MariaDbLineGrouper();
        var ready = new List<DataRecord>();
        var parts = 0L;
        var tooBig = 0;

        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("h"), MariaDbSamples.Start), ready);

        var before = RetainedBytes();

        // Act
        for (var index = 0; index < 100000; index++)
        {
            grouper.Add(MariaDbSamples.Record(new string('x', 100), MariaDbSamples.Start), ready);

            foreach (var record in ready)
            {
                var message = RecordingEmitter.Line(record).Message;

                parts += message.AsSpan().Count('\n') + 1;
                tooBig += Encoding.UTF8.GetByteCount(message) > ModelLimits.MaxTextBytes ? 1 : 0;
            }

            ready.Clear();
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(grouper);
        grouper.Finish(ready);

        foreach (var record in ready)
        {
            var message = RecordingEmitter.Line(record).Message;

            parts += message.AsSpan().Count('\n') + 1;
            tooBig += Encoding.UTF8.GetByteCount(message) > ModelLimits.MaxTextBytes ? 1 : 0;
        }

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, retaining the lines would be about 20 MB");
        Assert.AreEqual(100001L, parts, "the header and the 100,000 lines, each once");
        Assert.AreEqual(0, tooBig, "no record over 16,384 bytes");
        Assert.IsEmpty(ready, "the entry with the header and 162 lines was emitted when line 163 ended it, and no entry is open at the end");
    }

    /// <summary>
    /// Returns the bytes the process retains after a full collection, which is how the heap-bound claims are measured.
    /// </summary>
    /// <returns>The number of bytes</returns>
    private static long RetainedBytes()
    {
#pragma warning disable S1215
        return GC.GetTotalMemory(true);
#pragma warning restore S1215
    }

    #endregion // Methods
}