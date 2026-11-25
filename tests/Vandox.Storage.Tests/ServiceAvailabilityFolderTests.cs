namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="ServiceAvailabilityFolder"/>
/// </summary>
[TestClass]
public class ServiceAvailabilityFolderTests
{
    #region Methods

    /// <summary>
    /// A state holds from its record to the next record of the unit, the last one to the end of the range, and the last record ID is reported.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderHoldsStateUntilNextRecord()
    {
        // Arrange
        var folder = Create(0, 60);

        folder.Add(1, At(0), "a.service", "active");
        folder.Add(2, At(20), "a.service", "failed");
        folder.Add(3, At(40), "a.service", "active");

        // Act
        var rows = folder.Finish();

        // Assert
        Assert.HasCount(1, rows, "one unit");
        Assert.AreEqual("a.service", rows[0].Row.Unit, "the unit");
        Assert.AreEqual(TimeSpan.FromMinutes(40), rows[0].Row.Available, "20 minutes from each active record");
        Assert.AreEqual(TimeSpan.FromMinutes(20), rows[0].Row.NotAvailable, "the failed record holds up to the next one");
        Assert.AreEqual(TimeSpan.Zero, rows[0].Row.Unknown, "nothing is unknown");
        Assert.AreEqual(3L, rows[0].LastId, "the ID of the last record of the unit");
    }

    /// <summary>
    /// A state holds at most 1 hour 5 minutes: the time beyond it up to the next record is unknown, and a next record exactly at the cap leaves no unknown time.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderCapsHoldAtOneHourFiveMinutes()
    {
        // Arrange
        var folder = Create(0, 180);

        folder.Add(1, At(0), "late.service", "active");
        folder.Add(2, At(120), "late.service", "active");
        folder.Add(3, At(0), "exact.service", "active");
        folder.Add(4, At(65), "exact.service", "active");

        // Act
        var rows = folder.Finish().ToDictionary(row => row.Row.Unit, row => row.Row);

        // Assert
        Assert.AreEqual(65, ServiceAvailabilityFolder.Hold.TotalMinutes, "the cap is the heartbeat plus slack");
        Assert.AreEqual(TimeSpan.FromMinutes(65 + 60), rows["late.service"].Available, "65 minutes from the first record, then 60 minutes from the second up to the end");
        Assert.AreEqual(TimeSpan.FromMinutes(55), rows["late.service"].Unknown, "the 55 minutes after the cap are unknown");
        Assert.AreEqual(TimeSpan.FromMinutes(130), rows["exact.service"].Available, "a next record exactly at the cap leaves the time known, the last record holds the cap");
        Assert.AreEqual(TimeSpan.FromMinutes(50), rows["exact.service"].Unknown, "the rest after the last hold is unknown");
    }

    /// <summary>
    /// The time inside a gap span is neither available nor not available, but unknown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderCountsGapTimeAsUnknown()
    {
        // Arrange
        var folder = Create(0, 60, new UnknownSpans([(At(10), At(20)), (At(30), At(35))], null, At(60)));

        folder.Add(1, At(0), "a.service", "active");
        folder.Add(2, At(50), "a.service", "active");
        folder.Add(3, At(0), "b.service", "failed");

        // Act
        var rows = folder.Finish().ToDictionary(row => row.Row.Unit, row => row.Row);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(45), rows["a.service"].Available, "60 minutes minus the 15 minutes of the gaps");
        Assert.AreEqual(TimeSpan.FromMinutes(15), rows["a.service"].Unknown, "the gap time");
        Assert.AreEqual(TimeSpan.FromMinutes(45), rows["b.service"].NotAvailable, "the failed state loses the gap time as well");
        Assert.AreEqual(TimeSpan.FromMinutes(15), rows["b.service"].Unknown, "the gap time of the second unit");
    }

    /// <summary>
    /// The time from the completeness time of the gaps to the end of the range is unknown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderCountsTimeAfterGapsCompleteUntilAsUnknown()
    {
        // Arrange
        var folder = Create(0, 60, new UnknownSpans([], At(30), At(60)));

        folder.Add(1, At(0), "a.service", "active");
        folder.Add(2, At(0), "b.service", "failed");

        // Act
        var rows = folder.Finish().ToDictionary(row => row.Row.Unit, row => row.Row);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(30), rows["a.service"].Available, "only the first half is known");
        Assert.AreEqual(TimeSpan.FromMinutes(30), rows["a.service"].Unknown, "the time after the completeness time is unknown");
        Assert.AreEqual(TimeSpan.FromMinutes(30), rows["b.service"].NotAvailable, "a state not available is cut the same way");
        Assert.AreEqual(TimeSpan.FromMinutes(30), rows["b.service"].Unknown, "the same unknown time");
    }

    /// <summary>
    /// Time before a unit's first record is unknown, a record before the range carries its state into it, and the hold ends at the cap after the last record.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC4")]
    public void ServiceAvailabilityFolderUnknownBeforeFirstRecord()
    {
        // Arrange
        var folder = Create(0, 120);

        folder.Add(1, At(-30), "carried.service", "failed");
        folder.Add(2, At(50), "late.service", "active");

        // Act
        var rows = folder.Finish().ToDictionary(row => row.Row.Unit, row => row.Row);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(35), rows["carried.service"].NotAvailable, "the record 30 minutes before the range holds 35 minutes into it");
        Assert.AreEqual(TimeSpan.FromMinutes(85), rows["carried.service"].Unknown, "the rest of the range is unknown");
        Assert.AreEqual(TimeSpan.FromMinutes(65), rows["late.service"].Available, "the first record holds 65 minutes");
        Assert.AreEqual(TimeSpan.FromMinutes(55), rows["late.service"].Unknown, "50 minutes before the record and 5 minutes after its hold");
    }

    /// <summary>
    /// The states active, reloading and refreshing are available, every other state is not available.
    /// </summary>
    /// <param name="state">The active state</param>
    /// <param name="available">Whether the time counts as available</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow("active", true)]
    [DataRow("reloading", true)]
    [DataRow("refreshing", true)]
    [DataRow("failed", false)]
    [DataRow("inactive", false)]
    [DataRow("activating", false)]
    [DataRow("deactivating", false)]
    [DataRow("maintenance", false)]
    [DataRow("Active", false)]
    [DataRow("", false)]
    public void ServiceAvailabilityFolderAvailableStatesAreActiveReloadingRefreshing(string state, bool available)
    {
        // Arrange
        var folder = Create(0, 30);

        folder.Add(1, At(0), "a.service", state);

        // Act
        var row = folder.Finish()[0].Row;

        // Assert
        Assert.AreEqual(available ? TimeSpan.FromMinutes(30) : TimeSpan.Zero, row.Available, $"the available time of '{state}'");
        Assert.AreEqual(available ? TimeSpan.Zero : TimeSpan.FromMinutes(30), row.NotAvailable, $"the not available time of '{state}'");
    }

    /// <summary>
    /// For every unit the three durations add up to the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderDurationsAddUpToRange()
    {
        // Arrange
        var folder = Create(0, 240, new UnknownSpans([(At(15), At(25)), (At(100), At(110)), (At(200), At(260))], At(230), At(240)));

        folder.Add(1, At(-40), "a.service", "active");
        folder.Add(2, At(30), "a.service", "failed");
        folder.Add(3, At(190), "a.service", "active");
        folder.Add(4, At(100), "b.service", "inactive");
        folder.Add(5, At(5), "c.service", "reloading");
        folder.Add(6, At(239), "d.service", "failed");

        // Act
        var rows = folder.Finish();

        // Assert
        Assert.HasCount(4, rows, "four units");

        foreach (var (row, _) in rows)
        {
            Assert.AreEqual(TimeSpan.FromMinutes(240), row.Available + row.NotAvailable + row.Unknown, $"the durations of {row.Unit} add up to the range");
            Assert.IsTrue(row.Available >= TimeSpan.Zero && row.NotAvailable >= TimeSpan.Zero && row.Unknown >= TimeSpan.Zero, $"no duration of {row.Unit} is negative");
        }
    }

    /// <summary>
    /// Beyond 256 units the largest names are evicted in favor of smaller ones: the 256 smallest names remain, however the records arrive.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderEvictsLargestNameBeyond256()
    {
        // Arrange
        var folder = Create(0, 60);

        foreach (var index in Enumerable.Range(0, 300).Reverse())
        {
            folder.Add(300 - index, At(index % 50), $"unit{index:D3}", "active");
        }

        // Act
        var rows = folder.Finish();

        // Assert
        Assert.AreEqual(256, ServiceAvailabilityFolder.MaxUnits, "the cap");
        Assert.HasCount(256, rows, "256 units are kept");
        Assert.AreSequenceEqual(Enumerable.Range(0, 256).Select(index => $"unit{index:D3}").ToList(), rows.Select(row => row.Row.Unit).Order(StringComparer.Ordinal).ToList(), "the 256 smallest names in ordinal order");
    }

    /// <summary>
    /// A name that was evicted never returns with a later record, and the kept units keep adding up.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServiceAvailabilityFolderEvictedNameNeverReturns()
    {
        // Arrange
        var folder = Create(0, 120);

        foreach (var index in Enumerable.Range(1, 256))
        {
            folder.Add(index, At(0), $"n{index:D3}", "active");
        }

        folder.Add(300, At(0), "n000", "active");
        folder.Add(301, At(10), "n256", "failed");
        folder.Add(302, At(30), "n100", "failed");

        // Act
        var rows = folder.Finish().ToDictionary(row => row.Row.Unit, row => row.Row);

        // Assert
        Assert.HasCount(256, rows, "the cap holds");
        Assert.IsFalse(rows.ContainsKey("n256"), "the evicted name did not come back with its later record");
        Assert.IsTrue(rows.ContainsKey("n000"), "the smaller name took the place");
        Assert.AreEqual(TimeSpan.FromMinutes(30), rows["n100"].Available, "the kept unit holds its first record up to the next one");
        Assert.AreEqual(TimeSpan.FromMinutes(65), rows["n100"].NotAvailable, "the kept unit goes on with the later record, which holds the cap");
    }

    /// <summary>
    /// The folder reports whether a name was skipped or evicted.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <param name="expected">Whether a name was dropped</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow("fewUnits", false)]
    [DataRow("exactly256", false)]
    [DataRow("skippedAbove", true)]
    [DataRow("evictedBelow", true)]
    public void ServiceAvailabilityFolderReportsDropped(string scenario, bool expected)
    {
        // Arrange
        var folder = Create(0, 60);
        var first = scenario == "evictedBelow" ? 1 : 0;
        var count = scenario == "fewUnits" ? 10 : 256;

        foreach (var index in Enumerable.Range(first, count))
        {
            folder.Add(index + 1, At(0), $"n{index:D3}", "active");
        }

        if (scenario == "skippedAbove")
        {
            folder.Add(900, At(1), "n999", "active");
        }

        if (scenario == "evictedBelow")
        {
            folder.Add(901, At(1), "n000", "active");
        }

        // Act
        var dropped = folder.Dropped;

        // Assert
        Assert.AreEqual(expected, dropped, $"whether a name was dropped in the scenario {scenario}");
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
    /// Creates a folder of a range in minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="unknown">The unknown spans, or <c>null</c> for none</param>
    /// <returns>The folder</returns>
    private static ServiceAvailabilityFolder Create(int from, int to, UnknownSpans? unknown = null)
    {
        return new ServiceAvailabilityFolder(At(from), At(to), unknown ?? new UnknownSpans([], null, At(to)));
    }

    #endregion // Methods
}