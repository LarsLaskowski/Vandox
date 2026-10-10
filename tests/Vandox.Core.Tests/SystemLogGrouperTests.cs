using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SystemLogGrouper"/>
/// </summary>
[TestClass]
public class SystemLogGrouperTests
{
    #region Constants

    private const string OomStart = "[1.0] mariadbd invoked oom-killer: gfp_mask=0x100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0";
    private const string OomMember = "[1.1] CPU: 1 PID: 4242 Comm: mariadbd Not tainted 5.15.0-91-generic";
    private const string OomKill = "[1.9] Out of memory: Killed process 4242 (mariadbd) total-vm:8400000kB, anon-rss:7400000kB";
    private const int Mebibyte = 1024 * 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A kernel OOM report and a MariaDB entry that are interleaved are both grouped.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperAddGroupsInterleavedKernelAndMariaDbLines()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();
        var at = MariaDbSamples.Start;

        // Act
        grouper.Add(Kernel(OomStart, at), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("/usr/sbin/mariadbd: ready for connections."), at), ready);
        grouper.Add(Kernel(OomMember, at), ready);
        grouper.Add(MariaDbSamples.Record("Version: '10.6.12'", at), ready);
        grouper.Add(Kernel(OomKill, at), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(2, ready, "the report and the entry");
        Assert.AreEqual($"{OomStart}\n{OomMember}\n{OomKill}", RecordingEmitter.Line(ready[0]).Message, "the report ended first");
        Assert.AreEqual("kernel", RecordingEmitter.Line(ready[0]).Program, "program of the report");
        Assert.AreEqual("/usr/sbin/mariadbd: ready for connections.\nVersion: '10.6.12'", RecordingEmitter.Line(ready[1]).Message, "the entry");
        Assert.AreEqual(MariaDbEvents.Ready, RecordingEmitter.Line(ready[1]).Event, "event of the entry");
    }

    /// <summary>
    /// An entry that ends while a kernel report is open is emitted before the report record.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperAddEmitsAnEntryThatEndsInsideAReportBeforeTheReport()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();
        var at = MariaDbSamples.Start;

        // Act
        grouper.Add(Kernel(OomStart, at), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("first entry"), at, pid: 1), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("second entry"), at, pid: 2), ready);

        var beforeEnd = ready.ToList();

        grouper.Add(Kernel(OomKill, at), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, beforeEnd, "the first entry is emitted when the second header ends it, while the report is open");
        Assert.AreEqual("first entry", RecordingEmitter.Line(beforeEnd[0]).Message, "the first entry");
        Assert.HasCount(3, ready, "first entry, report, second entry");
        Assert.AreEqual("first entry", RecordingEmitter.Line(ready[0]).Message, "the entry before the report");
        Assert.AreEqual("kernel", RecordingEmitter.Line(ready[1]).Program, "the report");
        Assert.AreEqual("second entry", RecordingEmitter.Line(ready[2]).Message, "the open entry at the end");
    }

    /// <summary>
    /// A kernel report that ends while an entry is open is emitted before the entry record.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperAddEmitsAReportThatEndsInsideAnEntryBeforeTheEntry()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();
        var at = MariaDbSamples.Start;

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("entry"), at), ready);
        grouper.Add(Kernel(OomStart, at), ready);
        grouper.Add(Kernel(OomKill, at), ready);

        var afterReport = ready.ToList();

        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, afterReport, "the report ended and the entry is still open");
        Assert.AreEqual("kernel", RecordingEmitter.Line(afterReport[0]).Program, "the report");
        Assert.HasCount(2, ready, "report, entry");
        Assert.AreEqual("entry", RecordingEmitter.Line(ready[1]).Message, "the entry after the report");
    }

    /// <summary>
    /// Finish emits the open entry before the open report.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperFinishEmitsTheOpenEntryBeforeTheOpenReport()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();
        var at = MariaDbSamples.Start;

        grouper.Add(Kernel(OomStart, at), ready);
        grouper.Add(Kernel(OomMember, at), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("entry"), at), ready);

        var beforeFinish = ready.Count;

        // Act
        grouper.Finish(ready);

        // Assert
        Assert.AreEqual(0, beforeFinish, "the entry and the report are both open");
        Assert.HasCount(2, ready, "entry and report");
        Assert.AreEqual("entry", RecordingEmitter.Line(ready[0]).Message, "the entry first");
        Assert.AreEqual($"{OomStart}\n{OomMember}", RecordingEmitter.Line(ready[1]).Message, "the report second");
    }

    /// <summary>
    /// Five lines of another program, one call each, leave exactly those five records, in order.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperAddEmitsEveryPassThroughRecordExactlyOnce()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();
        var records = Enumerable.Range(0, 5).Select(index => MariaDbSamples.Record($"line {index}", MariaDbSamples.Start.AddSeconds(index), program: "sshd")).ToList();

        // Act
        for (var index = 0; index < records.Count; index++)
        {
            grouper.Add(records[index], ready);

            Assert.HasCount(index + 1, ready, $"one more record after call {index + 1}");
        }

        grouper.Finish(ready);

        // Assert
        Assert.HasCount(5, ready, "exactly five records");

        for (var index = 0; index < records.Count; index++)
        {
            Assert.AreSame(records[index], ready[index], $"record {index} in order");
        }
    }

    /// <summary>
    /// An entry that a header ends appears once, and the next entry once at the end.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperAddEmitsAnEntryThatAHeaderEndsExactlyOnce()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();

        // Act
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("one"), MariaDbSamples.Start), ready);
        grouper.Add(MariaDbSamples.Record(MariaDbSamples.Note("two"), MariaDbSamples.Start), ready);

        var afterHeader = ready.ToList();

        grouper.Add(MariaDbSamples.Record("sshd line", MariaDbSamples.Start, program: "sshd"), ready);
        grouper.Finish(ready);

        // Assert
        Assert.HasCount(1, afterHeader, "the first entry once");
        Assert.AreEqual("one", RecordingEmitter.Line(afterHeader[0]).Message, "the first entry");
        Assert.AreSequenceEqual(["one", "sshd line", "two"], ready.Select(record => RecordingEmitter.Line(record).Message).ToList(), "each record once, the open entry last");
    }

    /// <summary>
    /// 100,000 mixed records and then 100,000 continuation lines retain less than 1 MiB through both stages, and every record is emitted once.
    /// </summary>
    [TestMethod]
    public void SystemLogGrouperAddRetainsNoStagedRecords()
    {
        // Arrange
        var grouper = new SystemLogGrouper();
        var ready = new List<DataRecord>();
        var emitted = 0;
        var at = MariaDbSamples.Start;
        var before = RetainedBytes();

        // Act
        for (var index = 0; index < 100000; index++)
        {
            grouper.Add(MixedRecord(index, at), ready);
            Assert.IsLessThanOrEqualTo(3, ready.Count, $"call {index} emits at most the entry it ends, a report and the record itself");
            emitted += ready.Count;
            ready.Clear();
        }

        for (var index = 0; index < 100000; index++)
        {
            grouper.Add(MariaDbSamples.Record(new string('x', 100), at), ready);
            Assert.IsLessThanOrEqualTo(3, ready.Count, $"continuation call {index} emits at most the entry it ends and the record itself");
            emitted += ready.Count;
            ready.Clear();
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(grouper);
        grouper.Finish(ready);
        emitted += ready.Count;

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, a kept staging list would retain at least 15 MB");
        Assert.AreEqual(159838, emitted, "20,000 sshd lines, 20,000 reports, 20,000 entries (the last holds 162 lines) and 99,838 lines that did not join, each once");
    }

    /// <summary>
    /// Builds the record at a position of the cycle of five: a line of sshd, the start, a member and the end of an OOM report, and a header of mariadbd.
    /// </summary>
    /// <param name="index">The position</param>
    /// <param name="at">The time of every record</param>
    /// <returns>The record</returns>
    private static DataRecord MixedRecord(int index, DateTimeOffset at)
    {
        return (index % 5) switch
               {
                   0 => MariaDbSamples.Record(new string('s', 50), at, program: "sshd", pid: 22),
                   1 => Kernel(OomStart, at),
                   2 => Kernel(OomMember, at),
                   3 => Kernel(OomKill, at),
                   _ => MariaDbSamples.Record(MariaDbSamples.Note("x"), at)
               };
    }

    /// <summary>
    /// Builds a record of a line of the kernel.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="at">The time</param>
    /// <returns>The record</returns>
    private static DataRecord Kernel(string message, DateTimeOffset at)
    {
        return MariaDbSamples.Record(message, at, pid: 0, program: "kernel", priority: null, source: "syslog", log: "syslog");
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