using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.ServiceHealth;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ThreadSnapshotView"/> and the cell texts of <see cref="ServicesView"/> for a thread's time
/// </summary>
[TestClass]
public class ThreadSnapshotViewTests
{
    #region Constants

    private const string CapText = "36500 d 0 h or more";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _at = new(2026, 10, 1, 3, 12, 40, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The list of the snapshots keeps the order it is given, newest first, with the time, the threads recorded, the running threads and the longest running time of each; the panel has the
    /// heading with the time of the snapshot, the table with its columns and the cells of each thread, and no note.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewWritesNoteListAndPanel()
    {
        // Arrange
        var stamps = new[]
                     {
                         ServicesSamples.Stamp(3, _at, 120, 12, 125),
                         ServicesSamples.Stamp(2, _at.AddHours(-5).AddMinutes(-12).AddSeconds(-40), 4096, 3, 7200)
                     };
        var shown = ServicesSamples.Snapshot(_at, 120, 12, 108, ServicesSamples.Thread(99, 125, "SELECT 1"), ServicesSamples.Thread(100, 30, "UPDATE t SET a = 1"));

        // Act
        var panel = ThreadSnapshotView.Build(stamps, shown, null);

        // Assert
        Assert.HasCount(2, panel.Stamps, "a row per snapshot");
        Assert.AreEqual(_at, panel.Stamps[0].At, "the capture time of the newest");
        Assert.AreSequenceEqual(["2026-10-01 03:12:40", "120", "12", "2 min"], [panel.Stamps[0].Time, panel.Stamps[0].Threads, panel.Stamps[0].Running, panel.Stamps[0].Longest], "the cells of the newest snapshot");
        Assert.AreSequenceEqual(["2026-09-30 22:00:00", "4096", "3", "2 h 0 min"], [panel.Stamps[1].Time, panel.Stamps[1].Threads, panel.Stamps[1].Running, panel.Stamps[1].Longest], "the cells of the older snapshot");
        Assert.AreEqual(string.Empty, panel.More, "all snapshots are listed");
        Assert.AreEqual("Threads of 2026-10-01 03:12:40 UTC", panel.Heading, "the heading of the panel");
        Assert.IsNotNull(panel.Threads, "the table of the threads");
        Assert.AreSequenceEqual(["Id", "User", "Host", "Database", "Command", "State", "Time", "Statement"], panel.Threads.Columns.ToArray(), "the columns");
        Assert.AreSequenceEqual(["99|app|10.0.0.5:51234|shop|Query|Sending data|2 min|SELECT 1", "100|app|10.0.0.5:51234|shop|Query|Sending data|30 s|UPDATE t SET a = 1"], panel.Threads.Rows.Select(ServicesSamples.Cells).ToArray(), "the threads in the order given");
        Assert.IsEmpty(panel.Notes, "no note");
    }

    /// <summary>
    /// A statement the agent shortened is marked as such after its text, and one it did not shorten is not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewMarksShortenedStatement()
    {
        // Arrange
        var shortened = ServicesSamples.Thread(1, 5, "SELECT * FROM big");
        var whole = ServicesSamples.Thread(2, 5, "SELECT 2");

        shortened.Truncated = true;

        var shown = ServicesSamples.Snapshot(_at, 3, 2, 1, shortened, whole);

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, 3, 2, 5)], shown, null);

        // Assert
        Assert.IsNotNull(panel.Threads, "the table of the threads");

        var first = HistorySamples.Text(panel.Threads.Rows[0].Cells[7]);
        var second = HistorySamples.Text(panel.Threads.Rows[1].Cells[7]);

        Assert.StartsWith("SELECT * FROM big", first, "the statement comes first");
        Assert.EndsWith("[statement shortened by the agent]", first, "the mark follows it");
        Assert.AreEqual("SELECT 2", second, "a whole statement has no mark");
    }

    /// <summary>
    /// The number of sleeping connections the agent recorded is written below the table and says they are not listed.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewSaysSleepingRecorded()
    {
        // Arrange
        var shown = ServicesSamples.Snapshot(_at, 33, 8, 25, ServicesSamples.Thread(1, 5, "SELECT 1"));

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, 33, 8, 5)], shown, null);

        // Assert
        Assert.AreEqual("25 sleeping connections recorded, not listed.", panel.Sleeping, "the sleeping connections");
    }

    /// <summary>
    /// At most 100 threads are listed and the line below the table says how many more running threads there are; the 101st row only tells that there are more.
    /// </summary>
    /// <param name="rows">The rows the store gives</param>
    /// <param name="running">The running threads of the snapshot</param>
    /// <param name="listed">The rows the table lists</param>
    /// <param name="more">The expected line</param>
    [TestMethod]
    [TestCategory("AC8")]
    [TestCategory("AC9")]
    [DataRow(101, 130, 100, "and 30 more threads")]
    [DataRow(101, 101, 100, "and 1 more threads")]
    [DataRow(100, 100, 100, "")]
    [DataRow(3, 3, 3, "")]
    public void ThreadSnapshotViewListsAtMostHundredThreads(int rows, int running, int listed, string more)
    {
        // Arrange
        var threads = Enumerable.Range(1, rows).Select(index => ServicesSamples.Thread((ulong)index, 10, $"SELECT {index}")).ToArray();
        var shown = ServicesSamples.Snapshot(_at, running + 5, running, 5, threads);

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, running + 5, running, 10)], shown, null);

        // Assert
        Assert.IsNotNull(panel.Threads, "the table of the threads");
        Assert.HasCount(listed, panel.Threads.Rows, "the rows of the table");
        Assert.AreEqual("1", HistorySamples.Text(panel.Threads.Rows[0].Cells[0]), "the first thread of the agent's order");
        Assert.AreEqual(more, panel.Threads.More, "the line below the table");
    }

    /// <summary>
    /// The list shows at most 50 snapshots and says that older ones are not listed when the store gave one more, and says nothing for 50.
    /// </summary>
    /// <param name="count">The stamps the store gives</param>
    /// <param name="listed">The rows of the list</param>
    /// <param name="more">The expected text</param>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(51, 50, "Older snapshots are not listed; narrow the range.")]
    [DataRow(50, 50, "")]
    [DataRow(1, 1, "")]
    public void ThreadSnapshotViewSaysOlderSnapshotsAreNotListed(int count, int listed, string more)
    {
        // Arrange
        var stamps = Enumerable.Range(0, count).Select(index => ServicesSamples.Stamp(count - index, _at.AddMinutes(-index), 20, 11, 30)).ToArray();

        // Act
        var panel = ThreadSnapshotView.Build(stamps, ServicesSamples.Snapshot(_at, 20, 11, 9, ServicesSamples.Thread(1, 5, "SELECT 1")), null);

        // Assert
        Assert.HasCount(listed, panel.Stamps, "the rows of the list");
        Assert.AreEqual(_at, panel.Stamps[0].At, "the newest first");
        Assert.AreEqual(more, panel.More, "the text below the list");
    }

    /// <summary>
    /// Every external text of a thread is shown as the display of its text: control and direction characters as visible escapes, markup as text, the cells of the thread at 256 characters
    /// and the statement at 2,048.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewEscapesThreadTexts()
    {
        // Arrange
        var evil = $"evil{ServicesSamples.Rlo}<script>alert(1)</script>{ServicesSamples.Zwsp}{new string('x', 300)}";
        var statement = $"SELECT '{ServicesSamples.Rlo}<b>';{new string('y', 3000)}";
        var thread = new MariaDbThread
                     {
                         Id = 1,
                         User = evil,
                         Host = evil,
                         Db = evil,
                         Command = evil,
                         State = evil,
                         TimeSeconds = 5,
                         Info = statement
                     };

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, 1, 1, 5)], ServicesSamples.Snapshot(_at, 1, 1, 0, thread), null);

        // Assert
        Assert.IsNotNull(panel.Threads, "the table of the threads");

        var cells = panel.Threads.Rows[0].Cells;
        var expected = ServicesSamples.Describe(SnapshotCells.Runs(evil));

        foreach (var column in new[] { 1, 2, 3, 4, 5 })
        {
            Assert.AreEqual(expected, ServicesSamples.Describe(cells[column]), $"column {panel.Threads.Columns[column]} is the display of its text");
        }

        Assert.AreEqual(ServicesSamples.Describe(LogText.Runs(statement, 2048, 256)), ServicesSamples.Describe(cells[7]), "the statement is the display of its text capped at 2,048 characters");
        Assert.IsLessThan(statement.Length, HistorySamples.Text(cells[7]).Length, "the statement is capped");
        Assert.Contains(run => run.Escaped, cells[1], "the hostile text needs escapes");
        Assert.DoesNotContain(ServicesSamples.Rlo, string.Concat(cells.SelectMany(cell => cell).Where(run => run.Escaped == false).Select(run => run.Text)), "the direction character is only escaped");
    }

    /// <summary>
    /// Without a snapshot the panel says so: none in the range, or none within 10 minutes of the moment.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewShowsNoSnapshotTexts()
    {
        // Arrange
        var stamp = ServicesSamples.Stamp(3, _at.AddHours(-3), 20, 11, 30);

        // Act
        var none = ThreadSnapshotView.Build([], null, null);
        var atMoment = ThreadSnapshotView.Build([stamp], null, _at);

        // Assert
        Assert.AreSequenceEqual(["No thread snapshot is stored in this range."], none.Notes.ToArray(), "none in the range");
        Assert.IsEmpty(none.Stamps, "no list");
        Assert.IsNull(none.Threads, "no table");
        Assert.AreEqual(string.Empty, none.Heading, "no heading");
        Assert.AreSequenceEqual(["No thread snapshot was stored within 10 minutes of this moment."], atMoment.Notes.ToArray(), "none near the moment");
        Assert.HasCount(1, atMoment.Stamps, "the list of the range stays");
        Assert.IsNull(atMoment.Threads, "no table");
        Assert.AreEqual(string.Empty, atMoment.Heading, "no heading without a snapshot near the moment");
    }

    /// <summary>
    /// A thread's time and the longest running time of a snapshot are written as an age below 36,500 days and as <c>36500 d 0 h or more</c> from there on, the largest 64-bit value included,
    /// and the identifier keeps all its digits.
    /// </summary>
    /// <param name="seconds">The seconds</param>
    /// <param name="expected">The expected text</param>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(0UL, "0 s")]
    [DataRow(125UL, "2 min")]
    [DataRow(3153599999UL, "36499 d 23 h")]
    [DataRow(3153600000UL, CapText)]
    [DataRow(3153600001UL, CapText)]
    [DataRow(18446744073709551615UL, CapText)]
    public void ThreadSnapshotViewWritesTimeAtCapAsOrMore(ulong seconds, string expected)
    {
        // Arrange
        var thread = ServicesSamples.Thread(ulong.MaxValue, seconds, "SELECT 1");
        var longest = (long)Math.Min(seconds, (ulong)ThreadSnapshot.MaxSeconds);

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, 1, 1, longest)], ServicesSamples.Snapshot(_at, 1, 1, 0, thread), null);
        var text = ServicesView.Seconds(seconds);

        // Assert
        Assert.AreEqual(expected, text, "the text of the seconds");
        Assert.IsNotNull(panel.Threads, "the table of the threads");
        Assert.AreEqual("18446744073709551615", HistorySamples.Text(panel.Threads.Rows[0].Cells[0]), "the identifier with all its digits");
        Assert.AreEqual(expected, HistorySamples.Text(panel.Threads.Rows[0].Cells[6]), "the time of the thread");
        Assert.AreEqual(expected, panel.Stamps[0].Longest, "the longest running time of the snapshot");
    }

    /// <summary>
    /// A snapshot without a running thread writes <c>none</c> as the longest running time, a snapshot without sleeping connections writes no line about them, and an empty list of the range with
    /// a moment says that no snapshot is stored in the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewWritesNoneSleepingZeroAndEmptyListWithMoment()
    {
        // Arrange
        var shown = ServicesSamples.Snapshot(_at, 1, 1, 0, ServicesSamples.Thread(1, 5, "SELECT 1"));

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, 1, 0, null)], shown, null);
        var empty = ThreadSnapshotView.Build([], null, _at);

        // Assert
        Assert.AreEqual("none", panel.Stamps[0].Longest, "no running thread, no longest time");
        Assert.AreEqual(string.Empty, panel.Sleeping, "no sleeping connection, no line");
        Assert.AreSequenceEqual(["No thread snapshot is stored in this range."], empty.Notes.ToArray(), "an empty range says so also with a moment");
        Assert.AreEqual(string.Empty, empty.Heading, "no heading");
    }

    /// <summary>
    /// The line about more threads counts at least one when the store gave the 101st row although the snapshot counts fewer running threads.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ThreadSnapshotViewCountsAtLeastOneMoreThread()
    {
        // Arrange
        var threads = Enumerable.Range(1, 101).Select(index => ServicesSamples.Thread((ulong)index, 10, "SELECT 1")).ToArray();

        // Act
        var panel = ThreadSnapshotView.Build([ServicesSamples.Stamp(3, _at, 101, 50, 10)], ServicesSamples.Snapshot(_at, 101, 50, 0, threads), null);

        // Assert
        Assert.IsNotNull(panel.Threads, "the table");
        Assert.AreEqual("and 1 more threads", panel.Threads.More, "at least one more");
    }

    #endregion // Methods
}