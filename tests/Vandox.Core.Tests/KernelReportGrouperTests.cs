using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="KernelReportGrouper"/>
/// </summary>
[TestClass]
public class KernelReportGrouperTests
{
    #region Constants

    private const string OomStart = "[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0";
    private const string OomKill = "[1.9] Out of memory: Killed process 4242 (mariadbd) total-vm:8400000kB, anon-rss:7400000kB";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _base = new(2026, 3, 1, 12, 30, 15, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An OOM report is held back until its kill line and then emitted as one record with the time of its first line and the lines joined by line feeds.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddJoinsAnOomReportIntoOneRecord()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        for (var index = 0; index < KernelReportSamples.Oom.Length - 1; index++)
        {
            grouper.Add(Kernel(KernelReportSamples.Oom[index], index), ready);
        }

        var heldBack = ready.Count;

        grouper.Add(Kernel(KernelReportSamples.Oom[^1], 20), ready);

        // Assert
        Assert.AreEqual(0, heldBack, "nothing is emitted while the report is open");
        Assert.HasCount(1, ready, "the report is emitted at its kill line");

        var report = RecordingEmitter.Line(ready[0]);

        Assert.AreEqual("kernel", report.Program, "program");
        Assert.AreEqual(0, report.Pid, "process ID");
        Assert.AreEqual("web-1", report.Host, "host");
        Assert.AreEqual("syslog", report.Log, "log of the first line");
        Assert.AreEqual(string.Join('\n', KernelReportSamples.Oom), report.Message, "the member messages joined by line feeds in order");
        Assert.IsFalse(report.Truncated, "not truncated");
        Assert.AreEqual(_base, ready[0].CapturedAt, "the time of the first line");
        Assert.AreEqual(RecordOrigin.Import, ready[0].Origin, "origin");
        Assert.AreEqual("syslog", ready[0].Source, "source");
    }

    /// <summary>
    /// Lines outside a report pass through at once, in order.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddPassesLinesOutsideAReportThrough()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        var kernel = Kernel("[1.0] eth0: link up", 0);
        var other = Kernel("Accepted publickey", 1, program: "sshd");
        var oomLike = Kernel("[1.2] oom_reaper: reaped process 4242 (mariadbd)", 2);

        // Act
        grouper.Add(kernel, ready);
        grouper.Add(other, ready);
        grouper.Add(oomLike, ready);
        grouper.Finish(ready);

        // Assert
        Assert.AreSequenceEqual(new[] { kernel, other, oomLike }.Select(RecordingEmitter.Describe).ToList(), ready.Select(RecordingEmitter.Describe).ToList(), "the records, unchanged and in order");
    }

    /// <summary>
    /// A line of another program or a kernel line of another host written inside a report is emitted before the report and does not end it.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddEmitsInterleavedLinesOfOthersBeforeTheReport()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        var sshd = Kernel("Accepted publickey", 1, program: "sshd");
        var killLike = Kernel("Out of memory: Killed process 1 (x)", 2, program: "sshd");
        var otherHost = Kernel("[9.9] eth0: link up", 3, host: "web-2");
        var otherHostKill = Kernel("[9.9] Out of memory: Killed process 1 (x)", 4, host: "web-2");

        // Act
        grouper.Add(Kernel(OomStart, 0), ready);
        grouper.Add(sshd, ready);
        grouper.Add(Kernel("[1.1] CPU: 1 PID: 4242", 1), ready);
        grouper.Add(killLike, ready);
        grouper.Add(otherHost, ready);
        grouper.Add(otherHostKill, ready);

        var beforeEnd = ready.ToList();

        grouper.Add(Kernel(OomKill, 5), ready);

        // Assert
        Assert.AreSequenceEqual(new[] { sshd, killLike, otherHost, otherHostKill }.Select(RecordingEmitter.Describe).ToList(), beforeEnd.Select(RecordingEmitter.Describe).ToList(), "the lines of others are emitted at once");
        Assert.HasCount(5, ready, "then the report");
        Assert.AreEqual($"{OomStart}\n[1.1] CPU: 1 PID: 4242\n{OomKill}", RecordingEmitter.Line(ready[4]).Message, "the report holds only the lines of its host and program");
    }

    /// <summary>
    /// Every start marker opens and every end marker closes a report.
    /// </summary>
    /// <param name="start">The first message</param>
    /// <param name="end">The last message</param>
    [TestMethod]
    [DataRow("[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca", "[1.9] Out of memory: Killed process 4242 (mariadbd)")]
    [DataRow("[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca", "[1.9] Memory cgroup out of memory: Killed process 4242 (mariadbd) total-vm:1kB")]
    [DataRow("[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca", "[1.9] Out of memory and no killable processes")]
    [DataRow("[  1.0] ------------[ cut here ]------------", "[  1.9] ---[ end trace 0000000000000000 ]---")]
    [DataRow("[  1.0] ------------[ cut here ]------------", "[  1.9] ---[ end trace 9f1c2d3e4a5b6c7d ]---")]
    public void KernelReportGrouperAddOpensAndClosesAReportAtItsMarkers(string start, string end)
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(Kernel(start, 0), ready);
        grouper.Add(Kernel("[1.5] in between", 1), ready);

        var open = ready.Count;

        grouper.Add(Kernel(end, 2), ready);

        var afterEnd = ready.Count;

        grouper.Add(Kernel("[2.0] after", 3), ready);

        // Assert
        Assert.AreEqual(0, open, "the report is open");
        Assert.AreEqual(1, afterEnd, "the end line closes the report");
        Assert.HasCount(2, ready, "the next line is a record of its own");
        Assert.AreEqual($"{start}\n[1.5] in between\n{end}", RecordingEmitter.Line(ready[0]).Message, "the report");
        Assert.AreEqual("[2.0] after", RecordingEmitter.Line(ready[1]).Message, "the line after");
    }

    /// <summary>
    /// A warning report from the cut line to the end trace line is grouped the same way.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddGroupsAWarningReport()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        for (var index = 0; index < KernelReportSamples.CutHere.Length; index++)
        {
            grouper.Add(Kernel(KernelReportSamples.CutHere[index], index), ready);
        }

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual(string.Join('\n', KernelReportSamples.CutHere), RecordingEmitter.Line(ready[0]).Message, "the members joined by line feeds");
        Assert.AreEqual(_base, ready[0].CapturedAt, "the time of the first line");
    }

    /// <summary>
    /// The priority of the report is the lowest priority of its members, and none if no member has one.
    /// </summary>
    /// <param name="priorities">The priorities of the three lines separated by a comma, empty for none</param>
    /// <param name="expected">The expected priority, or -1 for none</param>
    [TestMethod]
    [DataRow("4,6,3", 3)]
    [DataRow("4,,3", 3)]
    [DataRow("6,6,6", 6)]
    [DataRow("0,7,7", 0)]
    [DataRow(",,", -1)]
    [DataRow(",5,", 5)]
    public void KernelReportGrouperAddUsesTheLowestPriorityOfTheMembers(string priorities, int expected)
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        byte?[] values = [.. priorities.Split(',').Select(part => part.Length == 0 ? (byte?)null : byte.Parse(part, CultureInfo.InvariantCulture))];

        // Act
        grouper.Add(Kernel(OomStart, 0, priority: values[0]), ready);
        grouper.Add(Kernel("[1.5] in between", 1, priority: values[1]), ready);
        grouper.Add(Kernel(OomKill, 2, priority: values[2]), ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.AreEqual(expected < 0 ? null : (byte)expected, RecordingEmitter.Line(ready[0]).Priority, "priority of the report");
    }

    /// <summary>
    /// A report is marked as truncated when one of its members is.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddMarksAReportWithATruncatedMemberAsTruncated()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(Kernel(OomStart, 0), ready);
        grouper.Add(Kernel("[1.5] cut line", 1, truncated: true), ready);
        grouper.Add(Kernel(OomKill, 2), ready);

        // Assert
        Assert.HasCount(1, ready, "one record");
        Assert.IsTrue(RecordingEmitter.Line(ready[0]).Truncated, "truncated");
    }

    /// <summary>
    /// A report over 16,384 bytes keeps its beginning and its end with the kill line and states how many lines were left out.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddKeepsHeadAndTailOfALongReportAndCountsTheOmittedLines()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        List<string> members = [OomStart];

        members.AddRange(Enumerable.Range(1, 400).Select(index => $"[2.{index:D6}] {new string('x', 90)}"));
        members.Add(OomKill);

        // Act
        for (var index = 0; index < members.Count; index++)
        {
            grouper.Add(Kernel(members[index], 0), ready);
        }

        // Assert
        Assert.HasCount(1, ready, "one record");

        var report = RecordingEmitter.Line(ready[0]);
        var lines = report.Message.Split('\n');
        var marker = lines.Select((line, index) => (Line: line, Index: index)).Single(entry => Regex.IsMatch(entry.Line, @"^\[\d+ lines omitted\]$", RegexOptions.None, TimeSpan.FromSeconds(1)));
        var omitted = int.Parse(Regex.Match(marker.Line, @"\d+", RegexOptions.None, TimeSpan.FromSeconds(1)).Value, CultureInfo.InvariantCulture);
        var head = lines[..marker.Index];
        var tail = lines[(marker.Index + 1)..];

        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(report.Message), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(report.Truncated, "truncated");
        Assert.AreEqual(OomStart, lines[0], "begins with the first member line");
        Assert.AreEqual(OomKill, lines[^1], "ends with the kill line");
        Assert.AreEqual(members.Count, head.Length + omitted + tail.Length, "the omitted count is exact");
        Assert.IsGreaterThan(1, omitted, "lines were left out");
        Assert.AreSequenceEqual(members.Take(head.Length).ToList(), head, "the head are the first lines");
        Assert.AreSequenceEqual(members.Skip(members.Count - tail.Length).ToList(), tail, "the tail are the last lines");
        Assert.IsLessThanOrEqualTo(KernelReportGrouper.HeadBytes, head.Sum(line => Encoding.UTF8.GetByteCount(line)), "the head stays within its budget");
    }

    /// <summary>
    /// The budgets count UTF-8 bytes of the decoded text, so members of invalid bytes give a message within the limit.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddCountsBudgetsInUtf8Bytes()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        var invalid = new string('�', 200);

        // Act
        grouper.Add(Kernel(OomStart, 0), ready);

        for (var index = 0; index < 100; index++)
        {
            grouper.Add(Kernel(invalid, 0), ready);
        }

        grouper.Add(Kernel(OomKill, 1), ready);

        // Assert
        Assert.HasCount(1, ready, "one record");

        var report = RecordingEmitter.Line(ready[0]);

        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(report.Message), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(report.Truncated, "truncated");
        Assert.IsNull(ready[0].Validate(), "the record passes the validation of the importer");
        Assert.AreEqual(OomKill, report.Message.Split('\n')[^1], "ends with the kill line");
    }

    /// <summary>
    /// A report without an end line ends at a new start line, and both are emitted.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddEndsAnOpenReportAtANewStartLine()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(Kernel(OomStart, 0), ready);
        grouper.Add(Kernel("[1.1] member of the first", 1), ready);
        grouper.Add(Kernel("[5.0] other invoked oom-killer: gfp_mask=0x1", 2), ready);

        var afterSecondStart = ready.Count;

        grouper.Add(Kernel("[5.1] member of the second", 3), ready);
        grouper.Finish(ready);

        // Assert
        Assert.AreEqual(1, afterSecondStart, "the first report is emitted as it is");
        Assert.HasCount(2, ready, "two reports");
        Assert.AreEqual($"{OomStart}\n[1.1] member of the first", RecordingEmitter.Line(ready[0]).Message, "first report");
        Assert.AreEqual("[5.0] other invoked oom-killer: gfp_mask=0x1\n[5.1] member of the second", RecordingEmitter.Line(ready[1]).Message, "second report");
    }

    /// <summary>
    /// A kernel line of the same host more than 60 seconds after the first line of the report ends it; the line itself is a record of its own.
    /// </summary>
    /// <param name="seconds">The seconds between the first line and the late line</param>
    /// <param name="joins">Whether the line still belongs to the report</param>
    [TestMethod]
    [DataRow(60, true)]
    [DataRow(61, false)]
    [DataRow(3600, false)]
    public void KernelReportGrouperAddEndsAnOpenReportAfterSixtySeconds(int seconds, bool joins)
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(Kernel(OomStart, 0), ready);
        grouper.Add(Kernel("[1.1] member", 1), ready);
        grouper.Add(Kernel("[9.0] late line", seconds), ready);

        var afterLate = ready.ToList();

        grouper.Finish(ready);

        // Assert
        if (joins)
        {
            Assert.IsEmpty(afterLate, "the report is still open");
            Assert.HasCount(1, ready, "one report");
            Assert.AreEqual($"{OomStart}\n[1.1] member\n[9.0] late line", RecordingEmitter.Line(ready[0]).Message, "the report holds the line");
        }
        else
        {
            Assert.HasCount(2, afterLate, "the report and the late line are emitted");
            Assert.AreEqual($"{OomStart}\n[1.1] member", RecordingEmitter.Line(afterLate[0]).Message, "the report is emitted as it is");
            Assert.AreEqual("[9.0] late line", RecordingEmitter.Line(afterLate[1]).Message, "the late line is a record of its own");
            Assert.HasCount(2, ready, "nothing more at the end");
        }
    }

    /// <summary>
    /// A report ends with its 2,000th line.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddEndsAReportAfter2000Lines()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(Kernel(OomStart, 0), ready);

        for (var index = 2; index < KernelReportGrouper.MaxLines; index++)
        {
            grouper.Add(Kernel("k", 1), ready);
        }

        var before = ready.Count;

        grouper.Add(Kernel("k", 1), ready);

        var atLimit = ready.Count;

        grouper.Add(Kernel("[7.0] after the report", 1), ready);

        // Assert
        Assert.AreEqual(0, before, "the report is open at line 1,999");
        Assert.AreEqual(1, atLimit, "line 2,000 closes the report");
        Assert.HasCount(2, ready, "the next line is a record of its own");
        Assert.HasCount(KernelReportGrouper.MaxLines, RecordingEmitter.Line(ready[0]).Message.Split('\n'), "the report holds 2,000 lines");
        Assert.AreEqual("[7.0] after the report", RecordingEmitter.Line(ready[1]).Message, "the line after");
    }

    /// <summary>
    /// The end of the input flushes an open report, and does nothing when no report is open.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperFinishFlushesAnOpenReport()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        grouper.Add(Kernel(OomStart, 0), ready);
        grouper.Add(Kernel("[1.1] member", 1), ready);

        // Act
        grouper.Finish(ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, ready, "the report is emitted once");
        Assert.AreEqual($"{OomStart}\n[1.1] member", RecordingEmitter.Line(ready[0]).Message, "the report as it is");
    }

    /// <summary>
    /// Finishing without any line emits nothing.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperFinishWithoutLinesEmitsNothing()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Finish(ready);

        // Assert
        Assert.IsEmpty(ready, "nothing to emit");
    }

    /// <summary>
    /// A report of 2,000 lines of 16 KiB retains only head, tail and one line, whatever its length, and is emitted within the limit.
    /// </summary>
    [TestMethod]
    public void KernelReportGrouperAddRetainsAConstantAmountOfMemoryForALongReport()
    {
        // Arrange
        var grouper = new KernelReportGrouper();
        var ready = new List<DataRecord>();
        var before = RetainedBytes();

        // Act
        for (var index = 1; index < KernelReportGrouper.MaxLines; index++)
        {
            grouper.Add(Kernel(index == 1 ? Padded(OomStart) : Padded($"[3.{index:D6}] "), 0), ready);
            ready.Clear();
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(grouper);
        grouper.Add(Kernel(Padded("[4.0] last line "), 0), ready);

        // Assert
        Assert.IsLessThan(1024 * 1024, retained, "retained bytes after line 1,999, retaining the lines would be at least 32 MiB");
        Assert.HasCount(1, ready, "line 2,000 closes the report into one record");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(ready[0]).Message), "at most 16,384 UTF-8 bytes");
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

    /// <summary>
    /// Pads a text with letters to a line of 16,000 characters.
    /// </summary>
    /// <param name="start">The beginning of the line</param>
    /// <returns>The line</returns>
    private static string Padded(string start)
    {
        return start + new string('x', 16000 - start.Length);
    }

    /// <summary>
    /// Builds a record of a line of the kernel.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="seconds">The seconds after the base time</param>
    /// <param name="host">The host</param>
    /// <param name="program">The program</param>
    /// <param name="priority">The priority</param>
    /// <param name="truncated">Whether the line was cut</param>
    /// <returns>The record</returns>
    private static DataRecord Kernel(string message, int seconds, string host = "web-1", string program = "kernel", byte? priority = null, bool truncated = false)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = "syslog",
                   CapturedAt = _base.AddSeconds(seconds),
                   Data = new LogLine
                          {
                              Log = "syslog",
                              Host = host,
                              Program = program,
                              Priority = priority,
                              Message = message,
                              Truncated = truncated
                          }
               };
    }

    #endregion // Methods
}