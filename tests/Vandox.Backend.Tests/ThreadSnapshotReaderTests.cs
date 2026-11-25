using Vandox.Backend.History;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ThreadSnapshotReader"/>
/// </summary>
[TestClass]
public class ThreadSnapshotReaderTests
{
    #region Fields

    private static readonly DateTimeOffset _now = HistorySamples.Base;

    private static readonly HistoryRange _range = new(_now.AddDays(-30), _now);

    private static readonly DateTimeOffset _first = _now.AddDays(-3);

    private static readonly DateTimeOffset _second = _now.AddDays(-4);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Without a moment the reader lists the snapshots of the range with the limit of 51 and reads the newest one: two store calls, the second for the record of the first stamp, both
    /// with the token; the panel lists both stamps and shows the newest.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ThreadSnapshotReaderUsesLatestWithoutAt()
    {
        // Arrange
        using var reader = Fake();

        // Act
        var panel = await new ThreadSnapshotReader(reader).ReadAsync(_range, null, TestContext.CancellationToken);

        // Assert
        var calls = reader.Calls;

        Assert.AreSequenceEqual([nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync), nameof(IMariaDbHistoryReader.ThreadSnapshotAsync)], calls.Select(call => call.Name).ToArray(), "the list, then the newest record");

        var query = (ThreadSnapshotQuery)calls[0].Argument!;

        Assert.AreEqual(_range.Start, query.From, "the list from the start of the range");
        Assert.AreEqual(_range.End, query.To, "the list to the end of the range");
        Assert.AreEqual(ThreadSnapshotQuery.MaxLimit, query.Limit, "51 stamps at most");
        Assert.AreEqual(7L, calls[1].Argument, "the record of the newest stamp");
        Assert.IsTrue(calls.All(call => call.Token == TestContext.CancellationToken), "every call carries the token");
        Assert.HasCount(2, panel.Stamps, "both stamps are listed");
        Assert.AreEqual("Threads of 2026-09-28 10:00:00 UTC", panel.Heading, "the newest snapshot is shown");
        Assert.IsNotNull(panel.Threads, "its threads");
    }

    /// <summary>
    /// With a moment the reader also asks for the snapshot closest to it within the window of the history view and reads the record of that one: three store calls at most.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ThreadSnapshotReaderUsesClosestWithAt()
    {
        // Arrange
        using var reader = Fake();

        reader.Closest = ServicesSamples.Stamp(8, _second.AddMinutes(1), 40, 12, 125);
        reader.Snapshots[8] = ServicesSamples.Snapshot(_second.AddMinutes(1), 40, 12, 28, ServicesSamples.Thread(5, 125, "SELECT 5"));

        var moment = _second.AddMinutes(4);

        // Act
        var panel = await new ThreadSnapshotReader(reader).ReadAsync(_range, moment, TestContext.CancellationToken);

        // Assert
        var calls = reader.Calls;

        Assert.HasCount(3, calls, "at most three store calls");
        Assert.ContainsSingle(call => call.Name == nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync), calls, "the list of the range");
        Assert.AreEqual((moment, HistoryView.MomentWindow), calls.Single(call => call.Name == nameof(IMariaDbHistoryReader.ClosestThreadSnapshotAsync)).Argument, "the moment and the window of 10 minutes");
        Assert.AreEqual(8L, calls.Single(call => call.Name == nameof(IMariaDbHistoryReader.ThreadSnapshotAsync)).Argument, "the record of the closest stamp");
        Assert.IsTrue(calls.All(call => call.Token == TestContext.CancellationToken), "every call carries the token");
        Assert.AreEqual("Threads of 2026-09-27 10:01:00 UTC", panel.Heading, "the closest snapshot is shown, not the newest");
        Assert.HasCount(2, panel.Stamps, "the list of the range");
    }

    /// <summary>
    /// A moment without a snapshot within the window gives the text for the moment and reads no record; a range without a snapshot gives the text for the range after one call.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task ThreadSnapshotReaderReadsNoRecordWithoutSnapshot()
    {
        // Arrange
        using var near = Fake();
        using var empty = new FakeMariaDbHistoryReader();

        // Act
        var atMoment = await new ThreadSnapshotReader(near).ReadAsync(_range, _now.AddDays(-20), TestContext.CancellationToken);
        var none = await new ThreadSnapshotReader(empty).ReadAsync(_range, null, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["No thread snapshot was stored within 10 minutes of this moment."], atMoment.Notes.ToArray(), "the text for the moment");
        Assert.IsEmpty(near.CallsOf(nameof(IMariaDbHistoryReader.ThreadSnapshotAsync)), "no record is read");
        Assert.AreSequenceEqual(["No thread snapshot is stored in this range."], none.Notes.ToArray(), "the text for the range");
        Assert.HasCount(1, empty.Calls, "one call for an empty range");
    }

    /// <summary>
    /// A failure of the store passes to the caller, which shows its own text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ThreadSnapshotReaderPassesAStoreFailure()
    {
        // Arrange
        using var reader = Fake();

        reader.Failure = new StoreException("the store failed");

        // Act and assert
        var thrown = await Assert.ThrowsExactlyAsync<StoreException>(() => new ThreadSnapshotReader(reader).ReadAsync(_range, null, TestContext.CancellationToken), "the failure of the store");

        Assert.AreEqual("the store failed", thrown.Message, "the failure itself");
    }

    /// <summary>
    /// Creates a reader with two stamps in the range and the record of the newest.
    /// </summary>
    /// <returns>The reader</returns>
    private static FakeMariaDbHistoryReader Fake()
    {
        var reader = new FakeMariaDbHistoryReader();

        reader.Stamps.Add(ServicesSamples.Stamp(7, _first, 30, 11, 60));
        reader.Stamps.Add(ServicesSamples.Stamp(6, _second, 40, 12, 125));
        reader.Snapshots[7] = ServicesSamples.Snapshot(_first, 30, 11, 19, ServicesSamples.Thread(4, 60, "SELECT 4"));
        reader.Snapshots[6] = ServicesSamples.Snapshot(_second, 40, 12, 28, ServicesSamples.Thread(3, 125, "SELECT 3"));

        return reader;
    }

    #endregion // Methods
}