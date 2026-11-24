using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the history reads of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public partial class SqliteStoreHistoryTests
{
    #region Constants

    private const string LimitText = "store: invalid query: limit must be 1 to 256";
    private const string KindText = "store: invalid query: kind must be a snapshot kind";
    private const string WindowText = "store: invalid query: window must be positive and at most one day";
    private const string SourceText = "store: invalid query: source must not be empty";
    private const string AtText = "store: invalid query: at must be within the storable range";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _moment = Samples.Base.AddMinutes(30);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Raw metrics and hourly rollups are listed together in ordinal order of source and name, once each however many label sets they have.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task StoredMetricsListsRawAndHourlyInOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric("web", "rps", Samples.Base, 1),
                                           TimeSeriesSamples.Metric("host", "load", Samples.Base, 1),
                                           TimeSeriesSamples.Metric("host", "cpu", Samples.Base, 1, TimeSeriesSamples.Mode("user")),
                                           TimeSeriesSamples.Metric("host", "cpu", Samples.Base, 2, TimeSeriesSamples.Mode("idle")));
        await InsertRollupAsync(directory.Path, "host", "load");
        await InsertRollupAsync(directory.Path, "alpha", "disk_used");
        await InsertRollupAsync(directory.Path, "host", "mem");
        await InsertRollupAsync(directory.Path, "Zeta", "a");

        // Act
        var list = await store.StoredMetricsAsync(StorageLimits.MaxStoredMetrics, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("Zeta a, alpha disk_used, host cpu, host load, host mem, web rps", Describe(list), "ordinal order, a metric stored both ways once");
        Assert.IsFalse(list.More, "nothing beyond the limit");
    }

    /// <summary>
    /// The list stops at the limit and says so only when a further metric exists.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task StoredMetricsStopsAtTheLimitAndSetsMore()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric("s", "a1", Samples.Base, 1),
                                           TimeSeriesSamples.Metric("s", "a2", Samples.Base, 1),
                                           TimeSeriesSamples.Metric("s", "a3", Samples.Base, 1),
                                           TimeSeriesSamples.Metric("t", "a4", Samples.Base, 1),
                                           TimeSeriesSamples.Metric("t", "a5", Samples.Base, 1));
        await InsertRollupAsync(directory.Path, "s", "b1");
        await InsertRollupAsync(directory.Path, "t", "b2");
        await InsertRollupAsync(directory.Path, "u", "b3");

        // Act
        var all = await store.StoredMetricsAsync(8, TestContext.CancellationToken);
        var seven = await store.StoredMetricsAsync(7, TestContext.CancellationToken);
        var one = await store.StoredMetricsAsync(1, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(8, all.Metrics, "exactly the limit");
        Assert.IsFalse(all.More, "no further metric beyond exactly the limit");
        Assert.AreEqual("s a1, s a2, s a3, s b1, t a4, t a5, t b2, u b3", Describe(all), "all eight in order");
        Assert.AreEqual("s a1, s a2, s a3, s b1, t a4, t a5, t b2", Describe(seven), "the first seven");
        Assert.IsTrue(seven.More, "one more exists beyond seven");
        Assert.AreEqual("s a1", Describe(one), "the first one");
        Assert.IsTrue(one.More, "more exist beyond one");
    }

    /// <summary>
    /// The statements of the metric list search the indexes one seek at a time: no scan, no temporary tree.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task StoredMetricsSeeksTheIndexes()
    {
        // Arrange
        var statements = new (string Name, string Sql, string Table)[]
                         {
                             (nameof(HistoryQueries.MetricsFirst), HistoryQueries.MetricsFirst, "metrics"),
                             (nameof(HistoryQueries.MetricsNextName), HistoryQueries.MetricsNextName, "metrics"),
                             (nameof(HistoryQueries.MetricsNextSource), HistoryQueries.MetricsNextSource, "metrics"),
                             (nameof(HistoryQueries.RollupsFirst), HistoryQueries.RollupsFirst, "metric_rollups_1h"),
                             (nameof(HistoryQueries.RollupsNextName), HistoryQueries.RollupsNextName, "metric_rollups_1h"),
                             (nameof(HistoryQueries.RollupsNextSource), HistoryQueries.RollupsNextSource, "metric_rollups_1h")
                         };

        // Act
        var plans = await ExplainAsync(statements.Select(statement => (statement.Name, statement.Sql)).ToList());

        // Assert
        foreach (var (name, sql, table) in statements)
        {
            var details = plans[name];

            Assert.IsNotEmpty(sql, $"{name} is a statement");
            Assert.DoesNotContain(detail => ScanPattern().IsMatch(detail), details, $"{name} scans a table: {string.Join(" | ", details)}");
            Assert.DoesNotContain(detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal), details, $"{name} builds a temporary tree: {string.Join(" | ", details)}");
            Assert.Contains(detail => SearchPattern(table).IsMatch(detail), details, $"{name} searches {table} by its index or primary key: {string.Join(" | ", details)}");
        }
    }

    /// <summary>
    /// A limit outside 1 to 256 is refused with the fixed text.
    /// </summary>
    /// <param name="limit">The limit</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(StorageLimits.MaxStoredMetrics + 1)]
    [DataRow(int.MaxValue)]
    [DataRow(int.MinValue)]
    public async Task StoredMetricsRefusesLimitOutOfRange(int limit)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.StoredMetricsAsync(limit, TestContext.CancellationToken), "the limit is refused");

        // Assert
        Assert.AreEqual(LimitText, exception.Message, "the text of the refusal");
    }

    /// <summary>
    /// Each side reads the closest snapshot of the kind inside the window: at or before the moment, or strictly after it, and nothing outside the window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SnapshotStampReadsTheClosestRowOfEachSide()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          Snapshot(RecordKind.ProcessSnapshot, -15),
                                          Snapshot(RecordKind.ProcessSnapshot, -6),
                                          Snapshot(RecordKind.ProcessSnapshot, -2),
                                          Snapshot(RecordKind.ConnectionSnapshot, -1),
                                          Snapshot(RecordKind.ConnectionSnapshot, 1),
                                          Snapshot(RecordKind.ProcessSnapshot, 3),
                                          Snapshot(RecordKind.ProcessSnapshot, 7),
                                          Snapshot(RecordKind.ProcessSnapshot, 12));

        var before = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, -2);
        var after = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, 3);

        // Act
        var atOrBefore = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 10, SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var afterStamp = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 10, SnapshotSide.After), TestContext.CancellationToken);
        var connection = await store.SnapshotStampAsync(Query(RecordKind.ConnectionSnapshot, _moment, 10, SnapshotSide.After), TestContext.CancellationToken);
        var exactlyAt = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), 10, SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var strictlyAfter = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), 10, SnapshotSide.After), TestContext.CancellationToken);
        var narrow = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 1, SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var narrowAfter = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 2, SnapshotSide.After), TestContext.CancellationToken);
        var edgeBefore = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 2, SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var edgeAfter = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 3, SnapshotSide.After), TestContext.CancellationToken);
        var wide = await store.SnapshotStampAsync(new SnapshotQuery
                                                  {
                                                      Kind = RecordKind.ProcessSnapshot,
                                                      At = _moment,
                                                      Window = TimeSpan.FromDays(1),
                                                      Side = SnapshotSide.AtOrBefore
                                                  },
                                                  TestContext.CancellationToken);

        // Assert
        AssertStamp(atOrBefore, before, RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), "closest at or before, the row 6 and 15 minutes back are farther");
        AssertStamp(afterStamp, after, RecordKind.ProcessSnapshot, _moment.AddMinutes(3), "closest after, not the later ones");
        Assert.AreEqual(RecordKind.ConnectionSnapshot, connection?.Kind, "the connection kind reads its own rows");
        Assert.AreEqual(_moment.AddMinutes(1), connection?.CapturedAt, "the connection row after the moment");
        AssertStamp(exactlyAt, before, RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), "a row exactly at the moment counts for the side at or before");
        AssertStamp(strictlyAfter, after, RecordKind.ProcessSnapshot, _moment.AddMinutes(3), "a row exactly at the moment does not count for the side after");
        Assert.IsNull(narrow, "no row within one minute before");
        Assert.IsNull(narrowAfter, "no row within two minutes after");
        AssertStamp(edgeBefore, before, RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), "a row exactly one window back counts");
        AssertStamp(edgeAfter, after, RecordKind.ProcessSnapshot, _moment.AddMinutes(3), "a row exactly one window ahead counts");
        AssertStamp(wide, before, RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), "a window of one day is allowed");
    }

    /// <summary>
    /// Two snapshots captured at the same time give the lower ID on either side.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SnapshotStampTakesTheLowerIdOnATie()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          Snapshot(RecordKind.ProcessSnapshot, -4),
                                          Snapshot(RecordKind.ProcessSnapshot, -4),
                                          Snapshot(RecordKind.ProcessSnapshot, 5),
                                          Snapshot(RecordKind.ProcessSnapshot, 5));

        var lowerBefore = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, -4);
        var lowerAfter = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, 5);

        // Act
        var before = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 10, SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var after = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 10, SnapshotSide.After), TestContext.CancellationToken);

        // Assert
        AssertStamp(before, lowerBefore, RecordKind.ProcessSnapshot, _moment.AddMinutes(-4), "the lower ID before");
        AssertStamp(after, lowerAfter, RecordKind.ProcessSnapshot, _moment.AddMinutes(5), "the lower ID after");
    }

    /// <summary>
    /// The record of a stamp is read once with its payload, and a stamp whose row was removed or has another kind reads nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SnapshotReadsTheRecordOfTheStamp()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, Snapshot(RecordKind.ProcessSnapshot, -2), Snapshot(RecordKind.ProcessSnapshot, 3));

        var stamp = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 10, SnapshotSide.AtOrBefore), TestContext.CancellationToken);

        Assert.IsNotNull(stamp, "the arranged stamp");

        var otherKind = new SnapshotStamp
                        {
                            Id = stamp.Id,
                            Kind = RecordKind.ConnectionSnapshot,
                            CapturedAt = stamp.CapturedAt
                        };
        var removed = await store.SnapshotStampAsync(Query(RecordKind.ProcessSnapshot, _moment, 10, SnapshotSide.After), TestContext.CancellationToken);

        Assert.IsNotNull(removed, "the arranged second stamp");

        // Act
        var record = await store.SnapshotAsync(stamp, TestContext.CancellationToken);
        var mismatch = await store.SnapshotAsync(otherKind, TestContext.CancellationToken);

        await RetentionSamples.ExecuteAsync(directory.Path, $"DELETE FROM records WHERE id = {removed.Id.ToString(CultureInfo.InvariantCulture)}", TestContext.CancellationToken);

        var gone = await store.SnapshotAsync(removed, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(record, "the record of the stamp");
        Assert.AreEqual(stamp.Id, record.Id, "record ID");
        Assert.AreEqual(RecordKind.ProcessSnapshot, record.Record.Data?.Kind, "record kind");
        Assert.AreEqual(_moment.AddMinutes(-2), record.Record.CapturedAt, "capture time");
        Assert.AreEqual("processes", record.Record.Source, "source");

        var snapshot = record.Record.Data as ProcessSnapshot;

        Assert.IsNotNull(snapshot, "the payload is decoded");
        Assert.AreEqual("nginx", snapshot.Programs![0].Program, "the program of the payload");
        Assert.IsNull(mismatch, "a stamp with the kind of another record reads nothing");
        Assert.IsNull(gone, "a removed row reads nothing");
    }

    /// <summary>
    /// The stamp statements and the record statement search by the kind and time index and by the record ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SnapshotSeeksTheKindTimeIndex()
    {
        // Arrange
        var statements = new List<(string Name, string Sql)>
                         {
                             (nameof(HistoryQueries.StampAtOrBefore), HistoryQueries.StampAtOrBefore),
                             (nameof(HistoryQueries.StampAfter), HistoryQueries.StampAfter)
                         };

        // Act
        var plans = await ExplainAsync(statements);

        // Assert
        foreach (var (name, sql) in statements)
        {
            var details = plans[name];

            Assert.IsNotEmpty(sql, $"{name} is a statement");
            Assert.DoesNotContain(detail => ScanPattern().IsMatch(detail), details, $"{name} scans a table: {string.Join(" | ", details)}");
            Assert.Contains(detail => KindTimePattern().IsMatch(detail), details, $"{name} searches records_kind_time: {string.Join(" | ", details)}");
        }

        Assert.DoesNotContain(detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal), plans[nameof(HistoryQueries.StampAfter)], "the side after is ordered by the index alone");
        Assert.IsTrue(plans[nameof(HistoryQueries.StampAtOrBefore)].Where(detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal)).All(detail => detail == "USE TEMP B-TREE FOR LAST TERM OF ORDER BY"), "the side at or before sorts only the ID term");
    }

    /// <summary>
    /// The statement that reads a snapshot record seeks it by its ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SnapshotReadsSeekTheIndexes()
    {
        // Arrange
        var statements = new List<(string Name, string Sql)>
                         {
                             (nameof(HistoryQueries.SnapshotRecord), HistoryQueries.SnapshotRecord)
                         };

        // Act
        var plans = await ExplainAsync(statements);

        // Assert
        var details = plans[nameof(HistoryQueries.SnapshotRecord)];

        Assert.IsNotEmpty(HistoryQueries.SnapshotRecord, "the record read is a statement");
        Assert.DoesNotContain(detail => ScanPattern().IsMatch(detail), details, $"the record read scans a table: {string.Join(" | ", details)}");
        Assert.Contains(detail => RowIdPattern().IsMatch(detail), details, $"the record read searches by the record ID: {string.Join(" | ", details)}");
    }

    /// <summary>
    /// A query with another kind, a window of zero, below zero or above one day, or a moment outside the storable range is refused with its own text.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="windowSeconds">The window in seconds</param>
    /// <param name="year">The year of the moment</param>
    /// <param name="expected">The text of the refusal</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow("metric", 60.0, 2026, KindText)]
    [DataRow("", 60.0, 2026, KindText)]
    [DataRow("log_line", 60.0, 2026, KindText)]
    [DataRow("process_snapshot", 0.0, 2026, WindowText)]
    [DataRow("process_snapshot", -1.0, 2026, WindowText)]
    [DataRow("process_snapshot", 86401.0, 2026, WindowText)]
    [DataRow("connection_snapshot", 0.0, 2026, WindowText)]
    [DataRow("process_snapshot", 60.0, 1, AtText)]
    [DataRow("connection_snapshot", 60.0, 9999, AtText)]
    public async Task SnapshotStampRefusesInvalidQuery(string kind, double windowSeconds, int year, string expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var query = new SnapshotQuery
                    {
                        Kind = kind,
                        At = new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero),
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        Side = SnapshotSide.After
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SnapshotStampAsync(query, TestContext.CancellationToken), "the query is refused");

        // Assert
        Assert.AreEqual(expected, exception.Message, "the text of the refusal");
    }

    /// <summary>
    /// A stamp with a kind that is no snapshot kind is refused before the record is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SnapshotRefusesStampOfAnotherKind()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base, "cpu", 1));

        var stamp = new SnapshotStamp
                    {
                        Id = 1,
                        Kind = RecordKind.Metric,
                        CapturedAt = Samples.Base
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SnapshotAsync(stamp, TestContext.CancellationToken), "the stamp is refused");

        // Assert
        Assert.AreEqual(KindText, exception.Message, "the text of the refusal");
    }

    /// <summary>
    /// With a source set, each side reads the closest snapshot of that source only, and a source with no snapshot reads nothing; without one, any source counts as before.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SnapshotStampOfASourceSkipsOtherSources()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          SourceSnapshot("alpha", -5),
                                          SourceSnapshot("beta", -2),
                                          SourceSnapshot("beta", 1),
                                          SourceSnapshot("alpha", 3));

        var alphaBefore = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, -5);
        var alphaAfter = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, 3);
        var betaBefore = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, -2);
        var betaAfter = await IdAtAsync(directory.Path, RecordKind.ProcessSnapshot, 1);

        // Act
        var alphaBeforeStamp = await store.SnapshotStampAsync(SourceQuery("alpha", SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var alphaAfterStamp = await store.SnapshotStampAsync(SourceQuery("alpha", SnapshotSide.After), TestContext.CancellationToken);
        var betaBeforeStamp = await store.SnapshotStampAsync(SourceQuery("beta", SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var betaAfterStamp = await store.SnapshotStampAsync(SourceQuery("beta", SnapshotSide.After), TestContext.CancellationToken);
        var unknownBefore = await store.SnapshotStampAsync(SourceQuery("gamma", SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var unknownAfter = await store.SnapshotStampAsync(SourceQuery("gamma", SnapshotSide.After), TestContext.CancellationToken);
        var anyBefore = await store.SnapshotStampAsync(SourceQuery(null, SnapshotSide.AtOrBefore), TestContext.CancellationToken);
        var anyAfter = await store.SnapshotStampAsync(SourceQuery(null, SnapshotSide.After), TestContext.CancellationToken);

        // Assert
        AssertStamp(alphaBeforeStamp, alphaBefore, RecordKind.ProcessSnapshot, _moment.AddMinutes(-5), "alpha before the moment, not the closer row of beta");
        AssertStamp(alphaAfterStamp, alphaAfter, RecordKind.ProcessSnapshot, _moment.AddMinutes(3), "alpha after the moment, not the closer row of beta");
        AssertStamp(betaBeforeStamp, betaBefore, RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), "beta before the moment");
        AssertStamp(betaAfterStamp, betaAfter, RecordKind.ProcessSnapshot, _moment.AddMinutes(1), "beta after the moment, not the later row of alpha");
        Assert.IsNull(unknownBefore, "a source without a snapshot has none before the moment");
        Assert.IsNull(unknownAfter, "a source without a snapshot has none after the moment");
        AssertStamp(anyBefore, betaBefore, RecordKind.ProcessSnapshot, _moment.AddMinutes(-2), "without a source the closest row of any source is read");
        AssertStamp(anyAfter, betaAfter, RecordKind.ProcessSnapshot, _moment.AddMinutes(1), "without a source the closest row of any source is read after the moment");
    }

    /// <summary>
    /// The two statements that read the stamp of one source search by the kind, source and time index and scan no table.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SnapshotStampOfASourceSeeksTheKindSourceTimeIndex()
    {
        // Arrange
        var statements = SourceStampStatements();

        // Act
        var plans = await ExplainAsync(statements);

        // Assert
        Assert.HasCount(2, statements, "one statement per side reads the stamp of a source");

        foreach (var (name, _) in statements)
        {
            var details = plans[name];

            Assert.DoesNotContain(detail => ScanPattern().IsMatch(detail), details, $"{name} scans a table: {string.Join(" | ", details)}");
            Assert.Contains(detail => KindSourceTimePattern().IsMatch(detail), details, $"{name} searches records_kind_source_time by kind and source: {string.Join(" | ", details)}");
        }
    }

    /// <summary>
    /// An empty source is refused with its own text, on either side.
    /// </summary>
    /// <param name="side">The side</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(SnapshotSide.AtOrBefore)]
    [DataRow(SnapshotSide.After)]
    public async Task SnapshotStampRefusesAnEmptySource(SnapshotSide side)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var query = SourceQuery(string.Empty, side);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SnapshotStampAsync(query, TestContext.CancellationToken), "the empty source is refused");

        // Assert
        Assert.AreEqual("store: invalid query: source must not be empty", exception.Message, "the text of the refusal");
    }

    /// <summary>
    /// The latest stamp of a kind and source is the one with the latest capture time inside the range, the lowest ID on a tie; other sources, other kinds and times outside the range are skipped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreLatestSnapshotStampReadsTheLatestOfTheSource()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "alpha", -5),
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "alpha", -2),
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "alpha", -2),
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "beta", 3),
                                          KindSnapshot(RecordKind.ProcessSnapshot, "alpha", 4),
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "alpha", 20));

        var lowerId = await IdAtAsync(directory.Path, RecordKind.ConnectionSnapshot, -2);

        // Act
        var alpha = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", -10, 10), TestContext.CancellationToken);
        var beta = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "beta", -10, 10), TestContext.CancellationToken);
        var unknown = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "gamma", -10, 10), TestContext.CancellationToken);
        var wider = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", -10, 30), TestContext.CancellationToken);

        // Assert
        AssertStamp(alpha, lowerId, RecordKind.ConnectionSnapshot, _moment.AddMinutes(-2), "the latest of alpha in the range, the lower ID on the tie, not the other kind or source");
        Assert.IsNotNull(beta, "beta has a snapshot in the range");
        Assert.AreEqual(_moment.AddMinutes(3), beta.CapturedAt, "the snapshot of beta");
        Assert.IsNull(unknown, "a source without a snapshot reads nothing");
        Assert.AreEqual(_moment.AddMinutes(20), wider?.CapturedAt, "a wider range reaches the later snapshot");
    }

    /// <summary>
    /// The range includes its start and excludes its end, and an empty or reversed range reads nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreLatestSnapshotStampBoundsAreStartInclusiveEndExclusive()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "alpha", -4),
                                          KindSnapshot(RecordKind.ConnectionSnapshot, "alpha", 4));

        // Act
        var atStart = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", -4, -3), TestContext.CancellationToken);
        var atEnd = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", -3, 4), TestContext.CancellationToken);
        var justAfter = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", -3, 5), TestContext.CancellationToken);
        var empty = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", 4, 4), TestContext.CancellationToken);
        var reversed = await store.LatestSnapshotStampAsync(LatestQuery(RecordKind.ConnectionSnapshot, "alpha", 5, -5), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(_moment.AddMinutes(-4), atStart?.CapturedAt, "a snapshot at the start of the range is inside");
        Assert.IsNull(atEnd, "a snapshot at the end of the range is outside");
        Assert.AreEqual(_moment.AddMinutes(4), justAfter?.CapturedAt, "a snapshot just before the end is inside");
        Assert.IsNull(empty, "an empty range reads nothing");
        Assert.IsNull(reversed, "a reversed range reads nothing");
    }

    /// <summary>
    /// A kind that is no snapshot kind, an empty source or a bound outside the storable range is refused with its own text.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="source">The source</param>
    /// <param name="year">The year of the start, or 0 for a stored one</param>
    /// <param name="endYear">The year of the end, or 0 for a stored one</param>
    /// <param name="expected">The text of the refusal</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("metric", "alpha", 0, 0, KindText)]
    [DataRow("", "alpha", 0, 0, KindText)]
    [DataRow("log_line", "alpha", 0, 0, KindText)]
    [DataRow("connection_snapshot", "", 0, 0, SourceText)]
    [DataRow("process_snapshot", "", 0, 0, SourceText)]
    [DataRow("connection_snapshot", "alpha", 1, 0, AtText)]
    [DataRow("connection_snapshot", "alpha", 0, 9999, AtText)]
    public async Task SqliteStoreLatestSnapshotStampRefusesInvalidQuery(string kind, string source, int year, int endYear, string expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var query = new LatestSnapshotQuery
                    {
                        Kind = kind,
                        Source = source,
                        From = year == 0 ? _moment.AddMinutes(-10) : new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero),
                        To = endYear == 0 ? _moment.AddMinutes(10) : new DateTimeOffset(endYear, 6, 1, 0, 0, 0, TimeSpan.Zero)
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.LatestSnapshotStampAsync(query, TestContext.CancellationToken), "the query is refused");

        // Assert
        Assert.AreEqual(expected, exception.Message, "the text of the refusal");
    }

    /// <summary>
    /// The two statements of the latest stamp read search by the kind, source and time index and scan no table, whatever the range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreLatestSnapshotStampSeeksTheKindSourceTimeIndex()
    {
        // Arrange
        var statements = LatestStampStatements();

        // Act
        var plans = await ExplainAsync(statements);

        // Assert
        Assert.HasCount(2, statements, "two statements, named with Latest, read the latest stamp: the latest time, then the lowest ID at it");

        foreach (var (name, _) in statements)
        {
            var details = plans[name];

            Assert.DoesNotContain(detail => ScanPattern().IsMatch(detail), details, $"{name} scans a table: {string.Join(" | ", details)}");
            Assert.Contains(detail => KindSourceTimePattern().IsMatch(detail), details, $"{name} searches records_kind_source_time by kind and source: {string.Join(" | ", details)}");
        }
    }

    /// <summary>
    /// Creates a process snapshot record of a source captured some minutes from the moment.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="minutes">Minutes from <see cref="_moment"/></param>
    /// <returns>The record</returns>
    private static DataRecord SourceSnapshot(string source, int minutes)
    {
        var record = Snapshot(RecordKind.ProcessSnapshot, minutes);

        record.Source = source;

        return record;
    }

    /// <summary>
    /// Creates a process snapshot query of a source with a window of ten minutes around the moment.
    /// </summary>
    /// <param name="source">The source, <c>null</c> for any</param>
    /// <param name="side">The side</param>
    /// <returns>The query</returns>
    private static SnapshotQuery SourceQuery(string? source, SnapshotSide side)
    {
        return new SnapshotQuery
               {
                   Kind = RecordKind.ProcessSnapshot,
                   At = _moment,
                   Window = TimeSpan.FromMinutes(10),
                   Side = side,
                   Source = source
               };
    }

    /// <summary>
    /// Creates a snapshot record of a kind and source captured some minutes from the moment.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="source">The source</param>
    /// <param name="minutes">Minutes from <see cref="_moment"/></param>
    /// <returns>The record</returns>
    private static DataRecord KindSnapshot(string kind, string source, int minutes)
    {
        var record = Snapshot(kind, minutes);

        record.Source = source;

        return record;
    }

    /// <summary>
    /// Creates a latest stamp query of a range given in minutes from the moment.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="source">The source</param>
    /// <param name="fromMinutes">The start, minutes from <see cref="_moment"/></param>
    /// <param name="toMinutes">The end, minutes from <see cref="_moment"/></param>
    /// <returns>The query</returns>
    private static LatestSnapshotQuery LatestQuery(string kind, string source, int fromMinutes, int toMinutes)
    {
        return new LatestSnapshotQuery
               {
                   Kind = kind,
                   Source = source,
                   From = _moment.AddMinutes(fromMinutes),
                   To = _moment.AddMinutes(toMinutes)
               };
    }

    /// <summary>
    /// Finds the statements of <see cref="HistoryQueries"/> that read the latest stamp: constants whose name contains <c>Latest</c> and that read the records.
    /// </summary>
    /// <returns>The names and texts</returns>
    private static List<(string Name, string Sql)> LatestStampStatements()
    {
        return typeof(HistoryQueries).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                     .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string) && field.Name.Contains("Latest", StringComparison.Ordinal))
                                     .Select(field => (field.Name, Sql: (string)field.GetRawConstantValue()!))
                                     .Where(statement => statement.Sql.Contains("FROM records", StringComparison.Ordinal))
                                     .ToList();
    }

    /// <summary>
    /// Finds the stamp statements of <see cref="HistoryQueries"/> that filter by a source.
    /// </summary>
    /// <returns>The names and texts</returns>
    private static List<(string Name, string Sql)> SourceStampStatements()
    {
        return typeof(HistoryQueries).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                     .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
                                     .Select(field => (field.Name, Sql: (string)field.GetRawConstantValue()!))
                                     .Where(statement => statement.Name.Contains("Latest", StringComparison.Ordinal) == false)
                                     .Where(statement => statement.Sql.Contains("FROM records", StringComparison.Ordinal) && statement.Sql.Contains("source = $source", StringComparison.Ordinal))
                                     .ToList();
    }

    /// <summary>
    /// Creates a snapshot record of a kind captured some minutes from the moment.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="minutes">Minutes from <see cref="_moment"/></param>
    /// <returns>The record</returns>
    private static DataRecord Snapshot(string kind, int minutes)
    {
        return RetentionSamples.OfKind(kind, RecordOrigin.Backend, _moment.AddMinutes(minutes));
    }

    /// <summary>
    /// Creates a query of a window in minutes.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="at">The moment</param>
    /// <param name="minutes">The window in minutes</param>
    /// <param name="side">The side</param>
    /// <returns>The query</returns>
    private static SnapshotQuery Query(string kind, DateTimeOffset at, int minutes, SnapshotSide side)
    {
        return new SnapshotQuery
               {
                   Kind = kind,
                   At = at,
                   Window = TimeSpan.FromMinutes(minutes),
                   Side = side
               };
    }

    /// <summary>
    /// Checks a stamp.
    /// </summary>
    /// <param name="stamp">The stamp</param>
    /// <param name="id">The expected ID</param>
    /// <param name="kind">The expected kind</param>
    /// <param name="capturedAt">The expected capture time</param>
    /// <param name="because">What the stamp shows</param>
    private static void AssertStamp(SnapshotStamp? stamp, long id, string kind, DateTimeOffset capturedAt, string because)
    {
        Assert.IsNotNull(stamp, $"a stamp is read: {because}");
        Assert.AreEqual(id, stamp.Id, $"ID: {because}");
        Assert.AreEqual(kind, stamp.Kind, $"kind: {because}");
        Assert.AreEqual(capturedAt, stamp.CapturedAt, $"capture time: {because}");
    }

    /// <summary>
    /// Describes the metrics of a list as <c>source name</c>.
    /// </summary>
    /// <param name="list">The list</param>
    /// <returns>The descriptions</returns>
    private static string Describe(StoredMetricList list)
    {
        return string.Join(", ", list.Metrics.Select(metric => $"{metric.Source} {metric.Name}"));
    }

    /// <summary>
    /// Creates the pattern of a table scan in a plan line.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"\bSCAN\b")]
    private static partial Regex ScanPattern();

    /// <summary>
    /// Creates the pattern of a statement parameter.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"\$\w+")]
    private static partial Regex ParameterPattern();

    /// <summary>
    /// Creates the pattern of a search of the kind and time index.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^SEARCH records USING (COVERING )?INDEX records_kind_time ")]
    private static partial Regex KindTimePattern();

    /// <summary>
    /// Creates the pattern of a search of the kind, source and time index by the kind and the source.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^SEARCH records USING (COVERING )?INDEX records_kind_source_time \(kind=\? AND source=\?")]
    private static partial Regex KindSourceTimePattern();

    /// <summary>
    /// Creates the pattern of a search of the records by their ID.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^SEARCH \w+ USING INTEGER PRIMARY KEY \(rowid=\?\)")]
    private static partial Regex RowIdPattern();

    /// <summary>
    /// Creates the pattern of a search of a metric table by the covering index or the primary key.
    /// </summary>
    /// <param name="table">The table</param>
    /// <returns>The pattern</returns>
    private static Regex SearchPattern(string table)
    {
        return table == "metrics"
                   ? new Regex(@"^SEARCH metrics USING (COVERING )?INDEX metrics_source_name_time\b", RegexOptions.None, TimeSpan.FromSeconds(1))
                   : new Regex(@"^SEARCH metric_rollups_1h USING PRIMARY KEY\b", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Reads the lowest ID of a record of a kind captured some minutes from the moment.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="kind">The kind</param>
    /// <param name="minutes">Minutes from <see cref="_moment"/></param>
    /// <returns>A task that returns the ID</returns>
    private async Task<long> IdAtAsync(string directory, string kind, int minutes)
    {
        var text = await TimeSeriesSamples.TextAsync(directory, $"SELECT min(id) FROM records WHERE kind = '{kind}' AND captured_at = {RetentionSamples.Ns(_moment.AddMinutes(minutes))}", TestContext.CancellationToken);

        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Inserts one hourly rollup row directly.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="source">The source</param>
    /// <param name="name">The name</param>
    /// <returns>A task that completes when the row is stored</returns>
    private Task InsertRollupAsync(string directory, string source, string name)
    {
        var sql = "INSERT INTO metric_rollups_1h(source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                  + $"VALUES ('{source}', '{name}', '', {RetentionSamples.Ns(Samples.Base)}, 1, 1, 1, 1, 1, {RetentionSamples.Ns(Samples.Base)}, 1)";

        return RetentionSamples.ExecuteAsync(directory, sql, TestContext.CancellationToken);
    }

    /// <summary>
    /// Explains statements on a store database and returns the plan lines of each.
    /// </summary>
    /// <param name="statements">The names and texts</param>
    /// <returns>A task that returns the plan lines by name</returns>
    private async Task<Dictionary<string, List<string>>> ExplainAsync(List<(string Name, string Sql)> statements)
    {
        using var directory = new TempDirectory();

        await using (var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            await store.PingAsync(TestContext.CancellationToken);
        }

        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory.Path)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        var plans = new Dictionary<string, List<string>>();

        foreach (var (name, sql) in statements)
        {
            plans[name] = await ExplainOneAsync(connection, sql);
        }

        return plans;
    }

    /// <summary>
    /// Explains one statement.
    /// </summary>
    /// <param name="connection">The open connection</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that returns the plan lines</returns>
    private async Task<List<string>> ExplainOneAsync(SqliteConnection connection, string sql)
    {
        var details = new List<string>();

        using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the explained statement is a test-owned query text
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
#pragma warning restore CA2100

        foreach (var parameter in ParameterPattern().Matches(sql).Select(match => match.Value).Distinct())
        {
            command.Parameters.AddWithValue(parameter, 1L);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    #endregion // Methods
}