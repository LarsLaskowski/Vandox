using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the MariaDB status and thread snapshot reads of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreMariaDbHistoryTests
{
    #region Constants

    private const double Tolerance = 1e-9;

    private const string ThreadsOfOneQuery = "{\"availability\":\"up\",\"threads\":[{\"id\":1,\"command\":\"Query\",\"time_seconds\":1}]}";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The status read folds the records of the range on the steps: the share up of each step, the memory and the connections of the up records, the times of each availability, the latest memory and the number of records.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC6")]
    public async Task SqliteStoreMariaDbStatusFoldsOnSteps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           ServiceHistorySamples.Status(At(0), MariaDbStatus.Up, 100, 5),
                                           ServiceHistorySamples.Status(At(5), MariaDbStatus.Up, 300, 7),
                                           ServiceHistorySamples.Status(At(10), MariaDbStatus.Down, null, null),
                                           ServiceHistorySamples.Status(At(15), MariaDbStatus.NotAnswering, null, null),
                                           ServiceHistorySamples.Status(At(20), MariaDbStatus.Up, 500, 9));

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 30, 10), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([At(0), At(10), At(20)], result.Times.ToList(), "the steps of ten minutes");
        Assert.AreSequenceEqual([100d, 0d, 100d], result.Up.Select(point => point.Average).ToList(), "the share up of the known time of each step");
        Assert.IsTrue(result.Up.All(point => point.State == PointState.Value), "every step has known time");
        Assert.AreEqual(2, result.Memory[0].Count, "two up records in the first step");
        Assert.AreEqual(100d, result.Memory[0].Minimum, Tolerance, "minimum memory of the first step");
        Assert.AreEqual(200d, result.Memory[0].Average, Tolerance, "average memory of the first step");
        Assert.AreEqual(300d, result.Memory[0].Maximum, Tolerance, "maximum memory of the first step");
        Assert.AreEqual(PointState.Gap, result.Memory[1].State, "no up record in the second step");
        Assert.AreEqual(500d, result.Memory[2].Average, Tolerance, "memory of the third step");
        Assert.AreEqual(5d, result.Connections[0].Minimum, Tolerance, "minimum connections");
        Assert.AreEqual(6d, result.Connections[0].Average, Tolerance, "average connections");
        Assert.AreEqual(7d, result.Connections[0].Maximum, Tolerance, "maximum connections");
        Assert.AreEqual(9d, result.Connections[2].Last, Tolerance, "connections of the third step");
        Assert.AreEqual(TimeSpan.FromMinutes(6), result.UpTime, "three records of two minutes");
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.DownTime, "one down record of two minutes");
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.NotAnsweringTime, "one record not answering");
        Assert.AreEqual(TimeSpan.FromMinutes(20), result.UnknownTime, "the rest of the range");
        Assert.AreEqual(500d, result.LatestMemory, "the memory of the latest up record");
        Assert.AreEqual(At(20), result.LatestMemoryAt, "its time");
        Assert.AreEqual(5, result.Records, "five records in the range");
        Assert.AreEqual(At(0), result.Oldest, "the oldest status record");
        Assert.IsNull(result.GapsCompleteUntil, "all gaps are read");
    }

    /// <summary>
    /// Only records of the source <c>mariadb</c> are read, whatever the kind says.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusReadsOnlySourceMariadb()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, ServiceHistorySamples.Status(At(0), MariaDbStatus.Up, 100, 5));
        await TimeSeriesSamples.InsertRecordAsync(directory.Path, "mariadb_status", At(1), "{\"availability\":\"up\",\"status\":{\"Memory_used\":999}}", TestContext.CancellationToken);
        await TimeSeriesSamples.InsertRecordAsync(directory.Path, "mariadb_status", At(-10), "{\"availability\":\"up\"}", TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 10, 10), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.UpTime, "the forged record does not cut the hold of the real one and adds none");
        Assert.AreEqual(1, result.Records, "one record of the source mariadb");
        Assert.AreEqual(100d, result.LatestMemory, "the latest memory is the real record's");
        Assert.AreEqual(At(0), result.Oldest, "the forged record before the range is not the oldest");
    }

    /// <summary>
    /// The oldest status record is the oldest of all stored, also one before the range, and none without any status record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusReportsOldest()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var none = await store.MariaDbStatusAsync(Query(0, 30, 10), TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           ServiceHistorySamples.Status(At(-7 * 1440), MariaDbStatus.Up, 1, 1),
                                           ServiceHistorySamples.Status(At(5), MariaDbStatus.Up, 2, 2));

        var some = await store.MariaDbStatusAsync(Query(0, 30, 10), TestContext.CancellationToken);

        // Assert
        Assert.IsNull(none.Oldest, "no status record, no oldest");
        Assert.AreEqual(0, none.Records, "no record in the range");
        Assert.AreEqual(TimeSpan.FromMinutes(30), none.UnknownTime, "the whole range is unknown");
        Assert.IsTrue(none.Up.All(point => point.State == PointState.Gap), "every step is a gap");
        Assert.AreEqual(At(-7 * 1440), some.Oldest, "the record a week before the range is the oldest");
        Assert.AreEqual(1, some.Records, "only the record of the range is counted");
    }

    /// <summary>
    /// A status value above the largest signed 64-bit integer is read as the number it is, not as zero or an error.
    /// </summary>
    /// <param name="value">The value of <c>Memory_used</c> and <c>Threads_connected</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(ulong.MaxValue)]
    [DataRow(9223372036854775808UL)]
    public async Task SqliteStoreMariaDbStatusReadsStatusValueAboveLongMax(ulong value)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, ServiceHistorySamples.Status(At(1), MariaDbStatus.Up, value, value));

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 10, 10), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual((double)value, result.LatestMemory, "the latest memory");
        Assert.AreEqual((double)value, result.Memory[0].Maximum, "the memory of the step");
        Assert.AreEqual((double)value, result.Connections[0].Maximum, "the connections of the step");
    }

    /// <summary>
    /// A range that starts at the smallest storable time reads the record right after it, because the lead-in is clamped to the storable range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusClampsLeadInAtStorableMin()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertForgedStatusAsync(directory.Path, StorableTime.Min.AddMinutes(1), "{\"availability\":\"up\",\"status\":{\"Memory_used\":5}}", TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(new MariaDbStatusQuery
                                                    {
                                                        From = StorableTime.Min,
                                                        To = StorableTime.Min.AddMinutes(10),
                                                        Step = TimeSpan.FromMinutes(5)
                                                    },
                                                    TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(2), result.UpTime, "the record at the start of the storable range is read");
        Assert.AreEqual(1, result.Records, "one record");
    }

    /// <summary>
    /// A status record whose availability is none of the three known values counts as no state.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusIgnoresRecordOfUnknownAvailability()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertForgedStatusAsync(directory.Path, At(0), "{\"availability\":\"weird\"}", TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 30, 10), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.Zero, result.UpTime, "no time is up");
        Assert.AreEqual(TimeSpan.Zero, result.DownTime, "no time is down");
        Assert.AreEqual(TimeSpan.Zero, result.NotAnsweringTime, "no time is not answering");
    }

    /// <summary>
    /// A query that breaks a rule is refused with the texts of the time-series query and the two rules of the steps.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <param name="message">The error text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("fromEqualsTo", "store: invalid query: from must be before to")]
    [DataRow("fromAfterTo", "store: invalid query: from must be before to")]
    [DataRow("fromBelowStorable", "store: invalid query: from and to must be within the storable range")]
    [DataRow("toAboveStorable", "store: invalid query: from and to must be within the storable range")]
    [DataRow("stepZero", "store: invalid query: step must be positive")]
    [DataRow("stepNegative", "store: invalid query: step must be positive")]
    [DataRow("tooManySteps", "store: invalid query: at most 2001 steps")]
    public async Task SqliteStoreMariaDbStatusRefusesInvalidQuery(string scenario, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = scenario switch
                    {
                        "fromEqualsTo" => Query(10, 10, 5),
                        "fromAfterTo" => Query(20, 10, 5),
                        "fromBelowStorable" => new MariaDbStatusQuery
                                               {
                                                   From = DateTimeOffset.MinValue,
                                                   To = At(10),
                                                   Step = TimeSpan.FromMinutes(5)
                                               },
                        "toAboveStorable" => new MariaDbStatusQuery
                                             {
                                                 From = At(0),
                                                 To = DateTimeOffset.MaxValue,
                                                 Step = TimeSpan.FromMinutes(5)
                                             },
                        "stepZero" => new MariaDbStatusQuery
                                      {
                                          From = At(0),
                                          To = At(10),
                                          Step = TimeSpan.Zero
                                      },
                        "stepNegative" => new MariaDbStatusQuery
                                          {
                                              From = At(0),
                                              To = At(10),
                                              Step = TimeSpan.FromMinutes(-5)
                                          },
                        _ => Query(0, 5 * 2002, 5)
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.MariaDbStatusAsync(query, TestContext.CancellationToken), "the query breaks a rule");

        // Assert
        Assert.AreEqual(message, exception.Message, "the error names the rule");
    }

    /// <summary>
    /// A range of exactly 2,001 steps is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusReadsExactly2001Steps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 5 * 2001, 5), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2001, result.Times, "2,001 steps");
        Assert.HasCount(2001, result.Up, "one share per step");
    }

    /// <summary>
    /// The three statement shapes of the MariaDB reads are answered from the covering index of the status members alone, whose expressions they name.
    /// </summary>
    /// <param name="shape">The statement shape</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC8")]
    [DataRow("stream")]
    [DataRow("oldest")]
    [DataRow("snapshots")]
    public async Task SqliteStoreMariaDbStatusUsesCoveringIndex(string shape)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertStatusRunAsync(directory.Path, Samples.Base, 300, TimeSpan.FromSeconds(30), "up", TestContext.CancellationToken);

        var sql = shape switch
                  {
                      "stream" => "SELECT captured_at, json_extract(data, '$.availability'), json_extract(data, '$.status.Memory_used'), json_extract(data, '$.status.Threads_connected') FROM records INDEXED BY records_mariadb_status "
                                  + "WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at >= $from AND captured_at < $to ORDER BY captured_at",
                      "oldest" => "SELECT min(captured_at) FROM records INDEXED BY records_mariadb_status WHERE kind = 'mariadb_status' AND source = 'mariadb'",
                      _ => "SELECT id, captured_at, json_array_length(data, '$.threads') FROM records INDEXED BY records_mariadb_status "
                           + "WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at >= $from AND captured_at < $to AND json_array_length(data, '$.threads') > 0 ORDER BY captured_at DESC LIMIT $limit"
                  };

        // Act
        var plan = await ServiceHistorySamples.PlanAsync(directory.Path, sql, TestContext.CancellationToken);

        // Assert
        Assert.Contains("COVERING INDEX records_mariadb_status", plan, $"the {shape} statement reads the covering index: {plan}");
        Assert.IsFalse(plan.Split('\n').Contains("SCAN records"), $"the {shape} statement does not scan the table: {plan}");
    }

    /// <summary>
    /// The statements of the MariaDB reads, as the store runs them, are answered from the covering index of the status members alone.
    /// </summary>
    /// <param name="shape">The name of the statement constant</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC8")]
    [DataRow("StatusStream")]
    [DataRow("StatusOldest")]
    [DataRow("SnapshotStamps")]
    [DataRow("SnapshotAtOrBefore")]
    [DataRow("SnapshotAfter")]
    public async Task SqliteStoreMariaDbStatusReadStatementsUseCoveringIndex(string shape)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertStatusRunAsync(directory.Path, Samples.Base, 300, TimeSpan.FromSeconds(30), "up", TestContext.CancellationToken);

        var sql = shape switch
                  {
                      "StatusStream" => MariaDbHistoryQueries.StatusStream,
                      "StatusOldest" => MariaDbHistoryQueries.StatusOldest,
                      "SnapshotStamps" => MariaDbHistoryQueries.SnapshotStamps,
                      "SnapshotAtOrBefore" => MariaDbHistoryQueries.SnapshotAtOrBefore,
                      _ => MariaDbHistoryQueries.SnapshotAfter
                  };

        // Act
        var plan = await ServiceHistorySamples.PlanAsync(directory.Path, sql, TestContext.CancellationToken);

        // Assert
        Assert.Contains("COVERING INDEX records_mariadb_status", plan, $"the {shape} statement reads the covering index: {plan}");
        Assert.IsFalse(plan.Split('\n').Contains("SCAN records"), $"the {shape} statement does not scan the table: {plan}");
    }

    /// <summary>
    /// 2,880 touching gap records of the <c>systemd</c> collector over a day do not make any MariaDB time unknown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusIgnoresSystemdGaps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertStatusRunAsync(directory.Path, At(0), 2880, TimeSpan.FromSeconds(30), "up", TestContext.CancellationToken);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "systemd", 2880, At(0), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 1440, 60), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.FromDays(1), result.UpTime, "MariaDB was up the whole day");
        Assert.AreEqual(TimeSpan.Zero, result.UnknownTime, "no time is unknown");
        Assert.IsNull(result.GapsCompleteUntil, "no gap of the collector is left unread");
        Assert.HasCount(24, result.Up, "24 steps");
        Assert.IsTrue(result.Up.All(point => point.State == PointState.Value && Math.Abs(point.Average - 100) < Tolerance), "every step is completely up");
    }

    /// <summary>
    /// Gap rows that are not closed and gap records of no collector, of an unnamed one and of the <c>mariadb</c> collector make MariaDB time unknown, while a closed row and a row outside the range do not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusReadsGapRowsAndUnnamedCollectors()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertStatusRunAsync(directory.Path, At(0), 720, TimeSpan.FromSeconds(30), "up", TestContext.CancellationToken);
        await InsertGapRowAsync(directory, GapState.Final, 60, 90, 1);
        await InsertGapRowAsync(directory, GapState.Closed, 120, 150, 3);
        await InsertGapRowAsync(directory, GapState.Open, 400, 420, 5);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, string.Empty, 1, At(180), TimeSpan.Zero, TimeSpan.FromMinutes(20), TestContext.CancellationToken);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "future", 1, At(240), TimeSpan.Zero, TimeSpan.FromMinutes(10), TestContext.CancellationToken);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "mariadb", 1, At(300), TimeSpan.Zero, TimeSpan.FromMinutes(5), TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 360, 30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(65), result.UnknownTime, "30 minutes of the final row, 20 without a collector, 10 unnamed, 5 of the mariadb collector");
        Assert.AreEqual(TimeSpan.FromMinutes(295), result.UpTime, "the rest was up, the closed row and the row after the range do not count");
        Assert.IsNull(result.GapsCompleteUntil, "all gaps are read");
    }

    /// <summary>
    /// A gap record of another named collector does not make MariaDB time unknown, one of the <c>mariadb</c> collector does.
    /// </summary>
    /// <param name="collector">The collector of the gap record</param>
    /// <param name="unknownMinutes">The unknown minutes expected</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    [DataRow("systemd", 0)]
    [DataRow("filesystem", 0)]
    [DataRow("clock", 0)]
    [DataRow("logs", 0)]
    [DataRow("mariadb", 10)]
    public async Task SqliteStoreMariaDbStatusSkipsOtherCollectors(string collector, int unknownMinutes)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertStatusRunAsync(directory.Path, At(0), 720, TimeSpan.FromSeconds(30), "up", TestContext.CancellationToken);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, collector, 1, At(100), TimeSpan.Zero, TimeSpan.FromMinutes(10), TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 360, 30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(unknownMinutes), result.UnknownTime, $"the unknown time with a gap of the collector {collector}");
    }

    /// <summary>
    /// With more than 1,000 spans of the <c>mariadb</c> collector the first 1,000 are read and the time from the start of the next one is unknown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMariaDbStatusCapsGapSpansAtThousand()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertStatusRunAsync(directory.Path, At(0), 720, TimeSpan.FromSeconds(30), "up", TestContext.CancellationToken);
        await ServiceHistorySamples.InsertCollectorGapsAsync(directory.Path, "mariadb", 1100, At(0), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        // Act
        var result = await store.MariaDbStatusAsync(Query(0, 360, 5), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(At(0).AddSeconds(20000), result.GapsCompleteUntil, "complete up to the start of the 1,001st span");
        Assert.AreEqual(TimeSpan.FromSeconds(10000), result.UpTime, "20,000 seconds before the bound minus 1,000 spans of 10 seconds");
        Assert.AreEqual(TimeSpan.FromSeconds(11600), result.UnknownTime, "the 1,000 spans and the 1,600 seconds after the bound");
    }

    /// <summary>
    /// The thread snapshots of a range are listed newest first, the later record first at one capture time, with the number of threads, of those not sleeping and the longest of those; records without threads and outside the range are left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreThreadSnapshotsNewestFirstWithCounts()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var sleeping = ServiceHistorySamples.Thread(1, "Sleep", 5);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           ServiceHistorySamples.Status(At(-10), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Query", 1)),
                                           ServiceHistorySamples.Status(At(10), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Query", 5), ServiceHistorySamples.Thread(2, "Sleep", 900), ServiceHistorySamples.Thread(3, "Query", 30), ServiceHistorySamples.Thread(4, string.Empty, 40)),
                                           ServiceHistorySamples.Status(At(20), MariaDbStatus.Up, 1, 1, sleeping, ServiceHistorySamples.Thread(2, "Sleep", 6)),
                                           ServiceHistorySamples.Status(At(20), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Execute", 2)),
                                           ServiceHistorySamples.Status(At(30), MariaDbStatus.Up, 1, 1),
                                           ServiceHistorySamples.Status(At(50), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Query", 1)));
        await ServiceHistorySamples.InsertForgedStatusAsync(directory.Path, At(25), "{\"availability\":\"up\",\"threads\":[]}", TestContext.CancellationToken);

        // Act
        var stamps = await store.ThreadSnapshotsAsync(Threads(0, 45, 51), TestContext.CancellationToken);
        var limited = await store.ThreadSnapshotsAsync(Threads(0, 45, 1), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(51, ThreadSnapshotQuery.MaxLimit, "the largest limit");
        Assert.AreSequenceEqual([At(20), At(20), At(10)], stamps.Select(stamp => stamp.CapturedAt).ToList(), "newest first, without the empty list at 25, the record without threads at 30 and the records outside the range");
        Assert.IsGreaterThan(stamps[1].RecordId, stamps[0].RecordId, "at one capture time the later record comes first");
        Assert.AreEqual(1, stamps[0].Threads, "threads of the later record at 20");
        Assert.AreEqual(1, stamps[0].Running, "one thread running");
        Assert.AreEqual(2L, stamps[0].LongestSeconds, "its seconds");
        Assert.AreEqual(2, stamps[1].Threads, "two sleeping threads");
        Assert.AreEqual(0, stamps[1].Running, "none running");
        Assert.IsNull(stamps[1].LongestSeconds, "no longest without a thread running");
        Assert.AreEqual(4, stamps[2].Threads, "four threads recorded at 10");
        Assert.AreEqual(3, stamps[2].Running, "the thread without a command is not sleeping");
        Assert.AreEqual(40L, stamps[2].LongestSeconds, "the longest of the threads that are not sleeping");
        Assert.AreSequenceEqual([stamps[0].RecordId], limited.Select(stamp => stamp.RecordId).ToList(), "the limit keeps the newest");
    }

    /// <summary>
    /// A stamp reports the longest seconds of the threads that are not sleeping, capped at <see cref="ThreadSnapshot.MaxSeconds"/>, and a sleeping thread does not count.
    /// </summary>
    /// <param name="seconds">The seconds of the running thread</param>
    /// <param name="expected">The longest seconds expected</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(ulong.MaxValue, ThreadSnapshot.MaxSeconds)]
    [DataRow(3153600001UL, ThreadSnapshot.MaxSeconds)]
    [DataRow(3153600000UL, ThreadSnapshot.MaxSeconds)]
    [DataRow(3153599999UL, 3153599999L)]
    [DataRow(0UL, 0L)]
    public async Task SqliteStoreThreadSnapshotsStampClampsLongestSeconds(ulong seconds, long expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           ServiceHistorySamples.Status(At(10), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Sleep", ulong.MaxValue), ServiceHistorySamples.Thread(2, "Query", seconds)));

        // Act
        var stamps = await store.ThreadSnapshotsAsync(Threads(0, 20, 5), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, stamps, "one snapshot");
        Assert.AreEqual(expected, stamps[0].LongestSeconds, $"the longest seconds of a thread with {seconds}");
    }

    /// <summary>
    /// The snapshot closest to a moment is the nearer one within the window, the one at or before the moment on a tie, found on either side up to the window edge, and records without threads do not count.
    /// </summary>
    /// <param name="before">The minutes of the snapshot before the moment, or <c>null</c> for none</param>
    /// <param name="after">The minutes of the snapshot after the moment, or <c>null</c> for none</param>
    /// <param name="expected">The side expected</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(-4, 4, "before")]
    [DataRow(-3, 5, "before")]
    [DataRow(-5, 3, "after")]
    [DataRow(null, 9, "after")]
    [DataRow(-9, null, "before")]
    [DataRow(-11, 11, "none")]
    [DataRow(-10, null, "before")]
    [DataRow(null, 10, "after")]
    [DataRow(null, null, "none")]
    public async Task SqliteStoreThreadSnapshotsClosestWithinWindowPrefersEarlierOnTie(int? before, int? after, string expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var moment = At(100);
        var records = new List<DataRecord>
                      {
                          ServiceHistorySamples.Status(moment.AddMinutes(1), MariaDbStatus.Up, 1, 1)
                      };

        if (before is not null)
        {
            records.Add(ServiceHistorySamples.Status(moment.AddMinutes(before.Value), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Query", 1)));
        }

        if (after is not null)
        {
            records.Add(ServiceHistorySamples.Status(moment.AddMinutes(after.Value), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Query", 1)));
        }

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, [.. records]);

        // Act
        var stamp = await store.ClosestThreadSnapshotAsync(moment, TimeSpan.FromMinutes(10), TestContext.CancellationToken);

        // Assert
        var side = "none";

        if (stamp is not null)
        {
            side = stamp.CapturedAt <= moment ? "before" : "after";
        }

        Assert.AreEqual(expected, side, $"the closest of {before} and {after} minutes around the moment");
    }

    /// <summary>
    /// Of several snapshots at one capture time the one with the lowest record ID is the closest, on either side.
    /// </summary>
    /// <param name="minutes">The minutes of the snapshots from the moment</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(-3)]
    [DataRow(3)]
    public async Task SqliteStoreThreadSnapshotsClosestTakesLowerIdWithinOneCaptureTime(int minutes)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var moment = At(100);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           ServiceHistorySamples.Status(moment.AddMinutes(minutes), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(1, "Query", 1)),
                                           ServiceHistorySamples.Status(moment.AddMinutes(minutes), MariaDbStatus.Up, 1, 1, ServiceHistorySamples.Thread(2, "Query", 1)));

        var listed = await store.ThreadSnapshotsAsync(Threads(0, 200, 5), TestContext.CancellationToken);

        // Act
        var stamp = await store.ClosestThreadSnapshotAsync(moment, TimeSpan.FromMinutes(10), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(stamp, "a snapshot lies within the window");
        Assert.AreEqual(listed.Min(item => item.RecordId), stamp.RecordId, "the lower record ID");
    }

    /// <summary>
    /// The window of the closest snapshot is clamped to the storable range at both of its ends.
    /// </summary>
    /// <param name="edge">The edge, <c>min</c> or <c>max</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow("min")]
    [DataRow("max")]
    public async Task SqliteStoreThreadSnapshotsClosestClampsWindowAtStorableMin(string edge)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var capturedAt = edge == "min" ? StorableTime.Min.AddSeconds(1) : StorableTime.Max.AddSeconds(-30);
        var moment = edge == "min" ? StorableTime.Min.AddMinutes(1) : StorableTime.Max.AddMinutes(-1);

        await ServiceHistorySamples.InsertForgedStatusAsync(directory.Path, capturedAt, ThreadsOfOneQuery, TestContext.CancellationToken);

        // Act
        var stamp = await store.ClosestThreadSnapshotAsync(moment, TimeSpan.FromMinutes(10), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(stamp, "the snapshot next to the edge of the storable range is found");
        Assert.AreEqual(capturedAt, stamp.CapturedAt, "its time");
    }

    /// <summary>
    /// The rows of a snapshot are the threads that are not sleeping, a thread without a command among them, in the order the agent recorded them, with the counts of all threads.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreThreadSnapshotThreadRowsSkipSleepInAgentOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           ServiceHistorySamples.Status(At(10),
                                                                        MariaDbStatus.Up,
                                                                        1,
                                                                        1,
                                                                        ServiceHistorySamples.Thread(7, "Sleep", 900),
                                                                        ServiceHistorySamples.Thread(5, "Query", 3),
                                                                        ServiceHistorySamples.Thread(6, "Sleep", 800),
                                                                        ServiceHistorySamples.Thread(1, "Execute", 100),
                                                                        ServiceHistorySamples.Thread(9, string.Empty, 2)));

        var stamps = await store.ThreadSnapshotsAsync(Threads(0, 20, 5), TestContext.CancellationToken);

        // Act
        var snapshot = await store.ThreadSnapshotAsync(stamps[0].RecordId, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(snapshot, "the snapshot exists");
        Assert.AreEqual(At(10), snapshot.CapturedAt, "capture time");
        Assert.AreEqual(5, snapshot.Threads, "five threads recorded");
        Assert.AreEqual(3, snapshot.Running, "three are not sleeping");
        Assert.AreEqual(2, snapshot.Sleeping, "two are sleeping");
        Assert.AreSequenceEqual([5UL, 1UL, 9UL], snapshot.Rows.Select(row => row.Id).ToList(), "the threads not sleeping in the order of the record, not sorted by seconds");
        Assert.AreEqual(string.Empty, snapshot.Rows[2].Command, "the absent command reads as empty");
    }

    /// <summary>
    /// The thread rows map the keys <c>db</c> and <c>info</c>, keep the full 64-bit <c>id</c> and <c>time_seconds</c> and read absent keys as empty.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreThreadSnapshotThreadRowsMapDbAndInfoAndKeepFullIds()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var full = new MariaDbThread
                   {
                       Id = ulong.MaxValue,
                       User = "root",
                       Host = "localhost:3306",
                       Db = "shop",
                       Command = "Query",
                       TimeSeconds = ulong.MaxValue,
                       State = "executing",
                       Info = "SELECT 1",
                       Truncated = true
                   };

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, ServiceHistorySamples.Status(At(10), MariaDbStatus.Up, 1, 1, full, ServiceHistorySamples.Thread(2, "Query", 0)));

        var stamps = await store.ThreadSnapshotsAsync(Threads(0, 20, 5), TestContext.CancellationToken);

        // Act
        var snapshot = await store.ThreadSnapshotAsync(stamps[0].RecordId, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(snapshot, "the snapshot exists");
        Assert.HasCount(2, snapshot.Rows, "two rows");
        Assert.AreEqual(ulong.MaxValue, snapshot.Rows[0].Id, "the exact ID");
        Assert.AreEqual(ulong.MaxValue, snapshot.Rows[0].TimeSeconds, "the exact seconds");
        Assert.AreEqual("root", snapshot.Rows[0].User, "user");
        Assert.AreEqual("localhost:3306", snapshot.Rows[0].Host, "host");
        Assert.AreEqual("shop", snapshot.Rows[0].Db, "db");
        Assert.AreEqual("Query", snapshot.Rows[0].Command, "command");
        Assert.AreEqual("executing", snapshot.Rows[0].State, "state");
        Assert.AreEqual("SELECT 1", snapshot.Rows[0].Info, "info");
        Assert.IsTrue(snapshot.Rows[0].Truncated, "truncated");
        Assert.AreEqual(2UL, snapshot.Rows[1].Id, "the second ID");
        Assert.AreEqual(string.Empty, snapshot.Rows[1].User, "an absent user is empty");
        Assert.AreEqual(string.Empty, snapshot.Rows[1].Db, "an absent db is empty");
        Assert.AreEqual(string.Empty, snapshot.Rows[1].Info, "an absent info is empty");
        Assert.IsFalse(snapshot.Rows[1].Truncated, "an absent truncated is false");
    }

    /// <summary>
    /// Of 150 threads not sleeping only 101 rows are returned, with the counts of all threads.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreThreadSnapshotThreadRowsCapAt101()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var threads = Enumerable.Range(1, 150).Select(id => ServiceHistorySamples.Thread((ulong)id, "Query", 1)).Concat(Enumerable.Range(151, 20).Select(id => ServiceHistorySamples.Thread((ulong)id, "Sleep", 1))).ToArray();

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, ServiceHistorySamples.Status(At(10), MariaDbStatus.Up, 1, 1, threads));

        var stamps = await store.ThreadSnapshotsAsync(Threads(0, 20, 5), TestContext.CancellationToken);

        // Act
        var snapshot = await store.ThreadSnapshotAsync(stamps[0].RecordId, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(snapshot, "the snapshot exists");
        Assert.AreEqual(101, ThreadSnapshot.MaxRows, "the cap of the rows");
        Assert.HasCount(101, snapshot.Rows, "101 rows");
        Assert.AreEqual(101UL, snapshot.Rows[100].Id, "the first 101 in the order of the record");
        Assert.AreEqual(170, snapshot.Threads, "all threads are counted");
        Assert.AreEqual(150, snapshot.Running, "all threads not sleeping are counted");
        Assert.AreEqual(20, snapshot.Sleeping, "all sleeping threads are counted");
    }

    /// <summary>
    /// A record ID that no status record has reads no snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreThreadSnapshotOfUnknownRecordIsNull()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var snapshot = await store.ThreadSnapshotAsync(123456, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(snapshot, "no record, no snapshot");
    }

    /// <summary>
    /// A stored thread object that cannot be read as a thread fails the read with a store error, while the list of stamps still names the record.
    /// </summary>
    /// <param name="thread">The members of the thread object</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [TestCategory("AC9")]
    [DataRow("\"id\":\"abc\",\"command\":\"Query\",\"time_seconds\":1")]
    [DataRow("\"id\":-1,\"command\":\"Query\",\"time_seconds\":1")]
    [DataRow("\"id\":1,\"command\":\"Query\",\"time_seconds\":-5")]
    [DataRow("\"id\":1.5,\"command\":\"Query\",\"time_seconds\":1")]
    public async Task SqliteStoreThreadSnapshotThreadRowThatDoesNotParseIsStoreError(string thread)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ServiceHistorySamples.InsertForgedStatusAsync(directory.Path, At(10), $"{{\"availability\":\"up\",\"threads\":[{{{thread}}}]}}", TestContext.CancellationToken);

        var stamps = await store.ThreadSnapshotsAsync(Threads(0, 20, 5), TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.ThreadSnapshotAsync(stamps[0].RecordId, TestContext.CancellationToken), "the thread object does not deserialize");

        // Assert
        Assert.HasCount(1, stamps, "the list names the record");
        Assert.StartsWith("store: reading mariadb status", exception.Message, "the error names the read");
    }

    /// <summary>
    /// A snapshot list query that breaks a rule is refused.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <param name="message">The error text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow("limitZero", "store: invalid query: limit must be 1 to 51")]
    [DataRow("limitNegative", "store: invalid query: limit must be 1 to 51")]
    [DataRow("limitOver", "store: invalid query: limit must be 1 to 51")]
    [DataRow("fromEqualsTo", "store: invalid query: from must be before to")]
    [DataRow("fromAfterTo", "store: invalid query: from must be before to")]
    [DataRow("fromBelowStorable", "store: invalid query: from and to must be within the storable range")]
    [DataRow("toAboveStorable", "store: invalid query: from and to must be within the storable range")]
    public async Task SqliteStoreThreadSnapshotsRefusesInvalidQuery(string scenario, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = scenario switch
                    {
                        "limitZero" => Threads(0, 10, 0),
                        "limitNegative" => Threads(0, 10, -1),
                        "limitOver" => Threads(0, 10, 52),
                        "fromEqualsTo" => Threads(10, 10, 5),
                        "fromAfterTo" => Threads(20, 10, 5),
                        "fromBelowStorable" => new ThreadSnapshotQuery
                                               {
                                                   From = DateTimeOffset.MinValue,
                                                   To = At(10),
                                                   Limit = 5
                                               },
                        _ => new ThreadSnapshotQuery
                             {
                                 From = At(0),
                                 To = DateTimeOffset.MaxValue,
                                 Limit = 5
                             }
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ThreadSnapshotsAsync(query, TestContext.CancellationToken), "the query breaks a rule");

        // Assert
        Assert.AreEqual(message, exception.Message, "the error names the rule");
    }

    /// <summary>
    /// A closest-snapshot query that breaks a rule is refused.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <param name="message">The error text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow("windowZero", "store: invalid query: window must be positive and at most one day")]
    [DataRow("windowNegative", "store: invalid query: window must be positive and at most one day")]
    [DataRow("windowOverDay", "store: invalid query: window must be positive and at most one day")]
    [DataRow("atBelowStorable", "store: invalid query: at must be within the storable range")]
    [DataRow("atAboveStorable", "store: invalid query: at must be within the storable range")]
    public async Task SqliteStoreThreadSnapshotsClosestRefusesInvalidQuery(string scenario, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var (at, window) = scenario switch
                           {
                               "windowZero" => (At(10), TimeSpan.Zero),
                               "windowNegative" => (At(10), TimeSpan.FromMinutes(-1)),
                               "windowOverDay" => (At(10), TimeSpan.FromDays(1).Add(TimeSpan.FromSeconds(1))),
                               "atBelowStorable" => (DateTimeOffset.MinValue, TimeSpan.FromMinutes(10)),
                               _ => (DateTimeOffset.MaxValue, TimeSpan.FromMinutes(10))
                           };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ClosestThreadSnapshotAsync(at, window, TestContext.CancellationToken), "the query breaks a rule");

        // Assert
        Assert.AreEqual(message, exception.Message, "the error names the rule");
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
    /// Creates a status query of a range in minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="stepMinutes">The step in minutes</param>
    /// <returns>The query</returns>
    private static MariaDbStatusQuery Query(int from, int to, int stepMinutes)
    {
        return new MariaDbStatusQuery
               {
                   From = At(from),
                   To = At(to),
                   Step = TimeSpan.FromMinutes(stepMinutes)
               };
    }

    /// <summary>
    /// Creates a snapshot list query of a range in minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <returns>The query</returns>
    private static ThreadSnapshotQuery Threads(int from, int to, int limit)
    {
        return new ThreadSnapshotQuery
               {
                   From = At(from),
                   To = At(to),
                   Limit = limit
               };
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