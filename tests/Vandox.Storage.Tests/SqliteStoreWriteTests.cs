using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for writing batches with <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreWriteTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Metrics, log lines and JSON payloads are stored with the batch context and read back unchanged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchStoresAndReadsBackEveryPayloadStyle()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var batch = Samples.AgentBatch(Samples.Metric(RecordOrigin.Agent, 1, 0, "cpu"), Samples.Log(RecordOrigin.Agent, 2, 1, "hello world"));

        // Act
        var result = await store.WriteBatchAsync(batch, TestContext.CancellationToken);
        var metrics = await store.RecordsAsync(Samples.Query(RecordKind.Metric), TestContext.CancellationToken);
        var logs = await store.RecordsAsync(Samples.Query(RecordKind.LogLine), TestContext.CancellationToken);

        await store.WriteBatchAsync(Samples.Batch(Samples.Gap(2)), TestContext.CancellationToken);

        var gaps = await store.RecordsAsync(Samples.Query(RecordKind.Gap), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(new WriteResult(2, 0), result, "result");

        var metric = (MetricPoint)metrics[0].Record.Data!;

        Assert.AreEqual("cpu", metric.Name, "metric name");
        Assert.AreEqual(1.5, metric.Value, "metric value");
        Assert.AreEqual("percent", metric.Unit, "metric unit");
        Assert.AreEqual("b", metric.Labels!["a"], "metric labels");
        Assert.AreEqual("agent-1", metrics[0].AgentId, "agent ID");
        Assert.AreEqual("0123abcd-0123-0123-0123-0123456789ab", metrics[0].BootId, "boot ID");
        Assert.AreEqual(-5L, metrics[0].ClockOffsetNs, "clock offset");
        Assert.AreEqual(Samples.Base.AddMinutes(1), metrics[0].ReceivedAt, "received at");
        Assert.AreEqual(1UL, metrics[0].Record.Seq, "sequence number");
        Assert.AreEqual(Samples.Base, metrics[0].Record.CapturedAt, "captured at");

        var line = (LogLine)logs[0].Record.Data!;

        Assert.AreEqual("hello world", line.Message, "log message");
        Assert.AreEqual("web-1", line.Host, "log host");
        Assert.AreEqual((byte)3, line.Priority, "log priority");
        Assert.AreEqual(12, line.Pid, "log pid");
        Assert.IsTrue(line.Truncated, "log truncated");
        Assert.AreEqual(GapCause.NoData, ((Gap)gaps[0].Record.Data!).Cause, "JSON payload");
        Assert.AreEqual(string.Empty, gaps[0].AgentId, "no agent ID");
        Assert.IsNull(gaps[0].ClockOffsetNs, "no clock offset");
    }

    /// <summary>
    /// The event of a log line is stored with it and read back, and a line without an event reads back with an empty one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchStoresTheEventOfALogLine()
    {
        // Arrange
        using var directory = new TempDirectory();

        await using (var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            // Act
            var result = await store.WriteBatchAsync(Samples.Batch(Samples.LogWithEvent(RecordOrigin.Backend, 0, 0, "aborted", "mariadb.abort"), Samples.Log(RecordOrigin.Backend, 0, 1, "plain"), Samples.LogWithEvent(RecordOrigin.Backend, 0, 2, "started", "mariadb.start")), TestContext.CancellationToken);
            var lines = await store.RecordsAsync(Samples.Query(RecordKind.LogLine), TestContext.CancellationToken);

            // Assert
            Assert.AreEqual(new WriteResult(3, 0), result, "all records stored");
            Assert.AreSequenceEqual(["mariadb.abort", string.Empty, "mariadb.start"], lines.Select(line => ((LogLine)line.Record.Data!).Event), "events read back in order");
        }

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory.Path, "vandox.db")};Mode=ReadOnly");

        await connection.OpenAsync(TestContext.CancellationToken);

        Assert.AreEqual(1L, await CountAsync(connection, "log_lines WHERE event = 'mariadb.abort'"), "the abort is stored");
        Assert.AreEqual(1L, await CountAsync(connection, "log_lines WHERE event = ''"), "a line without an event stores an empty text, not NULL");
    }

    /// <summary>
    /// Values that are absent are stored as NULL even when the previous record of the batch had a value: the writer reuses its parameters.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchStoresAbsentValuesAsNullAfterPresentOnes()
    {
        // Arrange
        using var directory = new TempDirectory();

        var withoutPriority = Samples.Log(RecordOrigin.Backend, 0, 1, "second");

        ((LogLine)withoutPriority.Data!).Priority = null;

        await using (var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            // Act
            var result = await store.WriteBatchAsync(Samples.Batch(Samples.Log(RecordOrigin.Backend, 0, 0, "first"), withoutPriority, Samples.Metric(RecordOrigin.Backend, 0, 2, "cpu"), Samples.Gap(3), Samples.Metric(RecordOrigin.Backend, 0, 4, "mem")), TestContext.CancellationToken);

            // Assert
            Assert.AreEqual(new WriteResult(5, 0), result, "all records stored");
        }

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory.Path, "vandox.db")};Mode=ReadOnly");

        await connection.OpenAsync(TestContext.CancellationToken);

        Assert.AreEqual(1L, await CountAsync(connection, "log_lines WHERE priority IS NULL"), "log line without priority");
        Assert.AreEqual(1L, await CountAsync(connection, "log_lines WHERE priority IS NOT NULL"), "log line with priority");
        Assert.AreEqual(2L, await CountAsync(connection, "log_lines WHERE host = 'web-1'"), "the host is stored with every log line");
        Assert.AreEqual(5L, await CountAsync(connection, "records WHERE agent_id IS NULL AND boot_id IS NULL AND clock_offset_ns IS NULL AND seq IS NULL"), "no agent, boot, offset and sequence number");
        Assert.AreEqual(4L, await CountAsync(connection, "records WHERE data IS NULL"), "typed records keep no JSON payload");
        Assert.AreEqual(1L, await CountAsync(connection, "records WHERE data IS NOT NULL"), "the gap keeps its JSON payload");
        Assert.AreEqual(1L, await CountAsync(connection, "metrics WHERE name = 'mem' AND labels IS NOT NULL"), "labels of the metric after the gap");
    }

    /// <summary>
    /// The writer checks for cancellation before every record and writes nothing after it.
    /// </summary>
    [TestMethod]
    public void BatchWriterWriteStopsOnCancellation()
    {
        // Arrange
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");

        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var writer = new BatchWriter(connection, transaction);
        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        var batch = Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"));

        // Act and assert
        Assert.ThrowsExactly<OperationCanceledException>(() => writer.Write(batch, batch.Records[0], cancelled.Token), "cancelled write");
    }

    /// <summary>
    /// A resent agent record is counted as a duplicate and never overwrites the stored one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchStoresResentAgentRecordOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var changed = Samples.Metric(RecordOrigin.Agent, 1, 0, "cpu");

        ((MetricPoint)changed.Data!).Value = 99;

        await store.WriteBatchAsync(Samples.AgentBatch(Samples.Metric(RecordOrigin.Agent, 1, 0, "cpu")), TestContext.CancellationToken);

        // Act
        var again = await store.WriteBatchAsync(Samples.AgentBatch(changed, Samples.Metric(RecordOrigin.Agent, 2, 1, "cpu")), TestContext.CancellationToken);
        var stored = await store.RecordsAsync(Samples.Query(RecordKind.Metric), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(new WriteResult(1, 1), again, "one stored, one duplicate");
        Assert.HasCount(2, stored, "two records stored");
        Assert.AreEqual(1.5, ((MetricPoint)stored[0].Record.Data!).Value, "the first record is not overwritten");
    }

    /// <summary>
    /// Records of the same agent ID and sequence number from other origins are not deduplicated.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchDoesNotDeduplicateNonAgentRecords()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var first = await store.WriteBatchAsync(Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu")), TestContext.CancellationToken);
        var second = await store.WriteBatchAsync(Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu")), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, first.Stored, "first batch");
        Assert.AreEqual(1, second.Stored, "second batch");
    }

    /// <summary>
    /// A batch is stored completely or not at all.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchWritesNothingWhenOneRecordFails()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var bad = Samples.Metric(RecordOrigin.Backend, 0, 1, "cpu");

        bad.Source = string.Empty;

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidBatchException>(() => store.WriteBatchAsync(Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"), bad), TestContext.CancellationToken), "invalid record");
        var stored = await store.RecordsAsync(Samples.Query(RecordKind.Metric), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("store: invalid batch: records[1]: model: source: required", exception.Message, "message");
        Assert.IsEmpty(stored, "nothing is stored");
    }

    /// <summary>
    /// Batches that break a rule are refused before anything is written.
    /// </summary>
    /// <param name="what">The rule that is broken</param>
    /// <param name="message">The expected message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("no records", "store: invalid batch: no records")]
    [DataRow("too many records", "store: invalid batch: more than 20000 records")]
    [DataRow("received_at missing", "store: invalid batch: received_at is required")]
    [DataRow("received_at not UTC", "store: invalid batch: received_at must be UTC")]
    [DataRow("received_at out of range", "store: invalid batch: received_at is outside the storable range")]
    [DataRow("bad agent ID", "store: invalid batch: model: agent_id: must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit")]
    [DataRow("agent record without agent ID", "store: invalid batch: records[0]: origin agent needs an agent ID in the batch")]
    [DataRow("captured_at out of range", "store: invalid batch: records[0]: captured_at is outside the storable range")]
    [DataRow("seq out of range", "store: invalid batch: records[0]: seq is above the storable range")]
    [DataRow("import with agent", "store: invalid batch: an import batch has no agent ID")]
    [DataRow("import file ID", "store: invalid batch: import file ID must be positive")]
    [DataRow("import done negative", "store: invalid batch: import done must not be negative")]
    [DataRow("import record from agent", "store: invalid batch: records[0]: origin must be import")]
    [DataRow("import record invalid", "store: invalid batch: records[0]: model: data.name: required")]
    public async Task SqliteStoreWriteBatchRefusesInvalidBatch(string what, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var batch = Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"));

        switch (what)
        {
            case "no records":
                {
                    batch.Records = [];
                }
                break;

            case "too many records":
                {
                    batch.Records = Enumerable.Repeat(batch.Records[0], StorageLimits.MaxBatchRecords + 1).ToList();
                }
                break;

            case "received_at missing":
                {
                    batch.ReceivedAt = default;
                }
                break;

            case "received_at not UTC":
                {
                    batch.ReceivedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(2));
                }
                break;

            case "received_at out of range":
                {
                    batch.ReceivedAt = new DateTimeOffset(1600, 1, 1, 0, 0, 0, TimeSpan.Zero);
                }
                break;

            case "bad agent ID":
                {
                    batch.AgentId = "-bad";
                }
                break;

            case "agent record without agent ID":
                {
                    batch.Records = [Samples.Metric(RecordOrigin.Agent, 1, 0, "cpu")];
                }
                break;

            case "captured_at out of range":
                {
                    batch.Records[0].CapturedAt = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
                }
                break;

            case "seq out of range":
                {
                    batch.AgentId = "agent-1";
                    batch.Records = [Samples.Metric(RecordOrigin.Agent, ulong.MaxValue, 0, "cpu")];
                }
                break;

            default:
                {
                    PrepareImport(batch, what);
                }
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidBatchException>(() => store.WriteBatchAsync(batch, TestContext.CancellationToken), "invalid batch");

        // Assert
        Assert.AreEqual(message, exception.Message, "message");
    }

    /// <summary>
    /// Concurrent writers are serialized.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchSerializesConcurrentWriters()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => store.WriteBatchAsync(Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, index, "cpu"), Samples.Log(RecordOrigin.Backend, 0, index, $"line {index}")), TestContext.CancellationToken)));
        var stored = await store.RecordsAsync(Samples.Query(RecordKind.LogLine), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(20, results.Sum(result => result.Stored) / 2, "every batch stored two records");
        Assert.HasCount(20, stored, "every log line is stored");
    }

    /// <summary>
    /// A cancelled write is abandoned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchHonorsCancellation()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.WriteBatchAsync(Samples.Batch(Samples.Gap(0)), cancelled.Token), "cancelled write");
    }

    /// <summary>
    /// Turns a batch into the import batch the case describes.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <param name="what">The case</param>
    private static void PrepareImport(RecordBatch batch, string what)
    {
        batch.Import = new ImportStep
                       {
                           FileId = 1,
                           Done = 0
                       };
        batch.Records = [Samples.Metric(RecordOrigin.Import, 0, 0, "cpu")];

        switch (what)
        {
            case "import with agent":
                batch.AgentId = "agent-1";
                break;

            case "import file ID":
                batch.Import.FileId = 0;
                break;

            case "import done negative":
                batch.Import.Done = -1;
                break;

            case "import record from agent":
                batch.Records = [Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu")];
                break;

            default:
                ((MetricPoint)batch.Records[0].Data!).Name = string.Empty;
                break;
        }
    }

    /// <summary>
    /// Counts the rows of a table that match a condition.
    /// </summary>
    /// <param name="connection">The open connection</param>
    /// <param name="fromWhere">The table and the condition, after <c>FROM</c></param>
    /// <returns>The number of rows</returns>
    private async Task<long> CountAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string fromWhere)
    {
        await using var command = connection.CreateCommand();

#pragma warning disable CA2100
        command.CommandText = $"SELECT COUNT(*) FROM {fromWhere}";
#pragma warning restore CA2100

        return (long)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    #endregion // Methods
}