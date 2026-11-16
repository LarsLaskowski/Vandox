using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the overview reads of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreOverviewTests
{
    #region Constants

    private const int LiveScanLimit = 20000;

    private const string FirstAgent = "agent-1";

    private const string SecondAgent = "agent-2";

    private const ulong Jump = 1_000_000_000_000_000;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Gets the time the reads take as now.
    /// </summary>
    private static DateTimeOffset Now => Samples.Base;

    /// <summary>
    /// Gets the bound the reads take: now plus the live allowance of 90 seconds.
    /// </summary>
    private static DateTimeOffset Until => Samples.Base.AddSeconds(90);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The freshness read returns the capture time of the newest live metric, not that of a newer record that is not live, and the receive time of the agent's highest-numbered record, not that of a later backfill batch.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task FreshnessReturnsNewestLiveMetricAndHighestNumberReceiveTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAgentAsync(store, TimelineSamples.Run(1, 3, Now.AddMinutes(-20)));
        await WriteAgentAsync(store, TimelineSamples.Run(10, 3, Now.AddMinutes(-2)));
        await WriteAgentAsync(store, TimelineSamples.OldRun(4, 6, Now.AddMinutes(-30), Now.AddSeconds(-60)));
        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, TimeSeriesSamples.Metric("system", "cpu_user_percent", Now.AddSeconds(-10), 1));

        var live = await TimelineSamples.LiveOfAsync(RetentionSamples.DatabasePath(directory.Path), FirstAgent, TestContext.CancellationToken);

        Assert.IsTrue(live[3], "arrangement: the newest record of the first batch is live");
        Assert.IsFalse(live[12], "arrangement: the highest-numbered record lies above an open gap and is not live");
        Assert.IsFalse(live[9], "arrangement: the backfilled record is not live");

        // Act
        var freshness = await store.FreshnessAsync(LiveScanLimit, Until, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(FirstAgent, freshness.AgentId, "the agent of the stream");
        Assert.AreEqual(Now.AddMinutes(-2), freshness.ReceivedAt, "the receive time of the highest-numbered record, not of the later backfill batch");
        Assert.AreEqual(Now.AddMinutes(-20).AddSeconds(-1), freshness.LastLiveAt, "the capture time of the newest live metric");
        Assert.AreEqual(0, freshness.OpenSequenceGaps, "the backfill closed the only gap");
        Assert.IsNull(freshness.MissingStart, "no open gap has a start");
        Assert.IsNull(freshness.MissingEnd, "no open gap has an end");
    }

    /// <summary>
    /// When none of the scanned metric records is live, the freshness read reports no live time and the capture time of the oldest scanned record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task FreshnessReportsOldestScannedTimeWhenNoLiveRecordInScan()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAgentAsync(store, TimelineSamples.Run(1, 1, Now.AddMinutes(-10)));
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           Enumerable.Range(1, 5)
                                                     .Select(seconds => TimeSeriesSamples.Metric("system", "cpu_user_percent", Now.AddSeconds(-seconds), seconds))
                                                     .ToArray());

        // Act
        var freshness = await store.FreshnessAsync(3, Until, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(FirstAgent, freshness.AgentId, "the agent of the stream");
        Assert.IsNull(freshness.LastLiveAt, "the live record lies beyond the three scanned rows");
        Assert.AreEqual(Now.AddSeconds(-3), freshness.ScannedBackTo, "the capture time of the oldest of the three scanned rows");
    }

    /// <summary>
    /// An agent whose records were all removed is still returned, the one with the latest stored capture time, without a receive time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task FreshnessKeepsAgentWhoseRecordsWereRemoved()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = RetentionSamples.DatabasePath(directory.Path);

        await WriteAgentAsync(store, RunOf(FirstAgent, 1, 2, Now.AddMinutes(-20)));
        await WriteAgentAsync(store, RunOf(SecondAgent, 1, 2, Now.AddMinutes(-10)));
        await TimelineSamples.ExecuteAsync(path, "DELETE FROM metrics; DELETE FROM records", TestContext.CancellationToken);

        Assert.AreEqual(2, await TimelineSamples.CountAsync(path, "SELECT count(*) FROM agent_streams", TestContext.CancellationToken), "arrangement: both stream rows remain");

        // Act
        var freshness = await store.FreshnessAsync(LiveScanLimit, Until, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(SecondAgent, freshness.AgentId, "the agent with the latest stored capture time");
        Assert.IsNull(freshness.ReceivedAt, "the highest-numbered record is gone");
        Assert.IsNull(freshness.LastLiveAt, "no metric record is left");
        Assert.IsNull(freshness.ScannedBackTo, "no row was scanned");
    }

    /// <summary>
    /// A metric record captured after the bound takes no place in the scan, so it hides no live record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task FreshnessIgnoresRecordsAfterUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAgentAsync(store, TimelineSamples.Run(1, 1, Now.AddSeconds(-30)));
        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, TimeSeriesSamples.Metric("system", "cpu_user_percent", Now.AddDays(1), 1));

        // Act
        var freshness = await store.FreshnessAsync(1, Until, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(Now.AddSeconds(-31), freshness.LastLiveAt, "the one scanned row is the live record, not the one a day ahead");
    }

    /// <summary>
    /// The freshness read returns the number of open sequence gaps of the agent with the earliest start and the latest end, also over a jump of 10^15 numbers.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task FreshnessReturnsSpanOfOpenSequenceGapsAfterJump()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAgentAsync(store, TimelineSamples.Run(1, 2, Now.AddMinutes(-9)));
        await WriteAgentAsync(store, TimelineSamples.Run(5, 2, Now.AddMinutes(-6)));
        await WriteAgentAsync(store, TimelineSamples.Run(Jump, 2, Now.AddMinutes(-3)));

        Assert.AreEqual(2, await OpenSequenceGapsAsync(directory.Path), "arrangement: two open sequence gaps, the second over the jump");

        // Act
        var freshness = await store.FreshnessAsync(LiveScanLimit, Until, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(FirstAgent, freshness.AgentId, "the agent of the stream");
        Assert.AreEqual(2, freshness.OpenSequenceGaps, "both open gaps are counted");
        Assert.AreEqual(Now.AddMinutes(-9).AddSeconds(-1), freshness.MissingStart, "the start of the first gap: the capture time of number 2");
        Assert.AreEqual(Now.AddMinutes(-3).AddSeconds(-2), freshness.MissingEnd, "the end of the gap over the jump: the capture time of the first number after it");
    }

    /// <summary>
    /// The freshness read counts only the open sequence gaps of the agent of the newest batch, not those of another agent ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task FreshnessIgnoresOpenGapsOfOtherAgentIds()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAgentAsync(store, RunOf(FirstAgent, 1, 2, Now.AddMinutes(-20)));
        await WriteAgentAsync(store, RunOf(FirstAgent, 10, 2, Now.AddMinutes(-19)));
        await WriteAgentAsync(store, RunOf(SecondAgent, 1, 2, Now.AddMinutes(-3)));
        await WriteAgentAsync(store, RunOf(SecondAgent, 5, 2, Now.AddMinutes(-2)));

        Assert.AreEqual(2, await OpenSequenceGapsAsync(directory.Path), "arrangement: one open sequence gap per agent");

        // Act
        var freshness = await store.FreshnessAsync(LiveScanLimit, Until, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(SecondAgent, freshness.AgentId, "the agent of the newest batch");
        Assert.AreEqual(Now.AddMinutes(-2), freshness.ReceivedAt, "the receive time of its highest-numbered record");
        Assert.AreEqual(1, freshness.OpenSequenceGaps, "only the gap of the newest batch's agent is counted");
        Assert.AreEqual(Now.AddMinutes(-3).AddSeconds(-1), freshness.MissingStart, "the start of that agent's gap");
        Assert.AreEqual(Now.AddMinutes(-2).AddSeconds(-2), freshness.MissingEnd, "the end of that agent's gap");
    }

    /// <summary>
    /// The service read returns the latest record per unit, anchored on the newest service record, and leaves out a unit whose latest record is older than the retirement.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task LatestServiceStatesReturnsLatestPerUnitWithinRetirement()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var newest = Now.AddSeconds(-30);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           Service("a.service", "active", "running", Now.AddHours(-4), 3, Now.AddHours(-1)),
                                           Service("a.service", "failed", "failed", Now.AddHours(-4), 4, newest),
                                           Service("b.service", "active", "running", Now.AddMinutes(-90), 0, Now.AddHours(-1)),
                                           Service("c.service", "active", "running", Now.AddHours(-5), 0, Now.AddHours(-3)),
                                           Service("e.service", "active", "exited", null, 1, newest.AddHours(-2)));

        // Act
        var page = await store.LatestServiceStatesAsync(ServiceQuery(256), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(newest, page.Newest, "the newest service record");
        Assert.AreEqual(3, page.Total, "the retired unit is not counted");
        Assert.AreEqual("a.service failed/failed 4 @-30s, b.service active/running 0 @-3600s, e.service active/exited 1 @-7230s", Describe(page.States), "the latest row per unit, non-active first, then by unit");

        var failed = page.States[0];

        Assert.IsNotNull(failed.ActiveEnterAt, "the time the unit last became active is returned");
        Assert.AreEqual(Now.AddHours(-4), DateTimeOffset.Parse(failed.ActiveEnterAt, CultureInfo.InvariantCulture), "the stored time the unit last became active");
        Assert.IsNull(page.States[2].ActiveEnterAt, "a unit without the time returns none");
    }

    /// <summary>
    /// The service read orders units whose state is not active first, then by unit in ordinal order, keeps to the limit and counts every unit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task LatestServiceStatesOrdersNonActiveFirstAndCountsTotal()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var at = Now.AddSeconds(-60);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           Service("c.service", "active", "running", null, 0, at),
                                           Service("y.service", "inactive", "dead", null, 0, at),
                                           Service("a.service", "active", "running", null, 0, at),
                                           Service("x.service", "failed", "failed", null, 2, at),
                                           Service("B.service", "active", "running", null, 0, at));

        // Act
        var page = await store.LatestServiceStatesAsync(ServiceQuery(3), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(5, page.Total, "every unit is counted beyond the limit");
        Assert.AreEqual("x.service failed/failed 2 @-60s, y.service inactive/dead 0 @-60s, B.service active/running 0 @-60s", Describe(page.States), "non-active units first, then by unit in ordinal order, at most the limit");
    }

    /// <summary>
    /// A service record captured after the bound neither anchors the retirement nor is returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task LatestServiceStatesIgnoresRecordAfterUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           Service("a.service", "active", "running", null, 0, Now.AddSeconds(-30)),
                                           Service("b.service", "failed", "failed", null, 0, Now.AddDays(1)));

        // Act
        var page = await store.LatestServiceStatesAsync(ServiceQuery(256), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(Now.AddSeconds(-30), page.Newest, "the newest record not after the bound");
        Assert.AreEqual(1, page.Total, "the record a day ahead is not counted");
        Assert.AreEqual("a.service active/running 0 @-30s", Describe(page.States), "the current unit is returned, the one a day ahead is not");
    }

    /// <summary>
    /// The latest-value read returns per series and label set the newest value within the window before the series' newest value, the label sets in ordinal order of their text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task LatestValuesReturnsNewestPerLabelSetWithinWindow()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric("system", "mem_available_bytes", Now.AddMinutes(-5), 100),
                                           TimeSeriesSamples.Metric("system", "mem_available_bytes", Now.AddSeconds(-30), 200),
                                           TimeSeriesSamples.Metric("mariadb", "mem_available_bytes", Now.AddSeconds(-10), 999),
                                           TimeSeriesSamples.Metric("system", "pressure_avg60", Now.AddSeconds(-60), 1.5, Pressure("memory", "some")),
                                           TimeSeriesSamples.Metric("system", "pressure_avg60", Now.AddSeconds(-60), 0.5, Pressure("memory", "full")),
                                           TimeSeriesSamples.Metric("system", "pressure_avg60", Now.AddMinutes(-9), 2, Pressure("io", "some")),
                                           TimeSeriesSamples.Metric("system", "pressure_avg60", Now.AddMinutes(-3), 3, Pressure("io", "some")),
                                           TimeSeriesSamples.Metric("system", "pressure_avg60", Now.AddMinutes(-20), 9, Pressure("cpu", "some")));

        // Act
        var result = await store.LatestValuesAsync(ValueQuery(16, new SeriesKey("system", "mem_available_bytes"), new SeriesKey("system", "pressure_avg60")), TestContext.CancellationToken);

        // Assert
        var memory = SeriesOf(result, "mem_available_bytes");
        var pressure = SeriesOf(result, "pressure_avg60");

        Assert.AreEqual(" = 200 @-30s", Describe(memory), "the newest value of the series without labels");
        Assert.AreEqual(1, memory.LabelSets, "one label set");
        Assert.AreEqual("kind=full,resource=memory = 0.5 @-60s; kind=some,resource=io = 3 @-180s; kind=some,resource=memory = 1.5 @-60s", Describe(pressure), "the newest value per label set within the window, in ordinal order of the label text");
        Assert.AreEqual(3, pressure.LabelSets, "the label set older than the window is not counted");
    }

    /// <summary>
    /// The latest-value read keeps the smallest label sets up to the limit and counts every label set of the window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task LatestValuesCountsLabelSetsBeyondTheLimit()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var mounts = new[] { "/e", "/d", "/c", "/b", "/a" };

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, mounts.Select(mount => Available(mount, Now.AddMinutes(-2), 1)).ToArray());
        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, mounts.Select(mount => Available(mount, Now.AddSeconds(-60), 2)).ToArray());

        // Act
        var result = await store.LatestValuesAsync(ValueQuery(2, new SeriesKey("system", "filesystem_available_bytes")), TestContext.CancellationToken);

        // Assert
        var available = SeriesOf(result, "filesystem_available_bytes");

        Assert.AreEqual("mount=/a = 2 @-60s; mount=/b = 2 @-60s", Describe(available), "the two smallest label sets with their newest values");
        Assert.AreEqual(5, available.LabelSets, "every label set is counted");
    }

    /// <summary>
    /// A value captured after the bound neither anchors the window nor is returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task LatestValuesIgnoresValueAfterUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric("system", "mem_available_bytes", Now.AddSeconds(-30), 200),
                                           TimeSeriesSamples.Metric("system", "mem_available_bytes", Now.AddDays(1), 999));

        // Act
        var result = await store.LatestValuesAsync(ValueQuery(16, new SeriesKey("system", "mem_available_bytes")), TestContext.CancellationToken);

        // Assert
        var memory = SeriesOf(result, "mem_available_bytes");

        Assert.AreEqual(" = 200 @-30s", Describe(memory), "the current value, not the one a day ahead");
        Assert.AreEqual(1, memory.LabelSets, "the value a day ahead adds no label set");
    }

    /// <summary>
    /// Of 300 distinct label sets of 32 labels of 1,000 bytes each, the latest-value read returns only the four smallest, complete, and counts all 300.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task LatestValuesKeepsOnlyTheLimitOfLargeLabelSets()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var chunk in Enumerable.Range(0, 300).Reverse().Chunk(100))
        {
            await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, chunk.Select(LargeLabelMetric).ToArray());
        }

        // Act
        var result = await store.LatestValuesAsync(ValueQuery(4, new SeriesKey("system", "large")), TestContext.CancellationToken);

        // Assert
        var large = SeriesOf(result, "large");

        Assert.AreEqual(300, large.LabelSets, "every label set is counted");
        Assert.AreEqual("0, 1, 2, 3", string.Join(", ", large.Values.Select(value => value.Value.ToString(CultureInfo.InvariantCulture))), "only the four smallest label sets are returned");
        Assert.IsTrue(large.Values.All(value => value.Labels.Count == 32), "each returned label set is complete");
        Assert.AreEqual(LargeLabelValue(3), large.Values[^1].Labels["k31"], "the label values are returned whole");
    }

    /// <summary>
    /// The MariaDB read returns the selected members of the newest status record of source <c>mariadb</c>.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task LatestMariaDbStatusReturnsSelectedMembers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           MariaDb("mariadb", Now.AddMinutes(-5), "100"),
                                           MariaDb("mariadb", Now.AddSeconds(-30), "151"),
                                           MariaDb("forged", Now.AddSeconds(-10), "999"));

        // Act
        var status = await store.LatestMariaDbStatusAsync(Until, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(status, "a status record is stored");
        Assert.AreEqual(Now.AddSeconds(-30), status.CapturedAt, "the newest record of source mariadb");
        Assert.AreEqual(MariaDbStatus.Up, status.Availability, "the availability");
        Assert.AreEqual(12d, status.ThreadsConnected, "Threads_connected");
        Assert.AreEqual(123456789d, status.MemoryUsed, "Memory_used");
        Assert.AreEqual(151L, status.MaxConnections, "max_connections parsed as an integer");
    }

    /// <summary>
    /// The MariaDB read returns no <c>max_connections</c> for a stored text that is not a non-negative integer of ASCII digits, such as <c>5</c> followed by U+202E.
    /// </summary>
    /// <param name="prefix">The text before the character</param>
    /// <param name="codePoint">The code point of the character appended to the prefix, or 0 for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("5", 0x202E)]
    [DataRow("", 0xFF15)]
    [DataRow("", 0x0661)]
    [DataRow("-1", 0)]
    [DataRow("+5", 0)]
    [DataRow("1.5", 0)]
    [DataRow("", 0)]
    [DataRow("99999999999999999999", 0)]
    [DataRow("123456789012345678901", 0)]
    public async Task LatestMariaDbStatusReturnsNoMaxConnectionsForTextThatIsNoInteger(string prefix, int codePoint)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var text = codePoint == 0 ? prefix : prefix + char.ConvertFromUtf32(codePoint);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, MariaDb("mariadb", Now.AddSeconds(-30), text));

        // Act
        var status = await store.LatestMariaDbStatusAsync(Until, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(status, "a status record is stored");
        Assert.AreEqual(MariaDbStatus.Up, status.Availability, "the record is read");
        Assert.IsNull(status.MaxConnections, "a text that is no non-negative integer of ASCII digits gives no value");
    }

    /// <summary>
    /// A MariaDB status record captured after the bound is not returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task LatestMariaDbStatusIgnoresRecordAfterUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           MariaDb("mariadb", Now.AddSeconds(-30), "151"),
                                           new DataRecord
                                           {
                                               Origin = RecordOrigin.Backend,
                                               Source = "mariadb",
                                               CapturedAt = Now.AddDays(1),
                                               Data = new MariaDbStatus
                                                      {
                                                          Availability = MariaDbStatus.Down,
                                                          Complete = true
                                                      }
                                           });

        // Act
        var status = await store.LatestMariaDbStatusAsync(Until, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(status, "a status record is stored");
        Assert.AreEqual(Now.AddSeconds(-30), status.CapturedAt, "the current record, not the one a day ahead");
        Assert.AreEqual(MariaDbStatus.Up, status.Availability, "the availability of the current record");
    }

    /// <summary>
    /// Creates a batch of consecutive metrics of an agent, one second apart, the last one captured a second before the batch is received.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="firstSeq">The number of the first record</param>
    /// <param name="count">The number of records</param>
    /// <param name="receivedAt">The receive time</param>
    /// <returns>The batch</returns>
    private static RecordBatch RunOf(string agentId, ulong firstSeq, int count, DateTimeOffset receivedAt)
    {
        var run = TimelineSamples.Run(firstSeq, count, receivedAt);

        return TimelineSamples.BatchOf(agentId, TimelineSamples.BootId, receivedAt, run.Records);
    }

    /// <summary>
    /// Creates a service state record.
    /// </summary>
    /// <param name="unit">The unit</param>
    /// <param name="activeState">The active state</param>
    /// <param name="subState">The sub state</param>
    /// <param name="activeEnterAt">The time the unit last became active, or <c>null</c> for none</param>
    /// <param name="restarts">The restart counter</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    private static DataRecord Service(string unit, string activeState, string subState, DateTimeOffset? activeEnterAt, uint restarts, DateTimeOffset capturedAt)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = "services",
                   CapturedAt = capturedAt,
                   Data = new ServiceState
                          {
                              Unit = unit,
                              LoadState = "loaded",
                              ActiveState = activeState,
                              SubState = subState,
                              ActiveEnterAt = activeEnterAt,
                              Restarts = restarts
                          }
               };
    }

    /// <summary>
    /// Creates a MariaDB status record of an available server.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="maxConnections">The stored text of <c>max_connections</c></param>
    /// <returns>The record</returns>
    private static DataRecord MariaDb(string source, DateTimeOffset capturedAt, string maxConnections)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = source,
                   CapturedAt = capturedAt,
                   Data = new MariaDbStatus
                          {
                              Availability = MariaDbStatus.Up,
                              Status = new Dictionary<string, ulong>
                                       {
                                           ["Threads_connected"] = 12,
                                           ["Memory_used"] = 123456789,
                                           ["Uptime"] = 5
                                       },
                              Variables = new Dictionary<string, string>
                                          {
                                              ["max_connections"] = maxConnections,
                                              ["version"] = "11.4.2"
                                          },
                              Complete = true
                          }
               };
    }

    /// <summary>
    /// Creates the labels of a pressure metric.
    /// </summary>
    /// <param name="resource">The resource</param>
    /// <param name="kind">The kind</param>
    /// <returns>The labels</returns>
    private static Dictionary<string, string> Pressure(string resource, string kind)
    {
        return new Dictionary<string, string>
               {
                   ["resource"] = resource,
                   ["kind"] = kind
               };
    }

    /// <summary>
    /// Creates a metric of the available space of a mount.
    /// </summary>
    /// <param name="mount">The mount point</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="value">The value</param>
    /// <returns>The record</returns>
    private static DataRecord Available(string mount, DateTimeOffset capturedAt, double value)
    {
        return TimeSeriesSamples.Metric("system",
                                        "filesystem_available_bytes",
                                        capturedAt,
                                        value,
                                        new Dictionary<string, string>
                                        {
                                            ["mount"] = mount
                                        });
    }

    /// <summary>
    /// Creates a metric with a distinct label set of 32 labels of 1,000 bytes each, whose label text sorts by its index.
    /// </summary>
    /// <param name="index">The index of the label set, also the value</param>
    /// <returns>The record</returns>
    private static DataRecord LargeLabelMetric(int index)
    {
        var labels = Enumerable.Range(0, 32).ToDictionary(label => "k" + label.ToString("D2", CultureInfo.InvariantCulture), _ => LargeLabelValue(index));

        return TimeSeriesSamples.Metric("system", "large", Now.AddSeconds(-60), index, labels);
    }

    /// <summary>
    /// Creates a label value of 1,000 bytes that starts with the index of its label set.
    /// </summary>
    /// <param name="index">The index of the label set</param>
    /// <returns>The value</returns>
    private static string LargeLabelValue(int index)
    {
        return index.ToString("D3", CultureInfo.InvariantCulture) + new string('v', 997);
    }

    /// <summary>
    /// Creates a latest-value query of series with the bound of the tests and a window of 10 minutes.
    /// </summary>
    /// <param name="maxLabelSets">The number of label sets kept per series</param>
    /// <param name="series">The series</param>
    /// <returns>The query</returns>
    private static LatestValueQuery ValueQuery(int maxLabelSets, params SeriesKey[] series)
    {
        return new LatestValueQuery
               {
                   Series = series,
                   Window = TimeSpan.FromMinutes(10),
                   MaxLabelSets = maxLabelSets,
                   Until = Until
               };
    }

    /// <summary>
    /// Creates a service query with the bound of the tests and a retirement of 2 hours.
    /// </summary>
    /// <param name="limit">The number of units returned</param>
    /// <returns>The query</returns>
    private static LatestServiceQuery ServiceQuery(int limit)
    {
        return new LatestServiceQuery
               {
                   Retirement = TimeSpan.FromHours(2),
                   Limit = limit,
                   Until = Until
               };
    }

    /// <summary>
    /// Finds the result of a series of source <c>system</c> by name.
    /// </summary>
    /// <param name="result">The result of the read</param>
    /// <param name="name">The metric name</param>
    /// <returns>The series</returns>
    private static LatestSeries SeriesOf(IReadOnlyList<LatestSeries> result, string name)
    {
        var matches = result.Where(series => series.Series.Source == "system" && series.Series.Name == name).ToList();

        Assert.HasCount(1, matches, $"one result for the series {name}");

        return matches[0];
    }

    /// <summary>
    /// Describes the values of a series as labels, value and capture time in seconds relative to <see cref="Now"/>, in returned order.
    /// </summary>
    /// <param name="series">The series</param>
    /// <returns>The description</returns>
    private static string Describe(LatestSeries series)
    {
        return string.Join("; ", series.Values.Select(value => string.Create(CultureInfo.InvariantCulture, $"{DescribeLabels(value.Labels)} = {value.Value} @{Seconds(value.CapturedAt)}")));
    }

    /// <summary>
    /// Describes service states as unit, states, restarts and capture time in seconds relative to <see cref="Now"/>, in returned order.
    /// </summary>
    /// <param name="states">The states</param>
    /// <returns>The description</returns>
    private static string Describe(IReadOnlyList<LatestServiceState> states)
    {
        return string.Join(", ", states.Select(state => $"{state.Unit} {state.ActiveState}/{state.SubState} {state.Restarts.ToString(CultureInfo.InvariantCulture)} @{Seconds(state.CapturedAt)}"));
    }

    /// <summary>
    /// Describes labels as their pairs in ordinal order of the keys.
    /// </summary>
    /// <param name="labels">The labels</param>
    /// <returns>The description</returns>
    private static string DescribeLabels(IReadOnlyDictionary<string, string> labels)
    {
        return string.Join(",", labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// Formats a time as whole seconds relative to <see cref="Now"/>.
    /// </summary>
    /// <param name="at">The time</param>
    /// <returns>The seconds with a unit</returns>
    private static string Seconds(DateTimeOffset at)
    {
        return ((long)(at - Now).TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// Writes an agent batch and checks that every record was stored.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="batch">The batch</param>
    /// <returns>A task that completes when the batch is stored</returns>
    private async Task WriteAgentAsync(SqliteStore store, RecordBatch batch)
    {
        var result = await store.WriteBatchAsync(batch, TestContext.CancellationToken);

        Assert.AreEqual(batch.Records.Count, result.Stored, "every record of the arranged agent batch is stored");
    }

    /// <summary>
    /// Counts the open sequence gaps of every agent.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the number</returns>
    private async Task<long> OpenSequenceGapsAsync(string directory)
    {
        return await TimelineSamples.CountAsync(RetentionSamples.DatabasePath(directory), "SELECT count(*) FROM gaps WHERE kind = 'sequence' AND state = 'open'", TestContext.CancellationToken);
    }

    #endregion // Methods
}