using Vandox.Backend.History;
using Vandox.Backend.Processes;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessesMomentReader"/>
/// </summary>
[TestClass]
public class ProcessesMomentReaderTests
{
    #region Constants

    private const string Process = RecordKind.ProcessSnapshot;
    private const double Mebibyte = ProcessesSamples.Mebibyte;

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _at = HistorySamples.Base.AddMinutes(30);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The snapshot shown is the closest of the source asked for, although a snapshot of another source is closer; only process snapshots are read, with the source in each stamp query.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesMomentReaderReadsTheClosestSnapshotOfTheSource()
    {
        // Arrange
        using var inner = Reader((1, "import", _at), (2, "agent", _at.AddMinutes(3)));

        // Act
        var moment = await new ProcessesMomentReader(inner).MomentAsync("agent", _at, TestContext.CancellationToken);

        // Assert
        var queries = inner.Calls.Where(call => call.Argument is SnapshotQuery).Select(call => (SnapshotQuery)call.Argument!).ToArray();

        Assert.AreEqual(_at, moment.At, "the moment");
        Assert.AreEqual(_at.AddMinutes(3), moment.Process?.CapturedAt, "the snapshot of the source, not the closer one of the other");
        Assert.AreEqual("agent", HistorySamples.Text(moment.Process?.Source ?? []), "the source of the snapshot shown");
        Assert.IsNull(moment.Connection, "no connection snapshot is part of this view");
        Assert.AreSequenceEqual(["Stamp process_snapshot AtOrBefore", "Stamp process_snapshot After", "Snapshot 2"], inner.Sequence.ToArray(), "two stamp reads and one snapshot read of the process kind");
        Assert.HasCount(2, queries, "two stamp queries");
        Assert.IsTrue(queries.All(query => query.Source == "agent" && query.Kind == Process && query.At == _at && query.Window == TimeSpan.FromMinutes(10)), "each query asks for the source within ten minutes of the moment");
        Assert.AreEqual(SnapshotSide.AtOrBefore, queries[0].Side, "the side at or before");
        Assert.AreEqual(SnapshotSide.After, queries[1].Side, "the side after");
    }

    /// <summary>
    /// Another source's snapshot is never shown: with none of the source within ten minutes the moment has no process snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesMomentReaderFindsNoSnapshotOfAnotherSource()
    {
        // Arrange
        using var inner = Reader((1, "import", _at), (2, "agent", _at.AddMinutes(11)), (3, "agent", _at.AddMinutes(-11)));

        // Act
        var moment = await new ProcessesMomentReader(inner).MomentAsync("agent", _at, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(moment.Process, "no snapshot of the source within ten minutes");
        Assert.IsNull(moment.ProcessBefore, "nothing before is offered without a snapshot");
    }

    /// <summary>
    /// When the snapshot shown is later than the moment and an earlier one of the same source exists, its time is offered; a snapshot of another source is not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessesMomentReaderOffersTheSnapshotBefore()
    {
        // Arrange
        var earlier = _at.AddMinutes(-4).AddMilliseconds(400);

        using var laterShown = Reader((1, "agent", earlier), (2, "agent", _at.AddMinutes(1)), (3, "import", _at.AddSeconds(-30)));
        using var earlierShown = Reader((1, "agent", _at.AddMinutes(-1)), (2, "agent", _at.AddMinutes(3)));
        using var onlyAfter = Reader((2, "agent", _at.AddMinutes(1)), (3, "import", _at.AddMinutes(-1)));

        // Act
        var later = await new ProcessesMomentReader(laterShown).MomentAsync("agent", _at, TestContext.CancellationToken);
        var nearer = await new ProcessesMomentReader(earlierShown).MomentAsync("agent", _at, TestContext.CancellationToken);
        var after = await new ProcessesMomentReader(onlyAfter).MomentAsync("agent", _at, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(_at.AddMinutes(1), later.Process?.CapturedAt, "the nearer snapshot after the moment is shown");
        Assert.AreEqual(earlier, later.ProcessBefore, "the latest snapshot of the source before the moment is offered, with its sub-second time");
        Assert.AreEqual(_at.AddMinutes(-1), nearer.Process?.CapturedAt, "the nearer snapshot before the moment is shown");
        Assert.IsNull(nearer.ProcessBefore, "nothing is offered when the snapshot shown is not later");
        Assert.AreEqual(_at.AddMinutes(1), after.Process?.CapturedAt, "the only snapshot of the source is after the moment");
        Assert.IsNull(after.ProcessBefore, "the snapshot of the other source is not offered");
    }

    /// <summary>
    /// The earlier point is the base of the comparison whichever point was given first; its snapshot is read first, each point with two stamp reads of the source and one snapshot read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesMomentReaderComparesTheEarlierPointAsBase()
    {
        // Arrange
        var earlier = HistorySamples.Base.AddMinutes(10);
        var later = HistorySamples.Base.AddMinutes(40);

        using var forward = Snapshots(earlier, later);
        using var backward = Snapshots(earlier, later);

        // Act
        var ordered = await new ProcessesMomentReader(forward).CompareAsync("agent", earlier, later, TestContext.CancellationToken);
        var reversed = await new ProcessesMomentReader(backward).CompareAsync("agent", later, earlier, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("2026-10-01 10:10:00 UTC to 2026-10-01 10:40:00 UTC (agent)", reversed.Heading, "the earlier point comes first in the heading");
        Assert.AreEqual(ordered.Heading, reversed.Heading, "the same heading for the same points");
        Assert.AreSequenceEqual(["a", "100.0 MiB", "150.0 MiB", "+50.0 MiB"], Cells(reversed.Table.Rows[0]).Take(4).ToArray(), "the earlier snapshot is the base");
        Assert.AreSequenceEqual(Cells(ordered.Table.Rows[0]), Cells(reversed.Table.Rows[0]), "the same row for the same points");
        Assert.AreSequenceEqual(["Stamp process_snapshot AtOrBefore", "Stamp process_snapshot After", "Snapshot 1", "Stamp process_snapshot AtOrBefore", "Stamp process_snapshot After", "Snapshot 2"], backward.Sequence.ToArray(), "the earlier snapshot is read first, six reads in all");
        Assert.IsTrue(backward.Calls.Where(call => call.Argument is SnapshotQuery).All(call => ((SnapshotQuery)call.Argument!).Source == "agent"), "every stamp query names the source");
    }

    /// <summary>
    /// The figures come from the snapshots: PSS when every program has one, and a snapshot that does not list every program gives the note and no new or gone program.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesMomentReaderBuildsTheFiguresFromTheSnapshots()
    {
        // Arrange
        var first = HistorySamples.Base.AddMinutes(10);
        var second = HistorySamples.Base.AddMinutes(40);
        var early = ProcessesSamples.Snapshot(1, "agent", first, ("a", (ulong)(100 * Mebibyte), 1.0, (ulong)(40 * Mebibyte)), ("b", (ulong)(10 * Mebibyte), 1.0, (ulong)(5 * Mebibyte)));
        var late = ProcessesSamples.Snapshot(2, "agent", second, ("a", (ulong)(100 * Mebibyte), 1.0, (ulong)(60 * Mebibyte)), ("c", (ulong)(10 * Mebibyte), 1.0, (ulong)(5 * Mebibyte)));

        ((ProcessSnapshot)late.Record.Data!).Complete = false;

        using var inner = new FakeHistoryReader();

        Add(inner, early);
        Add(inner, late);

        // Act
        var view = await new ProcessesMomentReader(inner).CompareAsync("agent", first, second, TestContext.CancellationToken);

        // Assert
        Assert.Contains("(PSS)", view.Table.Columns[1], "the memory of the snapshots is PSS");
        Assert.AreEqual("One of the two snapshots does not list every program, so no program is marked new or gone.", view.Note, "the later snapshot is not complete");
        Assert.AreSequenceEqual(["a", "40.0 MiB", "60.0 MiB", "+20.0 MiB"], Cells(view.Table.Rows[0]).Take(4).ToArray(), "the PSS figures of the program in both");
        Assert.IsTrue(view.Table.Rows.All(row => Cell(row, 6).Length == 0), "no program is marked");
        Assert.HasCount(3, view.Table.Rows, "the union of the programs");
    }

    /// <summary>
    /// A point without a snapshot within ten minutes gives a text that names the point as it was given, first or second, whichever is earlier.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesMomentReaderSaysWhichPointHasNoSnapshot()
    {
        // Arrange
        var near = HistorySamples.Base.AddMinutes(10);
        var far = HistorySamples.Base.AddHours(3);

        using var inner = Reader((1, "agent", near), (2, "import", far));

        // Act
        var secondMissing = await new ProcessesMomentReader(inner).CompareAsync("agent", near, far, TestContext.CancellationToken);
        var firstMissing = await new ProcessesMomentReader(inner).CompareAsync("agent", far, near, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("No process snapshot was stored within 10 minutes of the second point.", secondMissing.Text, "the second point has none");
        Assert.AreEqual("No process snapshot was stored within 10 minutes of the first point.", firstMissing.Text, "the first point has none");
        Assert.IsEmpty(secondMissing.Table.Rows, "no row without a snapshot");
        Assert.AreEqual(string.Empty, secondMissing.Heading, "no heading");
    }

    /// <summary>
    /// Two points whose closest snapshot is the same one give the text to choose points further apart and no rows.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task ProcessesMomentReaderSaysBothPointsShowOneSnapshot()
    {
        // Arrange
        var snapshot = HistorySamples.Base.AddMinutes(10);

        using var inner = Reader((1, "agent", snapshot));

        // Act
        var view = await new ProcessesMomentReader(inner).CompareAsync("agent", snapshot.AddMinutes(-1), snapshot.AddMinutes(2), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("Both points show the same snapshot; choose points further apart.", view.Text, "the text of the same snapshot");
        Assert.IsEmpty(view.Table.Rows, "no row");
        Assert.AreEqual(string.Empty, view.Closing, "no closing line");
    }

    /// <summary>
    /// Adds a stored snapshot to a fake reader.
    /// </summary>
    /// <param name="inner">The reader</param>
    /// <param name="record">The record</param>
    private static void Add(FakeHistoryReader inner, StoredRecord record)
    {
        inner.Stamps.Add(HistorySamples.Stamp(record.Id, Process, record.Record.CapturedAt));
        inner.Records[record.Id] = record;
    }

    /// <summary>
    /// Creates a reader with process snapshots, each with one program <c>nginx</c>.
    /// </summary>
    /// <param name="snapshots">The ID, source and capture time of each snapshot</param>
    /// <returns>The reader</returns>
    private static FakeHistoryReader Reader(params (long Id, string Source, DateTimeOffset At)[] snapshots)
    {
        var reader = new FakeHistoryReader();

        foreach (var (id, source, at) in snapshots)
        {
            Add(reader, ProcessesSamples.Snapshot(id, source, at, ("nginx", 4096, 1.0, null)));
        }

        return reader;
    }

    /// <summary>
    /// Creates a reader with two snapshots of the source <c>agent</c> in which the program <c>a</c> grows from 100 to 150 MiB.
    /// </summary>
    /// <param name="first">The time of the first snapshot</param>
    /// <param name="second">The time of the second snapshot</param>
    /// <returns>The reader</returns>
    private static FakeHistoryReader Snapshots(DateTimeOffset first, DateTimeOffset second)
    {
        var reader = new FakeHistoryReader();

        Add(reader, ProcessesSamples.Snapshot(1, "agent", first, ("a", (ulong)(100 * Mebibyte), 1.0, null)));
        Add(reader, ProcessesSamples.Snapshot(2, "agent", second, ("a", (ulong)(150 * Mebibyte), 2.0, null)));

        return reader;
    }

    /// <summary>
    /// Gives the text of a cell.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="index">The column</param>
    /// <returns>The text</returns>
    private static string Cell(SnapshotRowView row, int index)
    {
        return HistorySamples.Text(row.Cells[index]);
    }

    /// <summary>
    /// Gives the texts of the cells of a row.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The texts</returns>
    private static string[] Cells(SnapshotRowView row)
    {
        return [.. row.Cells.Select(HistorySamples.Text)];
    }

    #endregion // Methods
}