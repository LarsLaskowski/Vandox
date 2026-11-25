namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="MariaDbStatusFolder"/>
/// </summary>
[TestClass]
public class MariaDbStatusFolderTests
{
    #region Constants

    private const double Tolerance = 1e-9;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A record weighs the time up to the next record, at most two minutes, and the time without a record is unknown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbStatusFolderWeighsByTimeCappedAtTwoMinutes()
    {
        // Arrange
        var folder = Create(0, 20);

        folder.Add(At(0), "up", null, null);
        folder.Add(At(1), "down", null, null);
        folder.Add(At(10), "up", null, null);

        // Act
        var result = folder.Finish(null);

        // Assert
        Assert.AreEqual(2, MariaDbStatusFolder.Hold.TotalMinutes, "the cap is four intervals of 30 seconds");
        Assert.AreEqual(TimeSpan.FromMinutes(3), result.UpTime, "one minute up to the next record, two minutes for the last one");
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.DownTime, "the down record holds the cap, not the nine minutes to the next record");
        Assert.AreEqual(TimeSpan.Zero, result.NotAnsweringTime, "no record is not answering");
        Assert.AreEqual(TimeSpan.FromMinutes(15), result.UnknownTime, "the rest of the 20 minutes is unknown");
    }

    /// <summary>
    /// A record that holds across a step boundary is apportioned to both steps, and the share of a step is of its known time.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbStatusFolderSharesPerStep()
    {
        // Arrange
        var folder = Create(0, 20);

        folder.Add(At(9), "up", null, null);
        folder.Add(At(11), "down", null, null);

        // Act
        var result = folder.Finish(null);

        // Assert
        Assert.AreSequenceEqual([At(0), At(10)], result.Times.ToList(), "two steps");
        Assert.HasCount(2, result.Up, "one point per step");
        Assert.AreEqual(PointState.Value, result.Up[0].State, "the first step has known time");
        Assert.AreEqual(100d, result.Up[0].Average, Tolerance, "one minute known, all of it up");
        Assert.AreEqual(100d / 3, result.Up[1].Average, Tolerance, "one minute up of three known minutes");
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.UpTime, "the two minutes of the record");
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.DownTime, "the two minutes of the down record");
        Assert.AreEqual(TimeSpan.FromMinutes(16), result.UnknownTime, "the rest");
    }

    /// <summary>
    /// The time of a gap span, and the time after the completeness time of the gaps, is unknown, and the share of a step is of its known time only.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbStatusFolderGapTimeUnknown()
    {
        // Arrange
        var gapped = Create(0, 10, new UnknownSpans([(At(3), At(5))], null, At(10)));
        var bounded = Create(0, 10, new UnknownSpans([], At(6), At(10)));

        foreach (var minute in Enumerable.Range(0, 10))
        {
            gapped.Add(At(minute), "up", null, null);
            bounded.Add(At(minute), "up", null, null);
        }

        // Act
        var gap = gapped.Finish(null);
        var complete = bounded.Finish(null);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(8), gap.UpTime, "the 2 minutes of the gap are not up");
        Assert.AreEqual(TimeSpan.FromMinutes(2), gap.UnknownTime, "the gap time is unknown");
        Assert.AreEqual(100d, gap.Up[0].Average, Tolerance, "the share is of the known time, so 100 and not 80");
        Assert.AreEqual(TimeSpan.FromMinutes(6), complete.UpTime, "only the time before the completeness time is known");
        Assert.AreEqual(TimeSpan.FromMinutes(4), complete.UnknownTime, "the time after it is unknown");
    }

    /// <summary>
    /// A record before the range holds its time into the range, but it is no record of the range: no value of it is charted, and it is not counted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbStatusFolderCarriesRecordFromBeforeRange()
    {
        // Arrange
        var folder = Create(10, 30);

        folder.Add(At(9), "up", 123, 4);
        folder.Add(At(14), "down", null, null);

        // Act
        var result = folder.Finish(null);

        // Assert
        Assert.AreSequenceEqual([At(10), At(20)], result.Times.ToList(), "the steps of the range");
        Assert.AreEqual(TimeSpan.FromMinutes(1), result.UpTime, "the record at minute 9 holds up to minute 11");
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.DownTime, "the down record of the range");
        Assert.AreEqual(TimeSpan.FromMinutes(17), result.UnknownTime, "the rest");
        Assert.AreEqual(100d / 3, result.Up[0].Average, Tolerance, "the carried minute counts in the first step");
        Assert.AreEqual(PointState.Gap, result.Memory[0].State, "the value of the carried record is not charted: it was captured before the first step");
        Assert.AreEqual(PointState.Gap, result.Connections[0].State, "nor its connections");
        Assert.AreEqual(1, result.Records, "one record of the range");
    }

    /// <summary>
    /// A step without known time is a gap, a step of known down time is the value 0, and without any record every step is a gap and the whole range is unknown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbStatusFolderNoKnownTimeIsGap()
    {
        // Arrange
        var some = Create(0, 30);
        var none = Create(0, 30);

        some.Add(At(2), "down", null, null);

        // Act
        var result = some.Finish(null);
        var empty = none.Finish(null);

        // Assert
        Assert.AreEqual(PointState.Value, result.Up[0].State, "a step of known time is a value");
        Assert.AreEqual(0d, result.Up[0].Average, Tolerance, "known time all down is 0 percent up");
        Assert.AreEqual(PointState.Gap, result.Up[1].State, "a step without a record is a gap");
        Assert.AreEqual(PointState.Gap, result.Up[2].State, "the last step is a gap too");
        Assert.AreEqual(PointState.Gap, result.Memory[0].State, "a down record has no memory");
        Assert.HasCount(3, empty.Times, "the axis is built without records");
        Assert.IsTrue(empty.Up.All(point => point.State == PointState.Gap), "every step is a gap");
        Assert.IsTrue(empty.Memory.All(point => point.State == PointState.Gap), "no memory");
        Assert.IsTrue(empty.Connections.All(point => point.State == PointState.Gap), "no connections");
        Assert.AreEqual(TimeSpan.FromMinutes(30), empty.UnknownTime, "the whole range is unknown");
        Assert.AreEqual(TimeSpan.Zero, empty.UpTime, "no up time");
        Assert.IsNull(empty.LatestMemory, "no latest memory");
        Assert.AreEqual(0, empty.Records, "no record");
    }

    /// <summary>
    /// The memory and the connections of a step are the minimum, average, maximum and last of the up records of the step that carry them, and the latest memory is the latest one.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void MariaDbStatusFolderAveragesMemoryOfUpRecords()
    {
        // Arrange
        var folder = Create(0, 10);

        folder.Add(At(1), "up", 100, 1);
        folder.Add(At(2), "up", 300, 3);
        folder.Add(At(3), "down", 9999, 99);
        folder.Add(At(4), "up", null, 5);

        // Act
        var result = folder.Finish(At(-100));
        var memory = result.Memory[0];
        var connections = result.Connections[0];

        // Assert
        Assert.AreEqual(PointState.Value, memory.State, "the memory is a value");
        Assert.AreEqual(2, memory.Count, "two up records carry the memory");
        Assert.AreEqual(100d, memory.Minimum, Tolerance, "minimum");
        Assert.AreEqual(200d, memory.Average, Tolerance, "average of 100 and 300, the down record is not counted");
        Assert.AreEqual(300d, memory.Maximum, Tolerance, "maximum");
        Assert.AreEqual(300d, memory.Last, Tolerance, "last");
        Assert.AreEqual(At(2), memory.LastAt, "time of the last");
        Assert.AreEqual(3, connections.Count, "three up records carry the connections");
        Assert.AreEqual(1d, connections.Minimum, Tolerance, "minimum connections");
        Assert.AreEqual(3d, connections.Average, Tolerance, "average connections");
        Assert.AreEqual(5d, connections.Maximum, Tolerance, "maximum connections");
        Assert.AreEqual(5d, connections.Last, Tolerance, "last connections");
        Assert.AreEqual(300d, result.LatestMemory, "the latest memory is the one of the latest up record that carries it");
        Assert.AreEqual(At(2), result.LatestMemoryAt, "its time");
        Assert.AreEqual(At(-100), result.Oldest, "the oldest time is the one the store gave");
        Assert.AreEqual(4, result.Records, "all four records of the range");
    }

    /// <summary>
    /// The steps are the multiples of the step since the epoch that overlap the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void MariaDbStatusFolderStepsAreEpochMultiplesOverlappingRange()
    {
        // Arrange
        var folder = Create(3, 23);

        // Act
        var result = folder.Finish(null);

        // Assert
        Assert.AreSequenceEqual([At(0), At(10), At(20)], result.Times.ToList(), "the three steps that overlap minute 3 to minute 23");
        Assert.AreEqual(TimeSpan.FromMinutes(20), result.UnknownTime, "the range without records");
    }

    /// <summary>
    /// Gets an instant a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="minutes">The minutes</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset At(int minutes)
    {
        return Samples.Base.AddMinutes(minutes);
    }

    /// <summary>
    /// Creates a folder with a step of ten minutes over a range in minutes after <see cref="Samples.Base"/>, whose base lies on a step of ten minutes.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="unknown">The unknown spans, or <c>null</c> for none</param>
    /// <returns>The folder</returns>
    private static MariaDbStatusFolder Create(int from, int to, UnknownSpans? unknown = null)
    {
        var query = new MariaDbStatusQuery
                    {
                        From = At(from),
                        To = At(to),
                        Step = TimeSpan.FromMinutes(10)
                    };

        return new MariaDbStatusFolder(query, unknown ?? new UnknownSpans([], null, At(to)));
    }

    #endregion // Methods
}