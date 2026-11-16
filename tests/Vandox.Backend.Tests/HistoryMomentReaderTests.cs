using Vandox.Backend.History;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryMomentReader"/>
/// </summary>
[TestClass]
public class HistoryMomentReaderTests
{
    #region Constants

    private const string Process = RecordKind.ProcessSnapshot;
    private const string Connection = RecordKind.ConnectionSnapshot;

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
    /// The nearer stamp wins, the one at or before the moment on equal distance, a missing side leaves the other, and both missing give none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void ClosestPrefersNearerThenEarlierThenLowerId()
    {
        // Arrange
        var farBefore = HistorySamples.Stamp(1, Process, _at.AddMinutes(-3));
        var nearBefore = HistorySamples.Stamp(2, Process, _at.AddSeconds(-30));
        var nearAfter = HistorySamples.Stamp(3, Process, _at.AddSeconds(30));
        var farAfter = HistorySamples.Stamp(4, Process, _at.AddMinutes(3));
        var atMoment = HistorySamples.Stamp(5, Process, _at);

        // Act
        var nearerAfter = HistoryMomentReader.Closest(farBefore, nearAfter, _at);
        var nearerBefore = HistoryMomentReader.Closest(nearBefore, farAfter, _at);
        var equal = HistoryMomentReader.Closest(nearBefore, nearAfter, _at);
        var exact = HistoryMomentReader.Closest(atMoment, nearAfter, _at);
        var onlyBefore = HistoryMomentReader.Closest(farBefore, null, _at);
        var onlyAfter = HistoryMomentReader.Closest(null, farAfter, _at);
        var none = HistoryMomentReader.Closest(null, null, _at);

        // Assert
        Assert.AreSame(nearAfter, nearerAfter, "the after stamp is nearer");
        Assert.AreSame(nearBefore, nearerBefore, "the before stamp is nearer");
        Assert.AreSame(nearBefore, equal, "on equal distance the earlier stamp");
        Assert.AreSame(atMoment, exact, "a stamp at the moment is nearest");
        Assert.AreSame(farBefore, onlyBefore, "no stamp after the moment");
        Assert.AreSame(farAfter, onlyAfter, "no stamp before the moment");
        Assert.IsNull(none, "no stamp at all");
    }

    /// <summary>
    /// When the process snapshot shown is later than the moment and an earlier one exists, the time of the earlier one is offered.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ReadAsyncOffersTheSnapshotBefore()
    {
        // Arrange
        var earlier = _at.AddMinutes(-4).AddMilliseconds(400);

        using var laterShown = Reader([(1, earlier), (2, _at.AddMinutes(1))]);
        using var earlierShown = Reader([(1, _at.AddMinutes(-1)), (2, _at.AddMinutes(3))]);
        using var onlyAfter = Reader([(2, _at.AddMinutes(1))]);
        using var onlyBefore = Reader([(1, _at.AddMinutes(-1))]);

        // Act
        var later = await new HistoryMomentReader(laterShown).ReadAsync(_at, TestContext.CancellationToken);
        var nearer = await new HistoryMomentReader(earlierShown).ReadAsync(_at, TestContext.CancellationToken);
        var after = await new HistoryMomentReader(onlyAfter).ReadAsync(_at, TestContext.CancellationToken);
        var before = await new HistoryMomentReader(onlyBefore).ReadAsync(_at, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(_at, later.At, "the moment");
        Assert.AreEqual(_at.AddMinutes(1), later.Process?.CapturedAt, "the nearer snapshot after the moment is shown");
        Assert.AreEqual(earlier, later.ProcessBefore, "the latest snapshot before the moment is offered, with its sub-second time");
        Assert.AreEqual(_at.AddMinutes(-1), nearer.Process?.CapturedAt, "the nearer snapshot before the moment is shown");
        Assert.IsNull(nearer.ProcessBefore, "nothing is offered when the snapshot shown is not later");
        Assert.AreEqual(_at.AddMinutes(1), after.Process?.CapturedAt, "the only snapshot is after the moment");
        Assert.IsNull(after.ProcessBefore, "no earlier snapshot exists");
        Assert.IsNull(before.ProcessBefore, "the snapshot before is the one shown");
    }

    /// <summary>
    /// Both sides are read for each kind in a window of ten minutes around the moment, and a snapshot outside the window is not found.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ReadAsyncReadsBothSidesWithinTenMinutes()
    {
        // Arrange
        using var inner = Reader([(1, _at.AddMinutes(-10)), (2, _at.AddMinutes(10).AddSeconds(1))]);

        inner.Stamps.Add(HistorySamples.Stamp(3, Connection, _at.AddMinutes(10)));
        inner.Records[3] = HistorySamples.ConnectionRecord(3, "agent", _at.AddMinutes(10));

        // Act
        var moment = await new HistoryMomentReader(inner).ReadAsync(_at, TestContext.CancellationToken);

        // Assert
        var queries = inner.Calls.Where(call => call.Name == nameof(IHistoryReader.SnapshotStampAsync)).Select(call => (SnapshotQuery)call.Argument!).ToList();

        Assert.HasCount(4, queries, "two sides of two kinds");
        Assert.IsTrue(queries.All(query => query.At == _at && query.Window == TimeSpan.FromMinutes(10)), "every read is around the moment within ten minutes");
        Assert.AreEqual(2, queries.Count(query => query.Kind == Process), "two process reads");
        Assert.AreEqual(2, queries.Count(query => query.Kind == Connection), "two connection reads");
        Assert.AreEqual(2, queries.Count(query => query.Side == SnapshotSide.AtOrBefore), "two reads at or before");
        Assert.AreEqual(_at.AddMinutes(-10), moment.Process?.CapturedAt, "a snapshot exactly ten minutes before is found, the one after the window is not");
        Assert.AreEqual(_at.AddMinutes(10), moment.Connection?.CapturedAt, "a snapshot exactly ten minutes after is found");
    }

    /// <summary>
    /// The kinds are read one after the other, the record of a kind before the stamps of the next, and one record is read per kind.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [TestCategory("AC11")]
    public async Task ReadAsyncDecodesOneSnapshotAtATime()
    {
        // Arrange
        using var inner = Reader([(1, _at.AddMinutes(-2)), (2, _at.AddMinutes(1))]);

        inner.Stamps.Add(HistorySamples.Stamp(3, Connection, _at.AddMinutes(-1)));
        inner.Stamps.Add(HistorySamples.Stamp(4, Connection, _at.AddMinutes(2)));
        inner.Records[3] = HistorySamples.ConnectionRecord(3, "agent", _at.AddMinutes(-1));
        inner.Records[4] = HistorySamples.ConnectionRecord(4, "agent", _at.AddMinutes(2));

        // Act
        var moment = await new HistoryMomentReader(inner).ReadAsync(_at, TestContext.CancellationToken);

        // Assert
        var sequence = inner.Sequence;

        Assert.HasCount(6, sequence, "four stamp reads and one record read per kind");
        Assert.AreEqual("Stamp process_snapshot After|Stamp process_snapshot AtOrBefore", string.Join('|', sequence.Take(2).Order(StringComparer.Ordinal)), "the process stamps first");
        Assert.AreEqual("Snapshot 2", sequence[2], "then the process record that is closest");
        Assert.AreEqual("Stamp connection_snapshot After|Stamp connection_snapshot AtOrBefore", string.Join('|', sequence.Skip(3).Take(2).Order(StringComparer.Ordinal)), "then the connection stamps");
        Assert.AreEqual("Snapshot 3", sequence[5], "then the connection record that is closest");
        Assert.AreEqual(_at.AddMinutes(1), moment.Process?.CapturedAt, "the process snapshot shown");
        Assert.AreEqual(_at.AddMinutes(-1), moment.Connection?.CapturedAt, "the connection snapshot shown");
    }

    /// <summary>
    /// A snapshot captured within a second before the offered moment is found again at the moment rounded up to the whole second, and then it is the one shown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ReadAsyncFindsTheSnapshotBeforeAtTheRoundedUpMoment()
    {
        // Arrange
        var earlier = _at.AddMinutes(-4).AddMilliseconds(400);

        using var inner = Reader([(1, earlier), (2, _at.AddMinutes(1))]);

        var reader = new HistoryMomentReader(inner);
        var first = await reader.ReadAsync(_at, TestContext.CancellationToken);
        var rounded = HistoryView.RoundUpToSecond(first.ProcessBefore!.Value);

        // Act
        var second = await reader.ReadAsync(rounded, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(_at.AddMinutes(-4).AddSeconds(1), rounded, "rounded up to the whole second");
        Assert.AreEqual(earlier, second.Process?.CapturedAt, "the snapshot before is shown at the rounded moment");
        Assert.IsNull(second.ProcessBefore, "nothing earlier is offered again");
        Assert.AreEqual(rounded, second.At, "the moment of the panel");
    }

    /// <summary>
    /// Without a stamp, or when the record was removed meanwhile, the kind has no snapshot and no further record is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ReadAsyncOffersNoSnapshotWithoutStampOrRecord()
    {
        // Arrange
        using var inner = new FakeHistoryReader();

        inner.Stamps.Add(HistorySamples.Stamp(9, Process, _at.AddMinutes(-1)));

        // Act
        var moment = await new HistoryMomentReader(inner).ReadAsync(_at, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(moment.Process, "the record was removed");
        Assert.IsNull(moment.ProcessBefore, "nothing offered");
        Assert.IsNull(moment.Connection, "no connection stamp");
        Assert.HasCount(1, inner.Sequence.Where(entry => entry.StartsWith("Snapshot", StringComparison.Ordinal)).ToList(), "only the record of the stamp found is read");
    }

    /// <summary>
    /// Creates a reader that holds process snapshots.
    /// </summary>
    /// <param name="snapshots">The record IDs and capture times</param>
    /// <returns>The reader</returns>
    private static FakeHistoryReader Reader(IReadOnlyList<(long Id, DateTimeOffset CapturedAt)> snapshots)
    {
        var reader = new FakeHistoryReader();

        foreach (var (id, capturedAt) in snapshots)
        {
            reader.Stamps.Add(HistorySamples.Stamp(id, Process, capturedAt));
            reader.Records[id] = HistorySamples.ProcessRecord(id, "agent", capturedAt);
        }

        return reader;
    }

    #endregion // Methods
}