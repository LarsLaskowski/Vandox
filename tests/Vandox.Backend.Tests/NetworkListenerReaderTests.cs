using Vandox.Backend.History;
using Vandox.Backend.Network;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkListenerReader"/>
/// </summary>
[TestClass]
public class NetworkListenerReaderTests
{
    #region Constants

    private const string LatestStamp = "LatestStamp connection_snapshot";
    private const string BeforeStamp = "Stamp connection_snapshot AtOrBefore";
    private const string AfterStamp = "Stamp connection_snapshot After";
    private const string NotComparedText = "Listeners of imported or older snapshots are not compared.";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _now = HistorySamples.Base;

    private static readonly HistoryRange _range = new(_now.AddDays(-1), _now);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Without a moment the latest snapshot of the source in the range is read: its stamp by the range and the source, its record, then the comparison stamp a day before it with a window of one day and its record, five calls at most, all with the token.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task NetworkListenerReaderReadsLatestInRange()
    {
        // Arrange
        using var history = new FakeHistoryReader();

        Add(history, NetworkSamples.Connections(1, "agent", _now.AddHours(-30), "all", NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(3, "agent", _now.AddHours(-5), "all", NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(2, "agent", _now.AddHours(-2), "all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)));
        Add(history, NetworkSamples.Connections(4, "other", _now.AddHours(-1), "all", NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(5, "agent", _now.AddHours(1), "all", NetworkSamples.Tcp(22)));

        // Act
        var view = await new NetworkListenerReader(history).ReadAsync("agent", _range, null, TestContext.CancellationToken);

        // Assert
        var latest = (LatestSnapshotQuery)history.Calls[0].Argument!;
        var comparison = (SnapshotQuery)history.Calls[2].Argument!;

        Assert.AreSequenceEqual([LatestStamp, "Snapshot 2", BeforeStamp, "Snapshot 1"], history.Sequence.ToArray(), "the latest stamp, its record, the comparison stamp, its record");
        Assert.AreEqual(RecordKind.ConnectionSnapshot, latest.Kind, "the kind of the latest read");
        Assert.AreEqual("agent", latest.Source, "the source of the latest read");
        Assert.AreEqual(_range.Start, latest.From, "the start of the range");
        Assert.AreEqual(_range.End, latest.To, "the end of the range");
        Assert.AreEqual(RecordKind.ConnectionSnapshot, comparison.Kind, "the kind of the comparison read");
        Assert.AreEqual("agent", comparison.Source, "the source of the comparison read");
        Assert.AreEqual(SnapshotSide.AtOrBefore, comparison.Side, "the comparison is at or before");
        Assert.AreEqual(_now.AddHours(-2).AddDays(-1), comparison.At, "a day before the shown snapshot");
        Assert.AreEqual(TimeSpan.FromDays(1), comparison.Window, "a window of one day");
        Assert.IsTrue(history.Calls.All(call => call.Token == TestContext.CancellationToken), "every call carries the token");
        Assert.AreEqual(_now.AddHours(-2), view.CapturedAt, "the latest snapshot of the source in the range");
        Assert.AreEqual("1 new listener since 2026-09-30 04:00:00 UTC.", view.Summary, "port 80 is new against the comparison snapshot");
        Assert.AreEqual("new|tcp|0.0.0.0:80|no owner|unknown", NetworkSamples.Cells(view.Table.Rows[0]), "the new listener first");
    }

    /// <summary>
    /// With a moment the snapshot of the source closest to it within 10 minutes is read as the history view reads it, and the comparison is a day before that snapshot, not before the moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task NetworkListenerReaderReadsClosestToMoment()
    {
        // Arrange
        var first = _now.AddHours(-2);
        var second = first.AddMinutes(30);
        var at = first.AddMinutes(20);

        using var history = new FakeHistoryReader();

        Add(history, NetworkSamples.Connections(1, "agent", second.AddDays(-1).AddHours(-1), "all", NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(2, "agent", first, "all", NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(3, "agent", second, "all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)));
        Add(history, NetworkSamples.Connections(4, "other", at.AddMinutes(1), "all", NetworkSamples.Tcp(22)));

        // Act
        var view = await new NetworkListenerReader(history).ReadAsync("agent", _range, at, TestContext.CancellationToken);

        // Assert
        var comparison = (SnapshotQuery)history.Calls[3].Argument!;

        Assert.AreSequenceEqual([BeforeStamp, AfterStamp, "Snapshot 3", BeforeStamp, "Snapshot 1"], history.Sequence.ToArray(), "the two moment stamps, the record, the comparison stamp, its record");
        Assert.IsTrue(history.Calls.Take(2).All(call => ((SnapshotQuery)call.Argument!).Source == "agent"), "the moment is read for the source");
        Assert.AreEqual(second.AddDays(-1), comparison.At, "the comparison is a day before the shown snapshot");
        Assert.AreEqual(second, view.CapturedAt, "the snapshot after the moment is the closer one");
        Assert.AreEqual("1 new listener since 2026-09-30 07:30:00 UTC.", view.Summary, "port 80 is new");
    }

    /// <summary>
    /// A range or a moment without a snapshot gives the text of E6 and reads no comparison, and so does a stamp whose record was removed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task NetworkListenerReaderNoSnapshotShowsText()
    {
        // Arrange
        using var empty = new FakeHistoryReader();
        using var removed = new FakeHistoryReader();

        removed.Stamps.Add(HistorySamples.Stamp(7, RecordKind.ConnectionSnapshot, _now.AddHours(-1)));
        removed.Records[7] = NetworkSamples.Connections(7, "other", _now.AddHours(-1), "all");
        removed.Records.Remove(7);

        // Act
        var inRange = await new NetworkListenerReader(empty).ReadAsync("agent", _range, null, TestContext.CancellationToken);
        var atMoment = await new NetworkListenerReader(empty).ReadAsync("agent", _range, _now.AddHours(-1), TestContext.CancellationToken);
        var gone = await new NetworkListenerReader(removed).ReadAsync("other", _range, null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("No connection snapshot of this source is stored in this range.", inRange.Text, "the text without a moment");
        Assert.AreEqual("No connection snapshot was stored within 10 minutes of this moment.", atMoment.Text, "the text with a moment");
        Assert.AreEqual("No connection snapshot of this source is stored in this range.", gone.Text, "a record removed between stamp and read");
        Assert.AreSequenceEqual([LatestStamp, BeforeStamp, AfterStamp], empty.Sequence.Take(3).ToArray(), "only stamps were read");
        Assert.IsEmpty(inRange.Table.Rows, "no rows");
        Assert.IsNull(inRange.CapturedAt, "no capture time");
    }

    /// <summary>
    /// The comparison snapshot is the latest of the source from 48 up to 24 hours before the shown one: an older one and one of another source are not used and the summary line says that none is marked new.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task NetworkListenerReaderReadsComparisonWindow()
    {
        // Arrange
        var shown = _now.AddHours(-2);

        using var inside = new FakeHistoryReader();
        using var outside = new FakeHistoryReader();

        Add(inside, NetworkSamples.Connections(1, "agent", shown.AddHours(-40), "all", NetworkSamples.Tcp(80)));
        Add(inside, NetworkSamples.Connections(2, "agent", shown.AddHours(-30), "all", NetworkSamples.Tcp(22)));
        Add(inside, NetworkSamples.Connections(3, "other", shown.AddHours(-25), "all", NetworkSamples.Tcp(80)));
        Add(inside, NetworkSamples.Connections(9, "agent", shown, "all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)));
        Add(outside, NetworkSamples.Connections(1, "agent", shown.AddHours(-60), "all", NetworkSamples.Tcp(22)));
        Add(outside, NetworkSamples.Connections(2, "agent", shown.AddHours(-10), "all", NetworkSamples.Tcp(22)));
        Add(outside, NetworkSamples.Connections(9, "agent", shown, "all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)));

        // Act
        var used = await new NetworkListenerReader(inside).ReadAsync("agent", _range, null, TestContext.CancellationToken);
        var none = await new NetworkListenerReader(outside).ReadAsync("agent", _range, null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("1 new listener since 2026-09-30 02:00:00 UTC.", used.Summary, "the latest snapshot of the source inside the window is the comparison");
        Assert.AreEqual("No connection snapshot of this source is stored from 24 to 48 hours before, so no listener is marked new.", none.Summary, "an older or a newer snapshot is not used");
        Assert.AreSequenceEqual([LatestStamp, "Snapshot 9", BeforeStamp], outside.Sequence.ToArray(), "the comparison stamp found nothing, so no comparison record was read");
        Assert.IsTrue(none.Table.Rows.All(row => NetworkSamples.Cells(row).StartsWith('|')), "nothing is marked");
    }

    /// <summary>
    /// A shown snapshot without inventory reads no comparison: only its stamp and its record, and the note of E9 alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task NetworkListenerReaderSkipsComparisonWithoutInventory()
    {
        // Arrange
        using var history = new FakeHistoryReader();

        Add(history, NetworkSamples.Connections(1, "agent", _now.AddHours(-30), "all", NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(2, "agent", _now.AddHours(-2), null, NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)));

        // Act
        var view = await new NetworkListenerReader(history).ReadAsync("agent", _range, null, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([LatestStamp, "Snapshot 2"], history.Sequence.ToArray(), "no comparison stamp or record is read");
        Assert.AreSequenceEqual([NotComparedText], view.PanelNotes.ToArray(), "the note of E9 alone");
        Assert.AreEqual(string.Empty, view.Summary, "no line of E7");
        Assert.HasCount(2, view.Table.Rows, "the listeners are shown");
    }

    /// <summary>
    /// A comparison snapshot without inventory is read and gives the note of E9, not that of an outcome of the comparer.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task NetworkListenerReaderNotesAComparisonWithoutInventory()
    {
        // Arrange
        using var history = new FakeHistoryReader();

        Add(history, NetworkSamples.Connections(1, "agent", _now.AddHours(-30), null, NetworkSamples.Tcp(22)));
        Add(history, NetworkSamples.Connections(2, "agent", _now.AddHours(-2), "all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)));

        // Act
        var view = await new NetworkListenerReader(history).ReadAsync("agent", _range, null, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([LatestStamp, "Snapshot 2", BeforeStamp, "Snapshot 1"], history.Sequence.ToArray(), "the comparison is read");
        Assert.AreSequenceEqual([NotComparedText], view.PanelNotes.ToArray(), "the note of E9");
        Assert.AreEqual(string.Empty, view.Summary, "no summary");
    }

    /// <summary>
    /// A failing read is not caught and no range is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task NetworkListenerReaderLetsAFailureThroughAndRefusesNoRange()
    {
        // Arrange
        using var history = new FakeHistoryReader
                            {
                                Failure = new InvalidOperationException("store failed")
                            };

        var reader = new NetworkListenerReader(history);

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync("agent", _range, null, TestContext.CancellationToken), "the failure");

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => reader.ReadAsync("agent", null!, null, TestContext.CancellationToken), "no range");

        // Assert
        Assert.AreEqual("store failed", thrown.Message, "the failure of the store");
    }

    /// <summary>
    /// Adds a record and its stamp to a history reader.
    /// </summary>
    /// <param name="history">The reader</param>
    /// <param name="record">The record</param>
    private static void Add(FakeHistoryReader history, StoredRecord record)
    {
        history.Stamps.Add(HistorySamples.Stamp(record.Id, RecordKind.ConnectionSnapshot, record.Record.CapturedAt));
        history.Records[record.Id] = record;
    }

    #endregion // Methods
}