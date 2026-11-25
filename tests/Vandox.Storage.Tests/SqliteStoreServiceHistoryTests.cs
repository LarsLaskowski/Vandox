using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the service availability read of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreServiceHistoryTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A unit recorded throughout the range reads its available, not available and unknown time and its latest state with its time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC3")]
    public async Task SqliteStoreServiceAvailabilityReadsTimesAndLatestStateOfRecordedUnit()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await InsertAsync(directory, "nginx.service", "active", "running", 0);
        await InsertAsync(directory, "nginx.service", "failed", "failed", 60);
        await InsertAsync(directory, "nginx.service", "failed", "failed", 120);
        await InsertAsync(directory, "nginx.service", "active", "running", 180);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 240), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, result.Units, "one unit");

        var unit = result.Units[0];

        Assert.AreEqual("nginx.service", unit.Unit, "the unit");
        Assert.AreEqual(TimeSpan.FromHours(2), unit.Available, "two hours active");
        Assert.AreEqual(TimeSpan.FromHours(2), unit.NotAvailable, "two hours failed");
        Assert.AreEqual(TimeSpan.Zero, unit.Unknown, "recorded throughout");
        Assert.AreEqual("active", unit.ActiveState, "the latest active state");
        Assert.AreEqual("running", unit.SubState, "the latest sub state");
        Assert.AreEqual(At(180), unit.StateAt, "the time of the latest record");
        Assert.AreEqual(1, result.UnitCount, "one distinct unit");
        Assert.IsNull(result.GapsCompleteUntil, "no gap was left unread");
        Assert.IsFalse(result.SystemdGaps, "no gap of the systemd collector");
    }

    /// <summary>
    /// Of more than 256 units only the first 256 in ordinal order of their names are evaluated, whatever the order of their records, and the count names all units.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreServiceAvailabilityEvaluatesFirst256UnitsByName()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertUnitsAsync(directory.Path, 300, At(10), true, TestContext.CancellationToken);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 60), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(Enumerable.Range(0, 256).Select(index => $"unit{index:D3}").ToList(), result.Units.Select(unit => unit.Unit).Order(StringComparer.Ordinal).ToList(), "the 256 smallest names");
        Assert.AreEqual(300, result.UnitCount, "all distinct units of the window are counted");
        Assert.IsTrue(result.Units.All(unit => unit.Available > TimeSpan.Zero), "every evaluated unit has its time");
    }

    /// <summary>
    /// The distinct units are counted past 256 units, and up to 256 the count is the number of units listed.
    /// </summary>
    /// <param name="units">The number of units with a record in the range</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow(100)]
    [DataRow(256)]
    [DataRow(257)]
    [DataRow(300)]
    public async Task SqliteStoreServiceAvailabilityCountsDistinctUnitsOnlyPast256(int units)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertUnitsAsync(directory.Path, units, At(10), false, TestContext.CancellationToken);
        await InsertAsync(directory, "unit000", "failed", "failed", 30);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 60), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(units, result.UnitCount, "a second record of a unit does not add a unit");
        Assert.HasCount(Math.Min(units, 256), result.Units, "at most 256 units are listed");
    }

    /// <summary>
    /// A state before the range holds into it up to 1 hour 5 minutes after its record, and the unit is listed with that time and its state.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC3")]
    public async Task SqliteStoreServiceAvailabilityCarriesStateFromBeforeTheRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await InsertAsync(directory, "carried.service", "failed", "dead", -30);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 180), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, result.Units, "the unit with a state carried into the range is listed");

        var unit = result.Units[0];

        Assert.AreEqual(TimeSpan.Zero, unit.Available, "nothing available");
        Assert.AreEqual(TimeSpan.FromMinutes(35), unit.NotAvailable, "the record 30 minutes before the range holds 35 minutes into it");
        Assert.AreEqual(TimeSpan.FromMinutes(145), unit.Unknown, "the rest of the range is unknown");
        Assert.AreEqual("failed", unit.ActiveState, "the state of the carried record");
        Assert.AreEqual("dead", unit.SubState, "its sub state");
        Assert.AreEqual(At(-30), unit.StateAt, "its time");
    }

    /// <summary>
    /// A unit whose only record lies exactly 65 minutes before the range holds nothing into it and is not listed; one 64 minutes before is.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreServiceAvailabilitySkipsUnitWhoseHoldEndsAtFrom()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await InsertAsync(directory, "0lead.service", "active", "running", -65);
        await InsertAsync(directory, "0near.service", "active", "running", -64);
        await InsertAsync(directory, "b.service", "active", "running", 10);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 120), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["0near.service", "b.service"], result.Units.Select(unit => unit.Unit).Order(StringComparer.Ordinal).ToList(), "the unit that holds nothing into the range is not listed");
        Assert.AreEqual(2, result.UnitCount, "and not counted");
        Assert.AreEqual(TimeSpan.FromMinutes(1), result.Units.Single(unit => unit.Unit == "0near.service").Available, "the unit 64 minutes before holds one minute into the range");
    }

    /// <summary>
    /// The distinct count past 256 units does not count a unit whose only record holds nothing into the range, and counts one whose record holds a minute.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreServiceAvailabilityCountExcludesUnitWhoseHoldEndsAtFrom()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertUnitsAsync(directory.Path, 257, At(10), false, TestContext.CancellationToken);
        await InsertAsync(directory, "0lead.service", "active", "running", -65);
        await InsertAsync(directory, "0near.service", "active", "running", -64);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 120), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(258, result.UnitCount, "257 units of the range and the one 64 minutes before it");
        Assert.HasCount(256, result.Units, "256 units are evaluated");
        Assert.AreEqual("0near.service", result.Units.Select(unit => unit.Unit).Order(StringComparer.Ordinal).First(), "the unit with the smaller name is evaluated");
        Assert.DoesNotContain("0lead.service", result.Units.Select(unit => unit.Unit).ToList(), "the unit that holds nothing into the range is not evaluated");
    }

    /// <summary>
    /// Each unit reads the state, sub state and time of its latest record before the end of the range, the later record of one capture time winning, and units are told apart in ordinal order of their names.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreServiceAvailabilityReadsLatestStateOfEachUnit()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await InsertAsync(directory, "b.service", "inactive", "dead", -30);
        await InsertAsync(directory, "b.service", "active", "running", 20);
        await InsertAsync(directory, "b.service", "failed", "failed", 20);
        await InsertAsync(directory, "B.service", "active", "exited", 30);
        await InsertAsync(directory, "a.service", "active", "running", 10);
        await InsertAsync(directory, "a.service", "failed", "failed", 60);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 60), TestContext.CancellationToken);

        // Assert
        var units = result.Units.ToDictionary(unit => unit.Unit);

        Assert.HasCount(3, units, "three units of different names");
        Assert.AreEqual("failed", units["b.service"].ActiveState, "the later record of the same capture time wins");
        Assert.AreEqual("failed", units["b.service"].SubState, "its sub state");
        Assert.AreEqual(At(20), units["b.service"].StateAt, "its time");
        Assert.AreEqual("active", units["B.service"].ActiveState, "the unit of upper case is another unit");
        Assert.AreEqual("exited", units["B.service"].SubState, "its sub state");
        Assert.AreEqual("active", units["a.service"].ActiveState, "a record at the end of the range is not read");
        Assert.AreEqual(At(10), units["a.service"].StateAt, "the time of the last record before the end");
        Assert.AreSequenceEqual(["B.service", "a.service", "b.service"], result.Units.Select(unit => unit.Unit).ToList(), "ordinal order of the names");
    }

    /// <summary>
    /// A start of the range at the smallest storable time reads the carried-in state, because the start of the lead-in is clamped to the storable range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreServiceAvailabilityClampsLeadInAtStorableMin()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var capturedAt = StorableTime.Min.AddHours(1);

        await ServiceHistorySamples.InsertServiceAsync(directory.Path, "edge.service", "active", "running", capturedAt, TestContext.CancellationToken);

        // Act
        var result = await store.ServiceAvailabilityAsync(new ServiceAvailabilityQuery
                                                          {
                                                              From = StorableTime.Min,
                                                              To = StorableTime.Min.AddHours(3)
                                                          },
                                                          TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, result.Units, "the record at the start of the storable range is read");
        Assert.AreEqual(TimeSpan.FromMinutes(65), result.Units[0].Available, "the record holds the cap");
        Assert.AreEqual(TimeSpan.FromMinutes(115), result.Units[0].Unknown, "the rest is unknown");
    }

    /// <summary>
    /// A range that breaks a rule is refused with the text of the time-series query.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <param name="message">The error text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow("fromEqualsTo", "store: invalid query: from must be before to")]
    [DataRow("fromAfterTo", "store: invalid query: from must be before to")]
    [DataRow("fromBelowStorable", "store: invalid query: from and to must be within the storable range")]
    [DataRow("toAboveStorable", "store: invalid query: from and to must be within the storable range")]
    public async Task SqliteStoreServiceAvailabilityRefusesInvalidQuery(string scenario, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = scenario switch
                    {
                        "fromEqualsTo" => Query(10, 10),
                        "fromAfterTo" => Query(20, 10),
                        "fromBelowStorable" => new ServiceAvailabilityQuery
                                               {
                                                   From = DateTimeOffset.MinValue,
                                                   To = At(10)
                                               },
                        _ => new ServiceAvailabilityQuery
                             {
                                 From = At(0),
                                 To = DateTimeOffset.MaxValue
                             }
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ServiceAvailabilityAsync(query, TestContext.CancellationToken), "the query breaks a rule");

        // Assert
        Assert.AreEqual(message, exception.Message, "the error names the rule");
    }

    /// <summary>
    /// The state stream of the view is answered from the covering index of the service state members alone, whose expressions it names.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreServiceAvailabilityUsesCoveringIndex()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertUnitsAsync(directory.Path, 300, Samples.Base, false, TestContext.CancellationToken);

        // Act
        var plan = await ServiceHistorySamples.PlanAsync(directory.Path,
                                                         "SELECT id, captured_at, CAST(json_extract(data, '$.unit') AS TEXT), CAST(json_extract(data, '$.active_state') AS TEXT) FROM records INDEXED BY records_service_state "
                                                         + "WHERE kind = 'service_state' AND captured_at > $from AND captured_at < $to ORDER BY captured_at",
                                                         TestContext.CancellationToken);

        // Assert
        Assert.Contains("SEARCH records USING COVERING INDEX records_service_state", plan, $"the stream reads the covering index: {plan}");
    }

    /// <summary>
    /// 2,880 touching gap records of the <c>systemd</c> collector over a day, one per 30-second run, merge into one unknown span that covers the day, with every gap read and the note flag set.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public async Task SqliteStoreServiceAvailabilityMergesPerRunSystemdGaps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var hour in Enumerable.Range(0, 24))
        {
            await InsertAsync(directory, "a.service", "active", "running", hour * 60);
        }

        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "systemd", 2880, At(0), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 1440), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, result.Units, "one unit");
        Assert.AreEqual(TimeSpan.FromDays(1), result.Units[0].Unknown, "the merged span covers the whole day");
        Assert.AreEqual(TimeSpan.Zero, result.Units[0].Available, "nothing is available inside the gaps");
        Assert.AreEqual(TimeSpan.Zero, result.Units[0].NotAvailable, "nothing is not available either");
        Assert.IsNull(result.GapsCompleteUntil, "the merged spans are one, so all gaps are read");
        Assert.IsTrue(result.SystemdGaps, "a gap of the systemd collector was read");
    }

    /// <summary>
    /// Gap rows of both kinds that are not closed, and gap records of no collector or of one the time-series query leaves unnamed, make the time of every unit unknown, while a closed row and a row outside the range do not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public async Task SqliteStoreServiceAvailabilityReadsGapRowsAndUnnamedCollectors()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var hour in Enumerable.Range(0, 6))
        {
            await InsertAsync(directory, "a.service", "active", "running", hour * 60);
        }

        await InsertGapRowAsync(directory, GapState.Final, 60, 90, 1);
        await InsertGapRowAsync(directory, GapState.Closed, 120, 150, 3);
        await InsertGapRowAsync(directory, GapState.Open, 400, 420, 5);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, string.Empty, 1, At(180), TimeSpan.Zero, TimeSpan.FromMinutes(20), TestContext.CancellationToken);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "future", 1, At(240), TimeSpan.Zero, TimeSpan.FromMinutes(10), TestContext.CancellationToken);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 360), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, result.Units, "one unit");
        Assert.AreEqual(TimeSpan.FromMinutes(60), result.Units[0].Unknown, "30 minutes of the final row, 20 of the record without a collector, 10 of the unnamed collector");
        Assert.AreEqual(TimeSpan.FromMinutes(300), result.Units[0].Available, "the rest, the closed row and the row after the range do not count");
        Assert.IsFalse(result.SystemdGaps, "no kept gap names the systemd collector");
        Assert.IsNull(result.GapsCompleteUntil, "all gaps are read");
    }

    /// <summary>
    /// A gap record of another named collector does not make the time of a unit unknown and does not set the note flag, one of the <c>systemd</c> collector does both.
    /// </summary>
    /// <param name="collector">The collector of the gap record</param>
    /// <param name="unknownMinutes">The unknown minutes expected</param>
    /// <param name="systemd">Whether the note flag is expected</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow("filesystem", 0, false)]
    [DataRow("clock", 0, false)]
    [DataRow("mariadb", 0, false)]
    [DataRow("logs", 0, false)]
    [DataRow("system", 0, false)]
    [DataRow("systemd", 10, true)]
    public async Task SqliteStoreServiceAvailabilitySkipsOtherCollectors(string collector, int unknownMinutes, bool systemd)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var hour in Enumerable.Range(0, 6))
        {
            await InsertAsync(directory, "a.service", "active", "running", hour * 60);
        }

        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, collector, 1, At(100), TimeSpan.Zero, TimeSpan.FromMinutes(10), TestContext.CancellationToken);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 360), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(unknownMinutes), result.Units[0].Unknown, $"the unknown time with a gap of the collector {collector}");
        Assert.AreEqual(systemd, result.SystemdGaps, $"the note flag with a gap of the collector {collector}");
    }

    /// <summary>
    /// With more than 1,000 spans of the <c>systemd</c> collector the first 1,000 are read, the time from the start of the next one counts as unknown and the result names that time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public async Task SqliteStoreServiceAvailabilityCapsGapSpansAtThousand()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var hour in Enumerable.Range(0, 8))
        {
            await InsertAsync(directory, "a.service", "active", "running", hour * 60);
        }

        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "systemd", 1200, At(0), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        // Act
        var result = await store.ServiceAvailabilityAsync(Query(0, 480), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(At(0).AddSeconds(20000), result.GapsCompleteUntil, "complete up to the start of the 1,001st span");
        Assert.AreEqual(TimeSpan.FromSeconds(10000), result.Units[0].Available, "20,000 seconds before the bound minus 1,000 spans of 10 seconds");
        Assert.AreEqual(TimeSpan.FromSeconds(18800), result.Units[0].Unknown, "the 1,000 spans and the 8,800 seconds after the bound");
        Assert.IsTrue(result.SystemdGaps, "the gaps read are of the systemd collector");
    }

    /// <summary>
    /// Gets an instant a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="minutes">The minutes</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset At(double minutes)
    {
        return ServiceHistorySamples.At(minutes);
    }

    /// <summary>
    /// Creates a query of a range in minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>The query</returns>
    private static ServiceAvailabilityQuery Query(int from, int to)
    {
        return new ServiceAvailabilityQuery
               {
                   From = At(from),
                   To = At(to)
               };
    }

    /// <summary>
    /// Inserts a service record a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="unit">The unit</param>
    /// <param name="activeState">The active state</param>
    /// <param name="subState">The sub state</param>
    /// <param name="minutes">The capture time in minutes</param>
    /// <returns>A task that completes when the record is stored</returns>
    private async Task InsertAsync(TempDirectory directory, string unit, string activeState, string subState, int minutes)
    {
        await ServiceHistorySamples.InsertServiceAsync(directory.Path, unit, activeState, subState, At(minutes), TestContext.CancellationToken);
    }

    /// <summary>
    /// Inserts a sequence gap row that starts and ends a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="state">The stored state</param>
    /// <param name="fromMinutes">The start of the span in minutes</param>
    /// <param name="toMinutes">The end of the span in minutes</param>
    /// <param name="firstSeq">The first missing number; the last is one more</param>
    /// <returns>A task that completes when the row is stored</returns>
    private async Task InsertGapRowAsync(TempDirectory directory, string state, int fromMinutes, int toMinutes, long firstSeq)
    {
        await TimeSeriesSamples.InsertGapAsync(directory.Path,
                                               new SampleGap
                                               {
                                                   State = state,
                                                   FirstSeq = firstSeq,
                                                   LastSeq = firstSeq + 1,
                                                   From = At(fromMinutes),
                                                   To = At(toMinutes)
                                               },
                                               TestContext.CancellationToken);
    }

    #endregion // Methods
}